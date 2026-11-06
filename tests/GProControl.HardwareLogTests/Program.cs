using System.Globalization;
using GProControl.Services;

// Verifies the incremental MiDeviceService log reader against a synthetic log, so
// the fast readback path can be checked without the vendor service or its DACL.
// Timestamps are explicit so the assertions do not depend on wall-clock races.
var path = Path.Combine(Path.GetTempPath(), $"mi-log-{Guid.NewGuid():N}.txt");
var t0 = new DateTime(2026, 10, 31, 12, 0, 0, 0);
try
{
    Append(path, Line(t0, "operator_manager.cpp 181: HandleGetResult:008000080400000000000000000000000000000000000000000000000000"));
    Append(path, Line(t0, "operator_manager.cpp 243: HandleRegResult:0116030000000000"));

    var reader = new MiDeviceServiceLogReader(path);

    // A record older than the request window must not confirm anything.
    Require(reader.TryReadRecentPerformanceMode(t0.AddSeconds(1)) is null,
        "stale HandleGetResult must not be accepted");
    Require(reader.TryReadRecentPerformanceNotification(t0.AddSeconds(1)) is null,
        "stale HandleRegResult must not be accepted");

    // A fresh read result is picked up incrementally.
    var readAt = t0.AddSeconds(2);
    Append(path, Line(readAt, "operator_manager.cpp 181: HandleGetResult:008000080300000000000000000000000000000000000000000000000000"));
    Require(reader.TryReadRecentPerformanceMode(t0.AddSeconds(1)) == 3,
        "fresh HandleGetResult value must be parsed");

    // A fresh apply notification is picked up too, and must not be confused with
    // the read-result record.
    var applyAt = t0.AddSeconds(4);
    Append(path, Line(applyAt, "operator_manager.cpp 243: HandleRegResult:0116010000000000"));
    Require(reader.TryReadRecentPerformanceNotification(t0.AddSeconds(3)) == 1,
        "fresh HandleRegResult notification must be parsed");

    // Out-of-range notification values are rejected rather than trusted.
    Append(path, Line(t0.AddSeconds(5), "operator_manager.cpp 243: HandleRegResult:0116070000000000"));
    Require(reader.TryReadRecentPerformanceNotification(t0.AddSeconds(3)) == 1,
        "out-of-range notification value must be ignored");

    // EC function 0x0900 returns CPU/GPU fan tachometers followed by the two
    // temperatures. Values here are 4200 RPM, 3900 RPM, 80 C and 55 C.
    var sensorAt = t0.AddSeconds(5);
    Append(path, Line(sensorAt,
        "operator_manager.cpp 181: HandleGetResult:0080000968103C0F00005000000037000000"));
    var sensors = reader.TryReadRecentThermalFanSnapshot(t0.AddSeconds(4));
    Require(sensors is
        {
            CpuTemperatureCelsius: 80,
            GpuTemperatureCelsius: 55,
            CpuFanRpm: 4200,
            GpuFanRpm: 3900
        }, "thermal/fan result fields must be decoded in firmware order");
    Require(reader.TryReadRecentThermalFanSnapshot(t0.AddSeconds(6)) is null,
        "stale thermal/fan result must not be accepted");

    Append(path, Line(t0.AddSeconds(5), "operator_manager.cpp 181: HandleGetResult:0080000B0100000000000000000000000000"));
    Require(reader.TryReadRecentGraphicMode(t0.AddSeconds(4)) == 1,
        "graphic-mode read reply must be accepted case-insensitively");
    Append(path, Line(t0.AddSeconds(6), "operator_manager.cpp 243: HandleRegResult:010d000000000000"));
    Require(reader.TryReadRecentGraphicMode(t0.AddSeconds(4)) == 0,
        "new graphic notification must take precedence over an older read reply");
    Require(reader.TryReadRecentGraphicMode(t0.AddSeconds(7)) is null,
        "stale graphic mode must not confirm a fresh command");

    // Screen acceleration reads the applied state from the NotifyMsg31 record that
    // follows kWmiSetOverDriverStatus.
    Append(path, Line(t0.AddSeconds(6), "WildEngineManager.cpp 313: [WildEngine] WmiIndicate, kWmiSetOverDriverStatus"));
    Append(path, Line(t0.AddSeconds(6), "WildEngineManager.cpp 414: [WildEngine] NotifyMsg30, data:1"));
    Append(path, Line(t0.AddSeconds(6), "WildEngineManager.cpp 414: [WildEngine] NotifyMsg31, data:1"));
    Require(reader.TryReadLatestScreenAccelerationStatus(t0.AddSeconds(5)) == 1,
        "screen acceleration enable must read back as 1");

    Append(path, Line(t0.AddSeconds(8), "WildEngineManager.cpp 313: [WildEngine] WmiIndicate, kWmiSetOverDriverStatus"));
    Append(path, Line(t0.AddSeconds(8), "WildEngineManager.cpp 414: [WildEngine] NotifyMsg30, data:1"));
    Append(path, Line(t0.AddSeconds(8), "WildEngineManager.cpp 414: [WildEngine] NotifyMsg31, data:0"));
    Require(reader.TryReadLatestScreenAccelerationStatus(t0.AddSeconds(7)) == 0,
        "screen acceleration disable must read back as 0");

    // Command type 3 is proven by the exact 32-byte buffer the service executed.
    var execAt = t0.AddSeconds(10);
    Append(path, Line(execAt, "operator_manager.cpp 92: OperatorManager::HandleExecBusiness  info_detail 00fb000a02000100000000000000000000000000000000000000000000000000"));
    Require(reader.HasRecentWmiExecute(t0.AddSeconds(9), "00FB000A0200"),
        "executed WMI buffer must be detected from the service log");
    Require(!reader.HasRecentWmiExecute(t0.AddSeconds(9), "00FA000A0100"),
        "a different WMI buffer must not match");
    Require(!reader.HasRecentWmiExecute(t0.AddSeconds(11), "00FB000A0200"),
        "an execution older than the request window must not match");

    // A wait must return promptly once a matching record lands, not at the deadline.
    var waitFrom = DateTime.Now;
    var waitTask = reader.WaitForPerformanceNotificationAsync(
        waitFrom, TimeSpan.FromSeconds(5), CancellationToken.None);
    Require(!waitTask.IsCompleted, "wait must not complete before the record exists");

    await Task.Delay(120);
    Append(path, Line(DateTime.Now.AddSeconds(1), "operator_manager.cpp 243: HandleRegResult:0116020000000000"));
    var waited = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));
    Require(waited == 2, "wait must return the notification value");

    // The original client logs the 65-byte CMD 1 reply. The app only decodes
    // that retained evidence and never opens or writes the HID device.
    var hidLogPath = Path.Combine(Path.GetTempPath(), $"xgcc-hid-{Guid.NewGuid():N}.txt");
    try
    {
        var enabledFrame = "0057AB010101020003030000B300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
        var disabledFrame = "0057AB0101FF000003000000B000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
        Append(hidLogPath, Line(t0, $"b21_payload_writer.cpp 598: [HID] KeyBoard1ByteCmd return data: {enabledFrame}"));
        Append(hidLogPath, Line(t0.AddSeconds(1), $"b21_payload_writer.cpp 598: [HID] KeyBoard1ByteCmd return data: {disabledFrame}"));

        var lightingReader = new XgccHidLogReader(hidLogPath);
        var disabledState = lightingReader.TryReadLatest();
        Require(disabledState is { IsEnabled: false, EffectCode: 0xFF, LedAreaMask: 0x0B, StatusNibble: 0 },
            "latest disabled CMD 1 reply must be decoded");

        Append(hidLogPath, Line(t0.AddSeconds(2), $"b21_payload_writer.cpp 598: [HID] KeyBoard1ByteCmd return data: {enabledFrame}"));
        var enabledState = lightingReader.TryReadLatest();
        Require(enabledState is
            {
                IsEnabled: true,
                EffectCode: 1,
                PaletteCode: 2,
                DirectionCode: 0,
                BrightnessCode: 3,
                SpeedCode: 3,
                LedAreaMask: 0x0B,
                StatusNibble: 3
            }, "enabled CMD 1 fields must match the target capture");
    }
    finally
    {
        try
        {
            File.Delete(hidLogPath);
        }
        catch (IOException)
        {
        }
    }

    var settingsPath = Path.Combine(Path.GetTempPath(), $"lighting-settings-{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(settingsPath, "{\"LastPerformanceServiceValue\":3}");
        var settings = new AppSettingsStore(settingsPath);
        Require(settings.Current.RestoreLighting && settings.Current.LastWaveLighting is null,
            "legacy settings must not invent a saved lighting profile");
        Require(settings.Current.CloseAction == CloseAction.Ask && !settings.Current.StartInBackground,
            "legacy settings must default to asking on close and showing the window on startup");
        settings.Update(value =>
        {
            value.LastWaveLighting = new GProControl.Models.WaveLightingProfile(2, 0, 3, 3);
            value.LastLightingEnabled = false;
        });
        var reloaded = new AppSettingsStore(settingsPath);
        Require(reloaded.Current.LastLightingEnabled == false
            && reloaded.Current.LastWaveLighting is { IsValidated: true, PaletteCode: 2, SpeedCode: 3 }
            && reloaded.Current.LastPerformanceServiceValue == 3,
            "disabled power and its wave profile must survive restart without losing performance settings");
        Require(!new GProControl.Models.WaveLightingProfile(1, 0, 3, 3).IsValidated,
            "uncaptured palette must never be eligible for automatic restore");
        reloaded.Update(value => value.RestoreLighting = false);
        Require(!new AppSettingsStore(settingsPath).Current.RestoreLighting,
            "disabling automatic lighting restore must persist");
        reloaded.Update(value => { value.CloseAction = CloseAction.Background; value.StartInBackground = true; });
        var backgroundSettings = new AppSettingsStore(settingsPath).Current;
        Require(backgroundSettings.CloseAction == CloseAction.Background && backgroundSettings.StartInBackground,
            "remembered background close action and background startup preference must survive reload");
        reloaded.Update(value => value.CloseAction = CloseAction.Ask);
        Require(new AppSettingsStore(settingsPath).Current.CloseAction == CloseAction.Ask,
            "users must be able to re-enable the close prompt");
    }
    finally
    {
        File.Delete(settingsPath);
    }

    Console.WriteLine("MiDeviceService log reader and lighting settings tests passed.");
    return 0;
}
finally
{
    try
    {
        File.Delete(path);
    }
    catch (IOException)
    {
    }
}

static string Line(DateTime timestamp, string message) =>
    $"{timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture)} INFO 1234 {message}";

static void Append(string path, string line)
{
    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    using var writer = new StreamWriter(stream);
    writer.Write(line);
    writer.Write('\n');
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"FAILED: {message}");
        Environment.Exit(1);
    }
}
