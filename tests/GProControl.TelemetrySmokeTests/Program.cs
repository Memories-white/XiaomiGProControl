using GProControl.Services;

// Headless check that the monitor pipeline produces sane values and that the
// NVIDIA telemetry degrades to null rather than throwing on a machine without
// a supported GPU.
var monitor = new WindowsSystemMonitorService();
byte[] buffer = new byte[256 * 1024];
for (var index = 0; index < 120; index++)
{
    buffer[index] = (byte)(index & 0xff);
}

var first = monitor.Capture();
System.Threading.Thread.Sleep(600);
var second = monitor.Capture();

Console.WriteLine($"cpu={second.CpuUsage:0.0}% memory={ToGiB(second.UsedMemoryBytes):0.0}/{ToGiB(second.TotalMemoryBytes):0.0} GB");
Console.WriteLine($"disk={ToGiB((ulong)second.UsedDiskBytes):0}/{ToGiB((ulong)second.TotalDiskBytes):0} GB");
Console.WriteLine($"net down={second.DownloadKbps:0} up={second.UploadKbps:0} Kbps");

Require(second.TotalMemoryBytes > 0, "total memory must be reported");
Require(second.UsedMemoryBytes <= second.TotalMemoryBytes, "used memory cannot exceed total");
Require(second.CpuUsage is >= 0 and <= 100, "cpu usage must be a percentage");
Require(second.TotalDiskBytes > 0, "system disk must be reported");
Require(second.CapturedAt >= first.CapturedAt, "timestamps must advance");

var gpu = second.Gpu;
if (gpu is null)
{
    Console.WriteLine("gpu: not available (no supported NVIDIA telemetry)");
}
else
{
    Console.WriteLine($"gpu={gpu.Name ?? "unknown"} util={Format(gpu.UtilizationPercent, "0")}% temp={FormatInt(gpu.TemperatureCelsius, "0")} C power={Format(gpu.PowerWatts, "0.0")} W vram={FormatVram(gpu)}");
    Require(gpu.UtilizationPercent is null or >= 0 and <= 100, "gpu utilization must be a percentage");
    Require(gpu.TemperatureCelsius is null or > 0 and < 130, "gpu temperature must be plausible");
    Require(gpu.PowerWatts is null or >= 0 and < 1000, "gpu power must be plausible");
    Require(gpu.UsedMemoryBytes is null or > 0, "gpu used memory must be positive when reported");
    Require(gpu.TotalMemoryBytes is null or > 0, "gpu total memory must be positive when reported");
    if (gpu is { UsedMemoryBytes: ulong used, TotalMemoryBytes: ulong total })
    {
        Require(used <= total, "gpu used memory cannot exceed total");
    }
}

Console.WriteLine("System monitor smoke tests passed.");
return 0;

static string Format(Double? value, string format) => value?.ToString(format) ?? "-";
static string FormatInt(int? value, string format) => value?.ToString(format) ?? "-";
static string FormatVram(GProControl.Models.GpuSnapshot gpu) =>
    gpu is { UsedMemoryBytes: ulong used, TotalMemoryBytes: ulong total }
        ? $"{ToGiB(used):0.0}/{ToGiB(total):0.0} GB"
        : "-";
static double ToGiB(ulong bytes) => bytes / 1024d / 1024d / 1024d;
static void Require(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"FAILED: {message}");
        Environment.Exit(1);
    }
}
