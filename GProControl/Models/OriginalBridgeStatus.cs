namespace GProControl.Models;

public sealed record OriginalBridgeStatus(
    bool IsInstalled,
    string? Version,
    string? InstallDirectory,
    bool MiServicePipePresent,
    bool MiDeviceServicePipePresent)
{
    public bool IsTransportAvailable => MiServicePipePresent || MiDeviceServicePipePresent;
}
