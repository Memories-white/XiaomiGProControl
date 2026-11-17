using GProControl.Models;
using GProControl.Protocol;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GProControl.Views;

public sealed partial class PerformancePage : Page
{
    private bool _updating;
    private bool _loaded;
    private bool _syncingGpuOverclock;
    private bool _gpuOverclockBusy;
    private bool? _gpuOverclockEnabled;

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
        if (App.PendingGpuOverclockTarget is bool target)
            SyncGpuOverclockToggle(target);
        if (App.MainWindow is { } window)
        {
            window.AppearanceChanged += OnAppearanceChanged;
            OnAppearanceChanged(window, EventArgs.Empty);
        }
        App.HardwareControl.StateChanged += OnHardwareStateChanged;
        _updating = true;
        ScreenAccelerationToggle.IsOn = App.Settings.Current.ScreenAccelerationEnabled;
        _updating = false;
        ApplyState(await App.HardwareControl.RefreshAsync());
        await RefreshModeAsync();
        await RefreshWlanAsync();
        await RefreshScreenAccelerationAsync();
        await RefreshGpuOverclockAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        if (App.MainWindow is { } window)
            window.AppearanceChanged -= OnAppearanceChanged;
        App.HardwareControl.StateChanged -= OnHardwareStateChanged;
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window) return;
        var appearance = window.ActiveAppearance;
        if (appearance == AppAppearance.Windows)
            GpuOverclockProgress.ClearValue(Control.ForegroundProperty);
        else
            GpuOverclockProgress.Foreground = (Brush)Application.Current.Resources["GamingToggleStrokeBrush"];
        foreach (var option in ModeButtons)
        {
            if (appearance == AppAppearance.Windows)
            {
                option.ClearValue(Control.BackgroundProperty);
                option.ClearValue(Control.BorderBrushProperty);
            }
            else
            {
                option.Background = (Brush)Resources[appearance == AppAppearance.Rog ? "RogModeBackground" : "LegionModeBackground"];
                option.BorderBrush = (Brush)Application.Current.Resources["GamingToggleStrokeBrush"];
            }
            option.CornerRadius = new CornerRadius(appearance == AppAppearance.Rog ? 2 : 6);
        }
    }

    private void ModeSelector_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 440;
        int columns = compact ? 2 : 4;
        int span = compact ? 2 : 1;
        ModeSelector.RowSpacing = compact ? 8 : 0;
        for (var index = 0; index < ModeSelector.Children.Count; index++)
        {
            var option = (RadioButton)ModeSelector.Children[index];
            Grid.SetColumn(option, index % columns * span);
            Grid.SetRow(option, index / columns);
            Grid.SetColumnSpan(option, span);
        }
    }

    private void OnHardwareStateChanged(object? sender, HardwareControlState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyState(state));

    private void ApplyState(HardwareControlState state)
    {
        SetModeButtonsEnabled(state.CanWrite);
        ScreenAccelerationToggle.IsEnabled = state.CanWrite;
        RefreshModeButton.IsEnabled = state.CanArmWrites;
        RefreshScreenAccelerationButton.IsEnabled = state.CanArmWrites;
        UpdateGpuOverclockControls(state);
        ControlInfo.Title = state.CanWrite ? "硬件写入已解锁" : "硬件写入已锁定";
        ControlInfo.Message = state.Message;
        ControlInfo.Severity = state.CanWrite ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        RoutineNotice.ApplyVisibility(ControlInfo, routine: state.CanWrite);
    }

    private async void RefreshModeButton_Click(object sender, RoutedEventArgs e) => await RefreshModeAsync();

    private async void RefreshWlanButton_Click(object sender, RoutedEventArgs e) => await RefreshWlanAsync();

    private async void RefreshScreenAccelerationButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshScreenAccelerationAsync();

    private async void RefreshGpuOverclockButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshGpuOverclockAsync();

    private void UpdateGpuOverclockControls(HardwareControlState state)
    {
        RefreshGpuOverclockButton.IsEnabled = state.CanArmWrites && !_gpuOverclockBusy;
        GpuOverclockToggle.IsEnabled = state.CanWrite && _gpuOverclockEnabled.HasValue
            && !_gpuOverclockBusy && !App.PendingGpuOverclockTarget.HasValue;
    }

    internal async Task RefreshGpuOverclockAsync(Func<Task<HardwareActionResult>>? read = null)
    {
        if (!_loaded || _gpuOverclockBusy) return;
        await RunGpuOverclockAsync(async () =>
        {
            ShowGpuOverclockProgress("正在通过原厂服务读取 GPU 加速状态…");
            return await (read?.Invoke() ?? App.HardwareControl.GetGpuOverclockAsync());
        });
    }

    private void SyncGpuOverclockToggle(bool requested)
    {
        _syncingGpuOverclock = true;
        GpuOverclockToggle.IsOn = requested;
        _syncingGpuOverclock = false;
    }

    private async void GpuOverclockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _syncingGpuOverclock || _gpuOverclockBusy) return;

        await RequestGpuOverclockAsync(GpuOverclockToggle.IsOn,
            async () => await CreateGpuOverclockDialog().ShowAsync() == ContentDialogResult.Primary,
            enabled => App.HardwareControl.SetGpuOverclockAsync(enabled));
    }

    internal async Task RequestGpuOverclockAsync(bool enabled, Func<Task<bool>> confirm,
        Func<bool, Task<HardwareActionResult>> set)
    {
        if (!_loaded || _gpuOverclockBusy || App.PendingGpuOverclockTarget.HasValue) return;
        await RunGpuOverclockAsync(async () =>
        {
            if (enabled)
            {
                SyncGpuOverclockToggle(_gpuOverclockEnabled == true);
                if (!await confirm() || !_loaded) return null;
            }
            App.PendingGpuOverclockTarget = enabled;
            SyncGpuOverclockToggle(enabled);
            ShowGpuOverclockProgress(enabled
                ? "正在开启 GPU 加速，等待原厂服务回读…"
                : "正在关闭 GPU 加速，等待原厂服务回读…");
            var result = await set(enabled);
            if (!result.Submitted || result.ReadbackValue.HasValue)
                App.PendingGpuOverclockTarget = null;
            StartupDiagnostics.Write($"GPU overclock: requested={enabled}, verified={result.Verified}, readback={result.ReadbackValue}. {result.Message}");
            if (!result.Submitted && result.ReadbackValue is null && _gpuOverclockEnabled is bool previous)
                return result with { ReadbackValue = previous ? 1 : 0 };
            return result;
        });
    }

    internal ContentDialog CreateGpuOverclockDialog()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "开启 GPU 加速引擎？",
            Content = "原厂显卡超频预设可能影响系统稳定性。请保持接通电源，退出狂暴模式前先关闭 GPU 加速。",
            PrimaryButtonText = "确认开启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        var appearance = App.MainWindow?.ActiveAppearance ?? AppAppearance.Windows;
        if (appearance == AppAppearance.Windows) return dialog;

        var resources = Application.Current.Resources;
        dialog.Background = GpuOverclockCard.Background;
        dialog.BorderBrush = GpuOverclockCard.BorderBrush;
        dialog.BorderThickness = GpuOverclockCard.BorderThickness;
        dialog.CornerRadius = GpuOverclockCard.CornerRadius;
        dialog.PrimaryButtonStyle = (Style)resources[appearance == AppAppearance.Rog
            ? "RogSectionButtonStyle" : "LegionSectionButtonStyle"];
        dialog.Resources["ContentDialogTopOverlay"] = GpuOverclockCard.Background;
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = GpuOverclockCard.BorderBrush;
        dialog.Resources["AccentButtonBackground"] = resources["GamingToggleFillBrush"];
        dialog.Resources["AccentButtonBackgroundPointerOver"] = resources["GamingToggleHoverBrush"];
        dialog.Resources["AccentButtonBackgroundPressed"] = resources["GamingTogglePressedBrush"];
        return dialog;
    }

    private void ShowGpuOverclockProgress(string status)
    {
        GpuOverclockStatus.Text = status;
        GpuOverclockProgress.Visibility = Visibility.Visible;
        GpuOverclockProgress.IsActive = true;
    }

    private async Task RunGpuOverclockAsync(Func<Task<HardwareActionResult?>> action)
    {
        _gpuOverclockBusy = true;
        UpdateGpuOverclockControls(App.HardwareControl.State);
        try
        {
            var result = await action();
            if (!_loaded || result is null) return;
            if (App.PendingGpuOverclockTarget is bool target)
            {
                if (!result.Verified || result.ReadbackValue != (target ? 1 : 0))
                {
                    SyncGpuOverclockToggle(target);
                    GpuOverclockStatus.Text = target
                        ? "尚未确认开启。点击「回读状态」继续确认。"
                        : "尚未确认关闭。点击「回读状态」继续确认。";
                    return;
                }
                App.PendingGpuOverclockTarget = null;
            }
            if (result.ReadbackValue is int value)
                _gpuOverclockEnabled = value == 1;
            else if (_gpuOverclockEnabled.HasValue)
            {
                GpuOverclockStatus.Text = $"{result.Message} 开关保留上次确认的位置，请重新读取。";
                return;
            }
            GpuOverclockStatus.Text = result.Message;
            SyncGpuOverclockToggle(_gpuOverclockEnabled == true);
        }
        finally
        {
            _gpuOverclockBusy = false;
            GpuOverclockProgress.IsActive = false;
            GpuOverclockProgress.Visibility = Visibility.Collapsed;
            if (_loaded) UpdateGpuOverclockControls(App.HardwareControl.State);
        }
    }

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
        StartupDiagnostics.Write($"Performance mode: requested={mode.Value.Name}, verified={result.Verified}, readback={result.ReadbackValue}. {result.Message}");
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

        RoutineNotice.ApplyVisibility(ControlInfo, routine: false);
        ControlInfo.IsOpen = true;
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
        StartupDiagnostics.Write($"Screen acceleration: requested={requested}, verified={result.Verified}, readback={result.ReadbackValue}. {result.Message}");
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
