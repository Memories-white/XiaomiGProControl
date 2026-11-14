using System.Reflection;
using GProControl.Models;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public sealed partial class SettingsPage : Page
{
    private bool _updating;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = BuildVersionText();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static string BuildVersionText()
    {
        var assembly = typeof(SettingsPage).Assembly;
        var version = assembly.GetName().Version;
        var informational = assembly
            .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var label = string.IsNullOrWhiteSpace(informational)
            ? version?.ToString(3) ?? "unknown"
            : informational;
        return $"G Pro Control {label}";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.HardwareControl.StateChanged += OnHardwareStateChanged;
        ApplyApplicationSettings();
        ApplyState(await App.HardwareControl.RefreshAsync());
    }

    private void ApplyApplicationSettings()
    {
        _updating = true;
        var settings = App.Settings.Current;
        ThemeSelector.SelectedIndex = settings.ThemeMode switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        NetworkSpeedUnitSelector.SelectedIndex = settings.ShowNetworkSpeedInMegabytes ? 1 : 0;
        StartupToggle.IsOn = StartupRegistrationService.IsEnabled();
        StartupDetail.Text = App.StartupRegistrationMessage;
        StartupBackgroundToggle.IsOn = settings.StartInBackground;
        StartupBackgroundToggle.IsEnabled = StartupToggle.IsOn;
        CloseActionSelector.SelectedIndex = Enum.IsDefined(settings.CloseAction) ? (int)settings.CloseAction : 0;
        RestoreModeToggle.IsOn = settings.RestorePerformanceMode;
        RestoreLightingToggle.IsOn = settings.RestoreLighting;
        RestoreLightingDetail.Text = App.LightingRestoreMessage;
        RestoreModeDetail.Text = $"启动后等待原厂服务，并重新应用关机前的模式。{App.StartupRestoreMessage}";
        _updating = false;
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ThemeSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string mode)
        {
            return;
        }

        App.Settings.Update(value => value.ThemeMode = mode);
        App.MainWindow?.ApplyTheme(mode);
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        var enabled = StartupToggle.IsOn;
        var succeeded = StartupRegistrationService.SetEnabled(enabled, out var message);
        StartupDetail.Text = message;
        if (succeeded)
        {
            App.Settings.Update(value => value.StartWithWindows = enabled);
            StartupBackgroundToggle.IsEnabled = enabled;
        }
        else
        {
            _updating = true;
            StartupToggle.IsOn = !enabled;
            _updating = false;
        }
    }

    private void CloseActionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || CloseActionSelector.SelectedIndex is < 0 or > 2) return;
        App.Settings.Update(value => value.CloseAction = (CloseAction)CloseActionSelector.SelectedIndex);
    }

    private void NetworkSpeedUnitSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || NetworkSpeedUnitSelector.SelectedIndex < 0) return;

        App.Settings.Update(value => value.ShowNetworkSpeedInMegabytes = NetworkSpeedUnitSelector.SelectedIndex == 1);
    }

    private void StartupBackgroundToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        App.Settings.Update(value => value.StartInBackground = StartupBackgroundToggle.IsOn);
    }

    private void RestoreModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        App.Settings.Update(value => value.RestorePerformanceMode = RestoreModeToggle.IsOn);
        RestoreModeDetail.Text = RestoreModeToggle.IsOn
            ? "启动后将等待原厂服务，并重新应用最后一次成功确认的模式。"
            : "已关闭自动恢复；重启后将保留固件或原厂服务的默认行为。";
    }

    private void RestoreLightingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        App.Settings.Update(value => value.RestoreLighting = RestoreLightingToggle.IsOn);
        RestoreLightingDetail.Text = RestoreLightingToggle.IsOn
            ? "启动后恢复最后一次成功应用的键盘灯效和灯光开关。"
            : "已关闭启动时恢复灯效。";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        App.HardwareControl.StateChanged -= OnHardwareStateChanged;

    private void OnHardwareStateChanged(object? sender, HardwareControlState state) =>
        DispatcherQueue.TryEnqueue(() => ApplyState(state));

    private void ApplyState(HardwareControlState state)
    {
        _updating = true;
        HardwareGuardDetail.Text = $"{state.Message} 机型：{state.ProductName}；原厂版本：{state.ControlCenterVersion ?? "未检测到"}。";
        HardwareWriteToggle.IsEnabled = state.CanArmWrites;
        HardwareWriteToggle.IsOn = state.IsWriteArmed;
        _updating = false;
    }

    private void HardwareWriteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        App.HardwareControl.SetWriteArmed(HardwareWriteToggle.IsOn);
    }
}
