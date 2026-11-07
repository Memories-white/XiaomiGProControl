using GProControl.Models;
using GProControl.Services;
using GProControl.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GProControl.Views;

public sealed partial class DashboardPage : Page
{
    private readonly ISystemMonitorService _monitor = new WindowsSystemMonitorService();
    private readonly OriginalControlCenterProbe _bridgeProbe = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _bridgeCaptured;
    private bool _sensorRefreshRunning;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _timer.Tick += (_, _) => Refresh();
        _sensorTimer.Tick += async (_, _) => await RefreshThermalFanAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Refresh();
        _timer.Start();
        var state = await App.HardwareControl.RefreshAsync();
        if (state.CanArmWrites)
        {
            var result = await App.HardwareControl.GetPerformanceModeAsync();
            if (result.Verified && result.ReadbackValue is int ecValue)
            {
                var index = PerformanceModeCatalog.IndexOfEc(ecValue);
                DashboardModeText.Text = index >= 0
                    ? $"{PerformanceModeCatalog.All[index].Name} · EC {ecValue}"
                    : $"未知模式 · EC {ecValue}";
            }
            else
            {
                DashboardModeText.Text = "暂未读取到模式";
            }

            await RefreshThermalFanAsync();
            _sensorTimer.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _sensorTimer.Stop();
    }

    private async void RefreshThermalFan_Click(object sender, RoutedEventArgs e) =>
        await RefreshThermalFanAsync();

    private async Task RefreshThermalFanAsync()
    {
        if (_sensorRefreshRunning || !App.HardwareControl.State.CanArmWrites)
        {
            return;
        }

        _sensorRefreshRunning = true;
        RefreshThermalFanButton.IsEnabled = false;
        try
        {
            var result = await App.HardwareControl.GetThermalFanSnapshotAsync();
            if (!result.Verified && !App.HardwareControl.IsWmiTransportVerified)
            {
                var probe = await App.HardwareControl.EnsureWmiTransportAsync();
                if (probe.Verified)
                {
                    result = await App.HardwareControl.GetThermalFanSnapshotAsync();
                }
            }
            if (result.Verified && result.Snapshot is { } snapshot)
            {
                CpuTemperatureText.Text = $"{snapshot.CpuTemperatureCelsius} °C";
                CpuFanText.Text = $"{snapshot.CpuFanRpm} RPM";
                GpuFanText.Text = $"{snapshot.GpuFanRpm} RPM";
                RefreshThermalFanButton.Content = $"EC · {snapshot.ObservedAt:HH:mm:ss}";
            }
            else
            {
                CpuTemperatureText.Text = "暂不可用";
                CpuFanText.Text = "—";
                GpuFanText.Text = "—";
                RefreshThermalFanButton.Content = "重试传感器";
            }
        }
        finally
        {
            RefreshThermalFanButton.IsEnabled = true;
            _sensorRefreshRunning = false;
        }
    }

    private void Refresh()
    {
        var snapshot = _monitor.Capture();
        CpuUsageText.Text = $"{snapshot.CpuUsage:0}%";
        CpuUsageBar.Value = snapshot.CpuUsage;
        var usedMemory = ToGiB(snapshot.UsedMemoryBytes);
        var totalMemory = ToGiB(snapshot.TotalMemoryBytes);
        MemoryText.Text = $"{usedMemory:0.0} / {totalMemory:0.0} GB";
        MemoryBar.Value = snapshot.TotalMemoryBytes == 0 ? 0 : snapshot.UsedMemoryBytes * 100d / snapshot.TotalMemoryBytes;
        var usedDisk = ToGiB((ulong)snapshot.UsedDiskBytes);
        var totalDisk = ToGiB((ulong)snapshot.TotalDiskBytes);
        DiskText.Text = $"{usedDisk:0} / {totalDisk:0} GB";
        DiskBar.Value = snapshot.TotalDiskBytes == 0 ? 0 : snapshot.UsedDiskBytes * 100d / snapshot.TotalDiskBytes;
        DownloadText.Text = $"{snapshot.DownloadKbps:0} Kbps";
        UploadText.Text = $"{snapshot.UploadKbps:0} Kbps";
        UpdateGpu(snapshot.Gpu);

        if (!_bridgeCaptured)
        {
            CaptureBridgeStatus();
            _bridgeCaptured = true;
        }
    }

    /// <summary>
    /// Renders the optional NVML telemetry. Missing sensors keep an em dash so a
    /// driver that does not expose one metric is never displayed as zero.
    /// </summary>
    private void UpdateGpu(GpuSnapshot? gpu)
    {
        if (gpu is null)
        {
            GpuUsageText.Text = "--%";
            GpuNameText.Text = "未检测到独立显卡";
            GpuUsageBar.Value = 0;
            GpuTemperatureText.Text = "—";
            GpuPowerText.Text = "—";
            GpuMemoryText.Text = "—";
            return;
        }

        GpuUsageText.Text = gpu.UtilizationPercent is double utilization ? $"{utilization:0}%" : "--%";
        GpuUsageBar.Value = gpu.UtilizationPercent ?? 0;
        GpuNameText.Text = string.IsNullOrWhiteSpace(gpu.Name)
            ? "独立显卡"
            : gpu.Name.Replace("NVIDIA ", string.Empty, StringComparison.Ordinal);
        GpuTemperatureText.Text = gpu.TemperatureCelsius is int temperature ? $"{temperature} °C" : "—";
        GpuPowerText.Text = gpu.PowerWatts is double watts ? $"{watts:0.0} W" : "—";
        GpuMemoryText.Text = gpu is { UsedMemoryBytes: ulong used, TotalMemoryBytes: ulong total } && total > 0
            ? $"{ToGiB(used):0.0}/{ToGiB(total):0.0} GB"
            : "—";
    }

    private void CaptureBridgeStatus()
    {
        var status = _bridgeProbe.Capture();
        if (!status.IsInstalled)
        {
            BridgeStatusText.Text = "未安装";
            BridgeDetailText.Text = "未找到 Xiaomi G Command Center；硬件控制保持禁用。";
            return;
        }

        if (status.IsTransportAvailable)
        {
            BridgeStatusText.Text = "服务可用";
            BridgeStatusDot.Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            BridgeDetailText.Text = $"检测到原厂组件 {status.Version} 与后台通信管道；已验证控制默认启用。";
            return;
        }

        BridgeStatusText.Text = "服务离线";
        BridgeDetailText.Text = $"检测到原厂组件 {status.Version}，但后台通信管道当前不可用。";
    }

    private static double ToGiB(ulong bytes) => bytes / 1024d / 1024d / 1024d;

    private void OpenPerformance_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateTo("performance");
    }
    private void OpenDetails_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("details", asSubpage: true);
}
