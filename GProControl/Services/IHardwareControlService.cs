using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

public interface IHardwareControlService
{
    HardwareControlState State { get; }
    event EventHandler<HardwareControlState>? StateChanged;

    /// <summary>
    /// True once the GameBox WMI proxy has been proven to reach the service.
    /// Device-switch writes stay disabled until then.
    /// </summary>
    bool IsWmiTransportVerified { get; }
    HardwareActionResult? LastWmiProbeResult { get; }
    bool IsWmiProbeRunning { get; }
    Task<HardwareActionResult> EnsureWmiTransportAsync(CancellationToken cancellationToken = default);

    Task<HardwareControlState> RefreshAsync(CancellationToken cancellationToken = default);
    bool SetWriteArmed(bool armed);
    Task<HardwareActionResult> GetPerformanceModeAsync(CancellationToken cancellationToken = default);
    Task<HardwareActionResult> GetWlanAccelerationStatusAsync(CancellationToken cancellationToken = default);
    Task<HardwareActionResult> GetScreenAccelerationStatusAsync(CancellationToken cancellationToken = default);
    Task<ThermalFanReadResult> GetThermalFanSnapshotAsync(CancellationToken cancellationToken = default);
    Task<HardwareActionResult> ProbeWmiTransportAsync(CancellationToken cancellationToken = default);
    Task<HardwareActionResult> SetPerformanceModeAsync(int setterValue, CancellationToken cancellationToken = default);
    Task<HardwareActionResult> SetScreenAccelerationAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<HardwareActionResult> SetDeviceSwitchAsync(MiDeviceSwitch device, bool enabled, CancellationToken cancellationToken = default);
    Task<HardwareActionResult> GetGraphicModeAsync(CancellationToken cancellationToken = default);
    Task<HardwareActionResult> SetGraphicModeAsync(int mode, CancellationToken cancellationToken = default);
}
