namespace GProControl.Models;

public sealed record HardwareControlState(
    string ProductName,
    string? ControlCenterVersion,
    bool IsTargetModel,
    bool IsSupportedVersion,
    bool IsServiceAvailable,
    bool IsWriteArmed,
    string Message)
{
    public bool CanArmWrites => IsTargetModel && IsSupportedVersion && IsServiceAvailable;
    public bool CanWrite => CanArmWrites && IsWriteArmed;

    public static HardwareControlState Initial { get; } = new(
        "检测中", null, false, false, false, false, "正在检查机型与原厂服务…");
}
