using GProControl.Models;
using GProControl.Services;
using GProControl.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GProControl.Views;

public sealed partial class DashboardPage : Page
{
    internal const double RogStackedBelow = 680;
    internal const double LegionStackedBelow = 770;

    private readonly ISystemMonitorService _monitor = new WindowsSystemMonitorService();
    private readonly OriginalControlCenterProbe _bridgeProbe = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _bridgeCaptured;
    private bool _sensorRefreshRunning;
    private int _zoneModeIndex = -1;
    private AppAppearance _appearance;

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
        if (App.MainWindow is { } window)
        {
            window.AppearanceChanged += OnAppearanceChanged;
            ApplyAppearance(window.ActiveAppearance);
        }
        Refresh();
        _timer.Start();
        await App.StartupPerformanceRestore;
        if (!IsLoaded) return;

        var state = await App.HardwareControl.RefreshAsync();
        if (!IsLoaded) return;
        if (state.CanArmWrites)
        {
            var result = await App.HardwareControl.GetPerformanceModeAsync();
            if (!IsLoaded) return;
            if (result.Verified && result.ReadbackValue is int ecValue)
            {
                var index = PerformanceModeCatalog.IndexOfEc(ecValue);
                _zoneModeIndex = index;
                UpdateZoneMode();
                DashboardModeText.Text = index >= 0
                    ? $"{PerformanceModeCatalog.All[index].Name} · EC {ecValue}"
                    : $"未知模式 · EC {ecValue}";
            }
            else
            {
                DashboardModeText.Text = "暂未读取到模式";
            }

            await RefreshThermalFanAsync();
            if (IsLoaded) _sensorTimer.Start();
        }
        else
        {
            DashboardModeText.Text = "原厂服务未就绪";
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is { } window)
            window.AppearanceChanged -= OnAppearanceChanged;
        _timer.Stop();
        _sensorTimer.Stop();
    }

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        if (sender is MainWindow window)
            ApplyAppearance(window.ActiveAppearance);
    }

    private void ApplyAppearance(AppAppearance appearance)
    {
        _appearance = appearance;
        if (appearance == AppAppearance.Rog)
            _ = FindName(nameof(RogDashboard));
        else if (appearance == AppAppearance.Legion)
        {
            _ = FindName(nameof(LegionDashboard));
            UpdateZoneMode();
        }

        if (RogDashboard is { } rog)
            rog.Visibility = appearance == AppAppearance.Rog ? Visibility.Visible : Visibility.Collapsed;
        if (LegionDashboard is { } legion)
            legion.Visibility = appearance == AppAppearance.Legion ? Visibility.Visible : Visibility.Collapsed;
        ClassicDashboard.Visibility = appearance == AppAppearance.Windows ? Visibility.Visible : Visibility.Collapsed;
        LegionOverviewBackdrop.Visibility = appearance == AppAppearance.Legion ? Visibility.Visible : Visibility.Collapsed;
        if (appearance == AppAppearance.Legion)
            DashboardScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        else
            DashboardScroller.ClearValue(ScrollViewer.VerticalScrollBarVisibilityProperty);
        UpdateDashboardViewport(DashboardScroller.ActualWidth, DashboardScroller.ActualHeight);
    }

    private void DashboardScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDashboardViewport(e.NewSize.Width, e.NewSize.Height);

    private void UpdateDashboardViewport(double width, double height)
    {
        bool themed = _appearance != AppAppearance.Windows;
        // Leave room for the viewport and Legion content to round independently at fractional DPI.
        DashboardLayoutRoot.MinHeight = _appearance switch
        {
            AppAppearance.Legion => Math.Max(0, height - 1),
            AppAppearance.Rog => height,
            _ => 0
        };
        if (!themed || width <= 0) return;

        // Let tall, wide windows use slightly larger instruments and section spacing.
        // The dashboard can still measure taller than the viewport and scroll normally.
        double extra = width >= 800 ? Math.Clamp((height - 620) * 0.16, 0, 34) : 0;
        if (_appearance == AppAppearance.Rog && RogDashboard is { } rog)
        {
            rog.Width = Math.Min(width, rog.MaxWidth);
            rog.RowSpacing = 22 + extra * 0.4;
            RogDeviceArt.MaxHeight = 176 + extra;
        }
        else if (LegionDashboard is { } legion)
        {
            legion.Width = Math.Min(width, legion.MaxWidth);
            legion.RowSpacing = 15 + extra * 0.3;
            ZoneCpuGauge.MaxHeight = ZoneGpuGauge.MaxHeight = 168 + extra;
        }
    }

    private void UpdateZoneMode()
    {
        if (ZoneModeStrip is not { } strip) return;

        ZoneModeCaption.Text = _zoneModeIndex >= 0
            ? PerformanceModeCatalog.All[_zoneModeIndex].Name
            : "未确认";
        foreach (var label in strip.Children.OfType<TextBlock>())
        {
            bool selected = Grid.GetColumn(label) == _zoneModeIndex;
            label.Opacity = selected ? 1 : 0.57;
            label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private void LegionDashboard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < LegionStackedBelow;
        Grid.SetColumnSpan(LegionMonitor, stacked ? 2 : 1);
        Grid.SetColumnSpan(LegionTools, stacked ? 2 : 1);
        Grid.SetColumn(LegionQuickSettings, stacked ? 0 : 1);
        Grid.SetColumnSpan(LegionQuickSettings, stacked ? 2 : 1);
        Grid.SetRow(LegionQuickSettings, stacked ? 2 : 1);
        Grid.SetRowSpan(LegionQuickSettings, stacked ? 1 : 2);
        Grid.SetRow(LegionTools, stacked ? 3 : 2);
    }

    private void RogDashboard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < RogStackedBelow;
        Grid.SetColumnSpan(RogDeviceSummary, stacked ? 2 : 1);
        Grid.SetColumnSpan(RogDeviceLinks, stacked ? 2 : 1);
        Grid.SetColumn(RogMonitor, stacked ? 0 : 1);
        Grid.SetColumnSpan(RogMonitor, stacked ? 2 : 1);
        Grid.SetRow(RogMonitor, stacked ? 2 : 1);
        Grid.SetRowSpan(RogMonitor, stacked ? 1 : 2);
        Grid.SetRow(RogDeviceLinks, stacked ? 3 : 2);
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
        var showMegabytes = App.Settings.Current.ShowNetworkSpeedInMegabytes;
        DownloadText.Text = FormatNetworkSpeed(snapshot.DownloadKbps, showMegabytes);
        UploadText.Text = FormatNetworkSpeed(snapshot.UploadKbps, showMegabytes);
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

    private string ZonePercent(double percent) => $"{percent:0}%";

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

    private static string FormatNetworkSpeed(double kilobitsPerSecond, bool showMegabytes) =>
        showMegabytes ? $"{kilobitsPerSecond / 8000d:0.00#} MB/s" : $"{kilobitsPerSecond:0} Kbps";

    private void OpenPerformance_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateTo("performance");
    }
    private void OpenDetails_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("details", asSubpage: true);
    private void OpenDevice_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("device");
    private void OpenLighting_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("lighting");
    private void OpenSupport_Click(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("support");
}
