namespace GProControl.Models;

/// <summary>
/// Optional discrete-GPU telemetry. Individual metrics stay nullable because a
/// driver may not expose every sensor; the UI renders an em dash instead of a
/// misleading zero. A machine without a supported GPU reports <see langword="null"/>
/// for the whole record.
/// </summary>
public sealed record GpuSnapshot(
    string? Name,
    double? UtilizationPercent,
    int? TemperatureCelsius,
    double? PowerWatts,
    ulong? UsedMemoryBytes,
    ulong? TotalMemoryBytes);

public sealed record SystemSnapshot(
    double CpuUsage,
    ulong UsedMemoryBytes,
    ulong TotalMemoryBytes,
    long UsedDiskBytes,
    long TotalDiskBytes,
    double DownloadKbps,
    double UploadKbps,
    DateTimeOffset CapturedAt,
    GpuSnapshot? Gpu = null);
