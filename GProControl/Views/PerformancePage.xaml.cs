using GProControl.Models;
using GProControl.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public sealed partial class PerformancePage : Page
{
    private bool _updating;
    private bool _loaded;

    private RadioButton[] ModeButtons =>
        [SilentModeButton, BalancedModeButton, TurboModeButton, FullSpeedModeButton];

    public PerformancePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        App.HardwareControl.StateChanged += OnHardwareStateChanged;
        _updating = true;
        ScreenAccelerationToggle.IsOn = App.Settings.Current.ScreenAccelerationEnabled;
        _updating = false;
        ApplyState(await App.HardwareControl.RefreshAsync());
        await RefreshModeAsync();
        await RefreshWlanAsync();
        await RefreshScreenAccelerationAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        App.HardwareControl.StateChanged -= OnHardwareStateChanged;
    }

    private void OnHardwareStateChanged(object? sender, HardwareControlState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyState(state));

    private void ApplyState(HardwareControlState state)
    {
        SetModeButtonsEnabled(state.CanWrite);
        ScreenAccelerationToggle.IsEnabled = state.CanWrite;
        RefreshModeButton.IsEnabled = state.CanArmWrites;
        RefreshScreenAccelerationButton.IsEnabled = state.CanArmWrites;
        ControlInfo.Title = state.CanWrite ? "硬件写入已解锁" : "硬件写入已锁定";
        ControlInfo.Message = state.Message;
        ControlInfo.Severity = state.CanWrite ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
    }

    private async void RefreshModeButton_Click(object sender, RoutedEventArgs e) => await RefreshModeAsync();

    private async void RefreshWlanButton_Click(object sender, RoutedEventArgs e) => await RefreshWlanAsync();

    private async void RefreshScreenAccelerationButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshScreenAccelerationAsync();

    private async Task RefreshScreenAccelerationAsync()
    {
        if (!_loaded || !App.HardwareControl.State.CanArmWrites)
        {
            ScreenAccelerationStatus.Text = "原厂服务当前不可用于回读屏幕加速状态。";
            return;
        }

        RefreshScreenAccelerationButton.IsEnabled = false;
        var result = await App.HardwareControl.GetScreenAccelerationStatusAsync();
        RefreshScreenAccelerationButton.IsEnabled = true;
        if (!_loaded)
        {
            return;
        }

        ScreenAccelerationStatus.Text = result.Message;
        if (result.Verified && result.ReadbackValue is int applied)
        {
            var enabled = applied == 1;
            _updating = true;
            ScreenAccelerationToggle.IsOn = enabled;
            _updating = false;
            App.Settings.Update(value => value.ScreenAccelerationEnabled = enabled);
        }
    }

    private async Task RefreshWlanAsync()
    {
        if (!_loaded || !App.HardwareControl.State.CanArmWrites)
        {
            WlanStatusText.Text = "原厂服务当前不可用于读取 WLAN 状态。";
            return;
        }

        RefreshWlanButton.IsEnabled = false;
        var result = await App.HardwareControl.GetWlanAccelerationStatusAsync();
        RefreshWlanButton.IsEnabled = true;
        if (_loaded)
        {
            WlanStatusText.Text = result.Message;
        }
    }

    private async Task RefreshModeAsync()
    {
        if (!_loaded || !App.HardwareControl.State.CanArmWrites)
        {
            CurrentModeText.Text = "原厂服务当前不可用于读取模式。";
            return;
        }

        RefreshModeButton.IsEnabled = false;
        var result = await App.HardwareControl.GetPerformanceModeAsync();
        RefreshModeButton.IsEnabled = true;
        if (!_loaded)
        {
            return;
        }

        if (!result.Verified || result.ReadbackValue is null)
        {
            SetSelectedIndex(-1);
            CurrentModeText.Text = result.Message;
            return;
        }

        var index = PerformanceModeCatalog.IndexOfEc(result.ReadbackValue.Value);
        SetSelectedIndex(index);
        if (index >= 0 && App.Settings.Current.LastPerformanceServiceValue is null)
        {
            App.Settings.Update(value => value.LastPerformanceServiceValue = PerformanceModeCatalog.All[index].ServiceValue);
        }
        CurrentModeText.Text = index >= 0
            ? $"当前：{PerformanceModeCatalog.All[index].Name}（EC {result.ReadbackValue}）"
            : $"读取到未识别的 EC 模式 {result.ReadbackValue}";
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updating
            || sender is not FrameworkElement element
            || !int.TryParse(element.Tag?.ToString(), out var serviceValue))
        {
            return;
        }

        var mode = PerformanceModeCatalog.FindByServiceValue(serviceValue);
        if (mode is null)
        {
            return;
        }

        SetModeButtonsEnabled(false);
        CurrentModeText.Text = "正在通过原厂服务写入并读回…";
        var result = await App.HardwareControl.SetPerformanceModeAsync(mode.Value.ServiceValue);
        if (!_loaded)
        {
            return;
        }

        if (result.Verified && result.ReadbackValue == mode.Value.EcValue)
        {
            App.Settings.Update(value => value.LastPerformanceServiceValue = mode.Value.ServiceValue);
            SetSelectedIndex(PerformanceModeCatalog.IndexOfEc(result.ReadbackValue.Value));
            CurrentModeText.Text = $"已确认：{mode.Value.Name}（EC {result.ReadbackValue}）";
            ControlInfo.Title = "写入成功";
            ControlInfo.Message = result.Message;
            ControlInfo.Severity = InfoBarSeverity.Success;
        }
        else
        {
            var finalIndex = result.ReadbackValue is int finalEc
                ? PerformanceModeCatalog.IndexOfEc(finalEc)
                : -1;
            SetSelectedIndex(finalIndex);
            CurrentModeText.Text = finalIndex >= 0
                ? $"{result.Message} 当前：{PerformanceModeCatalog.All[finalIndex].Name}（EC {result.ReadbackValue}）"
                : result.Message;
            ControlInfo.Title = result.Submitted ? "写入结果未确认" : "写入失败";
            ControlInfo.Message = result.Message;
            ControlInfo.Severity = result.Submitted ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
        }

        SetModeButtonsEnabled(App.HardwareControl.State.CanWrite);
    }

    private async void ScreenAccelerationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating || !_loaded)
        {
            return;
        }

        var requested = ScreenAccelerationToggle.IsOn;
        ScreenAccelerationToggle.IsEnabled = false;
        ScreenAccelerationStatus.Text = "正在通过原厂服务提交屏幕加速设置…";
        var result = await App.HardwareControl.SetScreenAccelerationAsync(requested);
        if (!_loaded)
        {
            return;
        }

        if (result.Submitted)
        {
            // Prefer the state the service reported back over the requested value,
            // so a rejected write leaves the switch showing the real hardware state.
            var actual = result.ReadbackValue is int applied ? applied == 1 : requested;
            App.Settings.Update(value => value.ScreenAccelerationEnabled = actual);
            _updating = true;
            ScreenAccelerationToggle.IsOn = actual;
            _updating = false;
            ScreenAccelerationStatus.Text = result.Message;
        }
        else
        {
            _updating = true;
            ScreenAccelerationToggle.IsOn = !requested;
            _updating = false;
            ScreenAccelerationStatus.Text = result.Message;
        }

        ScreenAccelerationToggle.IsEnabled = App.HardwareControl.State.CanWrite;
    }

    private void SetSelectedIndex(int index)
    {
        _updating = true;
        var buttons = ModeButtons;
        for (var buttonIndex = 0; buttonIndex < buttons.Length; buttonIndex++)
        {
            buttons[buttonIndex].IsChecked = buttonIndex == index;
        }
        _updating = false;
    }

    private void SetModeButtonsEnabled(bool enabled)
    {
        foreach (var button in ModeButtons)
        {
            button.IsEnabled = enabled;
        }
    }
}
