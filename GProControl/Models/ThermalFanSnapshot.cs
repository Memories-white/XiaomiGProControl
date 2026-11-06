namespace GProControl.Models;

public sealed record ThermalFanSnapshot(
    int CpuTemperatureCelsius,
    int GpuTemperatureCelsius,
    int CpuFanRpm,
    int GpuFanRpm,
    DateTime ObservedAt);

public sealed record ThermalFanReadResult(
    bool Submitted,
    bool Verified,
    string Message,
    ThermalFanSnapshot? Snapshot = null)
{
    public static ThermalFanReadResult Rejected(string message) =>
        new(false, false, message);
}
