using GProControl.Protocol;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public sealed partial class DevicePage : Page
{
    private bool _updating;
    private bool _loaded;
    private bool _switchBusy;
    private readonly Dictionary<MiDeviceSwitch, bool> _confirmed = new();

    public DevicePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        App.HardwareControl.StateChanged += OnHardwareStateChanged;
        var state = await App.HardwareControl.RefreshAsync();
        if (!_loaded)
        {
            return;
        }

        ApplyState();
        if (state.CanArmWrites)
        {
            WmiProbeDetail.Text = "正在自动运行只读自检…";
            WmiProbeButton.IsEnabled = false;
            var result = await App.HardwareControl.EnsureWmiTransportAsync();
            if (!_loaded) return;
            ApplyState();
            WmiProbeDetail.Text = result.Message;
            await RefreshSwitchesAsync();
        }
        else WmiProbeDetail.Text = state.Message;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        App.HardwareControl.StateChanged -= OnHardwareStateChanged;
    }

    private void OnHardwareStateChanged(object? sender, GProControl.Models.HardwareControlState state) =>
        DispatcherQueue.TryEnqueue(() => { if (_loaded) ApplyState(); });

    private void ApplyState()
    {
        var canWrite = App.HardwareControl.State.CanWrite;
        var verified = App.HardwareControl.IsWmiTransportVerified;
        var enabled = canWrite && verified;
        _updating = true;
        FnKeyToggle.IsEnabled = enabled && !_switchBusy;
        WindowsKeyToggle.IsEnabled = enabled && !_switchBusy;
        TouchpadToggle.IsEnabled = enabled && !_switchBusy;
        _updating = false;
        WmiProbeButton.IsEnabled = App.HardwareControl.State.CanArmWrites && !App.HardwareControl.IsWmiProbeRunning;
        if (App.HardwareControl.LastWmiProbeResult is { } result)
            WmiProbeDetail.Text = result.Message;
        DeviceInfo.Severity = verified ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        DeviceInfo.Title = verified ? "设备自检已通过" : "设备自检未通过或正在进行";
        DeviceInfo.Message = verified ? "设备开关已可使用。每次启动自动执行只读自检。"
            : "等待原厂服务并运行只读自检；失败后可点击重新自检。";
    }

    private async Task RefreshSwitchesAsync()
    {
        foreach (var (device, toggle) in new[] { (MiDeviceSwitch.FnKey, FnKeyToggle),
            (MiDeviceSwitch.WindowsKey, WindowsKeyToggle), (MiDeviceSwitch.Touchpad, TouchpadToggle) })
        {
            var result = await App.HardwareControl.GetDeviceSwitchAsync(device);
            if (!_loaded) return;
            if (result is { Verified: true, ReadbackValue: int value })
            {
                var on = device == MiDeviceSwitch.WindowsKey ? value == 1 : value == 0;
                _confirmed[device] = on;
                _updating = true;
                toggle.IsOn = on;
                _updating = false;
            }
            else { toggle.IsEnabled = false; DeviceSwitchStatus.Text = result.Message; }
        }
        if (_confirmed.Count == 3) DeviceSwitchStatus.Text = "开关已与设备当前状态同步。";
    }

    private async void WmiProbeButton_Click(object sender, RoutedEventArgs e)
    {
        WmiProbeButton.IsEnabled = false;
        WmiProbeDetail.Text = "正在发送只读代理请求…";
        var result = await App.HardwareControl.ProbeWmiTransportAsync();
        WmiProbeButton.IsEnabled = true;
        if (!_loaded)
        {
            return;
        }

        ApplyState();
        WmiProbeDetail.Text = result.Message;
        if (result.Verified) await RefreshSwitchesAsync();
        DeviceInfo.Message = result.Verified
            ? "自检通过，开关状态已从设备同步；应用后将再次读回确认。"
            : result.Message;
    }

    private async void DeviceSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating || !_loaded || _switchBusy || sender is not ToggleSwitch toggle)
        {
            return;
        }

        if (!int.TryParse(toggle.Tag?.ToString(), out var deviceValue)
            || !Enum.IsDefined((MiDeviceSwitch)deviceValue))
        {
            return;
        }

        var requested = toggle.IsOn;
        var device = (MiDeviceSwitch)deviceValue;

        // The firmware field is an inverse lock flag for the Windows key: on means
        // "blocked", so the switch (which reads "opened = locked") writes disabled.
        // Fn and touchpad use the same field with the opposite meaning, so their
        // switch value maps straight through.
        var hardwareEnabled = device == MiDeviceSwitch.WindowsKey ? !requested : requested;

        _switchBusy = true;
        ApplyState();
        DeviceSwitchStatus.Text = "正在应用并确认设备状态…";
        var result = await App.HardwareControl.SetDeviceSwitchAsync(device, hardwareEnabled);
        StartupDiagnostics.Write($"Device switch: device={device}, enabled={hardwareEnabled}, verified={result.Verified}, readback={result.ReadbackValue}. {result.Message}");
        _switchBusy = false;
        if (!_loaded)
        {
            return;
        }

        if (!result.Verified)
        {
            _updating = true;
            toggle.IsOn = _confirmed.GetValueOrDefault(device, !requested);
            _updating = false;
            DeviceSwitchStatus.Text = result.Message;
        }
        else
        {
            _confirmed[device] = requested;
            DeviceSwitchStatus.Text = $"已确认：{DescribeIntent(device, requested)}。";
        }

        ApplyState();
    }

    private static string DescribeIntent(MiDeviceSwitch device, bool requested) => device switch
    {
        MiDeviceSwitch.WindowsKey => requested ? "锁定 Windows 键" : "解除 Windows 键锁定",
        MiDeviceSwitch.FnKey => requested ? "启用 Fn 键" : "停用 Fn 键",
        _ => requested ? "启用触控板" : "停用触控板"
    };
}
