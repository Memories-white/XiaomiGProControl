using GProControl.Models;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public sealed partial class LightingPage : Page
{
    private readonly B21HidClient _hidClient = new();
    private readonly XgccHidLogReader _logReader = new();
    private KeyboardLightingState? _state;
    private bool _busy;

    public LightingPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshStateAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshStateAsync();

    private async Task RefreshStateAsync(bool showReadNotice = true)
    {
        if (_busy) return;
        SetWriteControls(false);
        PowerButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            var version = await _hidClient.ReadCommandAsync(6, TimeSpan.FromSeconds(2));
            var stateFrame = await _hidClient.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
            if (!Protocol.KeyboardLightingCodec.TryParseCmd1Reply(
                stateFrame,
                DateTime.Now,
                out var state))
            {
                throw new InvalidDataException("CMD 1 回复格式不匹配");
            }

            ApplyState(state!, $"HID 直读 · 硬件 {version[5]:X2}.{version[6]:X2}");
            var approved = version[5] == 0x01
                && version[6] == 0x13
                && App.HardwareControl.State.CanWrite
                && state is not null && WaveLightingProfile.FromState(state) is not null;
            SetWriteControls(approved);
            PowerButton.IsEnabled = version[5] == 1 && version[6] == 0x13
                && App.HardwareControl.State.CanWrite
                && state!.LedAreaMask == 0xB
                && (WaveLightingProfile.FromState(state) is not null
                    || state is { IsEnabled: false, EffectCode: 0xFF, StatusNibble: 0 }
                        && App.Settings.Current.LastWaveLighting is { IsValidated: true });
            if (!showReadNotice) return;
            StatusInfoBar.Severity = approved || PowerButton.IsEnabled
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            StatusInfoBar.Title = "已主动读取灯效状态";
            StatusInfoBar.Message = approved
                ? "可调节波浪参数或关闭键盘灯。应用成功后会记住设置。"
                : PowerButton.IsEnabled ? "键盘灯已关闭，可恢复上次保存的波浪配置。"
                : $"硬件版本 {version[5]:X2}.{version[6]:X2} 或当前效果尚未验证；不会启用写入。";
        }
        catch (Exception exception) when (exception is IOException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or InvalidOperationException
            or OperationCanceledException)
        {
            var fallback = _logReader.TryReadLatest();
            if (fallback is null)
            {
                StatusInfoBar.Severity = InfoBarSeverity.Warning;
                StatusInfoBar.Title = "无法读取灯效状态";
                StatusInfoBar.Message = $"HID 直读失败，原厂日志也没有 CMD 1 状态：{exception.Message}";
                return;
            }

            ApplyState(fallback, "原厂日志回退");
            SetWriteControls(false);
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Title = "已从原厂日志回退读取";
            StatusInfoBar.Message = $"HID 直读失败：{exception.Message}。当前显示最近一条 CMD 1 日志，写入保持禁用。";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyState(KeyboardLightingState state, string source)
    {
        _state = state;
        PowerValueText.Text = state.IsEnabled ? "已开启" : "已关闭";
        PowerButton.Content = state.IsEnabled ? "关闭" : "开启";
        EffectValueText.Text = FormatEffect(state);
        PaletteComboBox.SelectedIndex = state.PaletteCode switch
        {
            0 => 0,
            2 => 1,
            _ => -1
        };
        DirectionComboBox.SelectedIndex = state.DirectionCode switch
        {
            1 => 0,
            0 => 1,
            _ => -1
        };
        BrightnessComboBox.SelectedIndex = state.BrightnessCode switch
        {
            0 => 0,
            3 => 1,
            _ => -1
        };
        SpeedComboBox.SelectedIndex = state.SpeedCode switch
        {
            1 => 0,
            3 => 1,
            _ => -1
        };
        LightingPreviewText.Text = state.IsEnabled
            ? $"{FormatEffect(state)} · {FormatPalette(state)}"
            : "键盘灯已关闭";
        LightingSourceText.Text = $"{source} · {state.ObservedAt:HH:mm:ss} · 区域 0x{state.LedAreaMask:X1}";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled
            || !App.HardwareControl.State.CanWrite
            || PaletteComboBox.SelectedIndex is < 0 or > 1
            || DirectionComboBox.SelectedIndex is < 0 or > 1
            || BrightnessComboBox.SelectedIndex is < 0 or > 1
            || SpeedComboBox.SelectedIndex is < 0 or > 1)
        {
            return;
        }

        SetWriteControls(false);
        _busy = true;
        PowerButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            var state = await _hidClient.ApplyWaveSettingsAsync(
                PaletteComboBox.SelectedIndex == 0 ? (byte)0 : (byte)2,
                DirectionComboBox.SelectedIndex == 0 ? (byte)1 : (byte)0,
                BrightnessComboBox.SelectedIndex == 0 ? (byte)0 : (byte)3,
                SpeedComboBox.SelectedIndex == 0 ? (byte)1 : (byte)3,
                TimeSpan.FromSeconds(2));
            ApplyState(state, "HID 写入并读回");
            RememberState(state);
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = "波浪参数已应用";
            StatusInfoBar.Message = "CMD 2 写入、CMD 1 读回、CMD 5 保存均成功。";
        }
        catch (Exception exception)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "应用失败";
            StatusInfoBar.Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            await RefreshStateAsync(false);
        }
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !App.HardwareControl.State.CanWrite || !PowerButton.IsEnabled || _state is null) return;
        var profile = WaveLightingProfile.FromState(_state) ?? App.Settings.Current.LastWaveLighting;
        if (profile is not { IsValidated: true }) return;
        var enabled = !_state.IsEnabled;
        // Persist the enabled profile before turning off, so enabling works after app restart.
        _busy = true;
        SetWriteControls(false);
        PowerButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            if (!enabled)
                App.Settings.Update(value => value.LastWaveLighting = profile);
            var state = await _hidClient.ApplyLightingProfileAsync(profile, enabled, TimeSpan.FromSeconds(2));
            ApplyState(state, "键盘灯开关已读回");
            RememberState(state);
            StatusInfoBar.Severity = InfoBarSeverity.Success;
            StatusInfoBar.Title = enabled ? "键盘灯已开启" : "键盘灯已关闭";
            StatusInfoBar.Message = "设置已保存。";
        }
        catch (Exception exception)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Error;
            StatusInfoBar.Title = "键盘灯开关失败";
            StatusInfoBar.Message = exception.Message;
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            await RefreshStateAsync(false);
        }
    }

    private static void RememberState(KeyboardLightingState state) => App.Settings.Update(value =>
    {
        value.LastLightingEnabled = state.IsEnabled;
        if (WaveLightingProfile.FromState(state) is { } profile) value.LastWaveLighting = profile;
    });

    private void SetWriteControls(bool enabled)
    {
        PaletteComboBox.IsEnabled = enabled;
        DirectionComboBox.IsEnabled = enabled;
        BrightnessComboBox.IsEnabled = enabled;
        SpeedComboBox.IsEnabled = enabled;
        ApplyButton.IsEnabled = enabled;
    }

    private static string FormatEffect(KeyboardLightingState state) =>
        !state.IsEnabled ? "已关闭" : state.EffectCode == 1 ? "波浪" : $"效果编码 0x{state.EffectCode:X2}";

    private static string FormatPalette(KeyboardLightingState state) => state.PaletteCode switch
    {
        0 => "配色第 1 项",
        2 => "配色第 3 项",
        _ => $"配色编码 {state.PaletteCode}"
    };

}
