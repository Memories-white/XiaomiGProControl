namespace GProControl.Models;

public sealed record HardwareActionResult(
    bool Submitted,
    bool Verified,
    string Message,
    int? ReadbackValue = null)
{
    public static HardwareActionResult Rejected(string message) => new(false, false, message);
}
