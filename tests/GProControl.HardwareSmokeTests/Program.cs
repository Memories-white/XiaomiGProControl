using GProControl.Protocol;
using GProControl.Services;

var restoreOnly = args is ["--restore-full-speed"];
var screenAcceleration = args is ["--screen-acceleration"];
var sensorRead = args.Length >= 1 && args[0].Equals("--sensor-read", StringComparison.OrdinalIgnoreCase);
var deviceCheck = args.Length >= 1 && args[0].Equals("--device-check", StringComparison.OrdinalIgnoreCase);
var keyRoundtrip = args.Length >= 1 && args[0].Equals("--windows-key-roundtrip", StringComparison.OrdinalIgnoreCase);
var outputIndex = Array.FindIndex(args, value => value.Equals("--output", StringComparison.OrdinalIgnoreCase));
var outputPath = outputIndex >= 0 && outputIndex + 1 < args.Length ? args[outputIndex + 1] : null;
var targetServiceValue = -1;
var hasTarget = args is ["--target-service-value", var rawTarget]
    && int.TryParse(rawTarget, out targetServiceValue);
if (!restoreOnly && !hasTarget && !screenAcceleration && !sensorRead && !deviceCheck && !keyRoundtrip)
{
    Console.Error.WriteLine("Usage: GProControl.HardwareSmokeTests --restore-full-speed | --target-service-value <0|1|2> | --screen-acceleration | --sensor-read [--output <path>]");
    return 64;
}

var target = PerformanceModeCatalog.FindByServiceValue(targetServiceValue);
if (!restoreOnly && !screenAcceleration && !sensorRead && !deviceCheck && !keyRoundtrip && (target is null || target.Value.ServiceValue == 3))
{
    Console.Error.WriteLine("Only reversible non-full-speed target values 0, 1, and 2 are accepted.");
    return 64;
}

var fullSpeed = PerformanceModeCatalog.FindByServiceValue(3)!.Value;
var hardware = new XiaomiHardwareControlService();
var state = await hardware.RefreshAsync();
Console.WriteLine($"Guard: model={state.ProductName}; original={state.ControlCenterVersion}; canArm={state.CanArmWrites}");
if (!state.CanArmWrites || !hardware.SetWriteArmed(true))
{
    Console.Error.WriteLine($"Hardware guard rejected the test: {state.Message}");
    return 2;
}

if (keyRoundtrip)
{
    var probe = await hardware.EnsureWmiTransportAsync();
    var before = await hardware.GetDeviceSwitchAsync(MiDeviceSwitch.WindowsKey);
    if (!probe.Verified || before.ReadbackValue is not int original) return 10;
    var lines = new List<string>();
    try
    {
        foreach (var enabled in new[] { false, true })
        {
            var changed = await hardware.SetDeviceSwitchAsync(MiDeviceSwitch.WindowsKey, enabled);
            var actual = await hardware.GetDeviceSwitchAsync(MiDeviceSwitch.WindowsKey);
            lines.Add($"enabled={enabled}; verified={changed.Verified}; raw={actual.ReadbackValue}");
            if (!changed.Verified || actual.ReadbackValue != (enabled ? 0 : 1)) return 11;
        }
    }
    finally
    {
        var restored = await hardware.SetDeviceSwitchAsync(MiDeviceSwitch.WindowsKey, original == 0);
        lines.Add($"restored={restored.Verified}; original={original}");
        if (!string.IsNullOrWhiteSpace(outputPath)) File.WriteAllLines(outputPath, lines);
        foreach(var line in lines) Console.WriteLine(line);
    }
    return 0;
}

if (deviceCheck)
{
    var firstTask = hardware.EnsureWmiTransportAsync();
    var concurrentTask = hardware.EnsureWmiTransportAsync();
    var first = await firstTask;
    var cached = await hardware.EnsureWmiTransportAsync();
    var graphic = await hardware.GetGraphicModeAsync();
    var duplicate = graphic is { Verified: true, ReadbackValue: int mode }
        ? await hardware.SetGraphicModeAsync(mode)
        : GProControl.Models.HardwareActionResult.Rejected("No mode readback.");
    var invalid = await hardware.SetGraphicModeAsync(2);
    var noWrite = duplicate.Verified && !duplicate.Submitted && !invalid.Submitted && !invalid.Verified;
    var summary = $"selftest={first.Verified}; sharedTask={ReferenceEquals(firstTask, concurrentTask)}; " +
        $"cachedResult={ReferenceEquals(first, cached)}; graphicVerified={graphic.Verified}; graphicMode={graphic.ReadbackValue}; noWriteGuards={noWrite}; " +
        $"{first.Message} {graphic.Message}";
    Console.WriteLine(summary);
    if (!string.IsNullOrWhiteSpace(outputPath)) File.WriteAllText(Path.GetFullPath(outputPath), summary);
    return first.Verified && ReferenceEquals(firstTask, concurrentTask)
        && ReferenceEquals(first, cached) && graphic.Verified && noWrite ? 0 : 9;
}

if (sensorRead)
{
    var result = await hardware.GetThermalFanSnapshotAsync();
    if (!result.Verified)
    {
        var probe = await hardware.ProbeWmiTransportAsync();
        if (probe.Verified)
        {
            result = await hardware.GetThermalFanSnapshotAsync();
        }
    }
    var summary = result.Snapshot is { } snapshot
        ? $"submitted={result.Submitted}; verified={result.Verified}; " +
          $"cpuTemp={snapshot.CpuTemperatureCelsius}; gpuTemp={snapshot.GpuTemperatureCelsius}; " +
          $"cpuFan={snapshot.CpuFanRpm}; gpuFan={snapshot.GpuFanRpm}; {result.Message}"
        : $"submitted={result.Submitted}; verified={result.Verified}; {result.Message}";
    Console.WriteLine(summary);
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        File.WriteAllText(Path.GetFullPath(outputPath), summary + Environment.NewLine);
    }

    return result.Verified ? 0 : 8;
}

if (screenAcceleration)
{
    var screenExitCode = 0;
    try
    {
        var enabled = await hardware.SetScreenAccelerationAsync(true);
        PrintResult("Enable screen acceleration", enabled);
        if (!enabled.Submitted)
        {
            screenExitCode = 6;
        }

        await Task.Delay(500);
    }
    finally
    {
        var disabled = await hardware.SetScreenAccelerationAsync(false);
        PrintResult("Restore screen acceleration off", disabled);
        if (!disabled.Submitted)
        {
            screenExitCode = 7;
        }
    }

    return screenExitCode;
}

var initial = await hardware.GetPerformanceModeAsync();
PrintResult("Initial", initial);
if (restoreOnly)
{
    if (!initial.Verified || initial.ReadbackValue is not (1 or 2 or 3 or 4))
    {
        Console.Error.WriteLine("Current EC mode is not a recognized reversible state; no write was attempted.");
        return 3;
    }

    var restored = await hardware.SetPerformanceModeAsync(fullSpeed.ServiceValue);
    PrintResult("Restore 狂暴", restored);
    return restored.Verified && restored.ReadbackValue == fullSpeed.EcValue ? 0 : 5;
}

if (!initial.Verified || initial.ReadbackValue != fullSpeed.EcValue)
{
    Console.Error.WriteLine("Initial mode is not confirmed full-speed EC 4; no write was attempted.");
    return 3;
}

var selectedTarget = target!.Value;
var exitCode = 0;
try
{
    var applied = await hardware.SetPerformanceModeAsync(selectedTarget.ServiceValue);
    PrintResult($"Apply {selectedTarget.Name}", applied);
    if (!applied.Verified || applied.ReadbackValue != selectedTarget.EcValue)
    {
        Console.Error.WriteLine("Target mode was not confirmed.");
        exitCode = 4;
    }
}
finally
{
    var restored = await hardware.SetPerformanceModeAsync(fullSpeed.ServiceValue);
    PrintResult("Restore 狂暴", restored);
    if (!restored.Verified || restored.ReadbackValue != fullSpeed.EcValue)
    {
        Console.Error.WriteLine("CRITICAL: final full-speed EC 4 restoration was not confirmed.");
        exitCode = 5;
    }
}

return exitCode;

static void PrintResult(string label, GProControl.Models.HardwareActionResult result) =>
    Console.WriteLine($"{label}: submitted={result.Submitted}; verified={result.Verified}; ec={result.ReadbackValue?.ToString() ?? "none"}; {result.Message}");
