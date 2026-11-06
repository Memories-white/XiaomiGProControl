using GProControl.Models;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace GProControl.Views;
public sealed partial class LightingPage : Page
{
    private readonly B21HidClient _client = new();
    private KeyboardLightingState? _state;
    private bool _busy, _updating, _loaded, _approved;
    public LightingPage()
    {
        InitializeComponent();
        _updating = true; EffectSelector.ItemsSource = LightingEffectChoice.All; _updating = false;
        Loaded += async (_, _) => {
            _loaded = true;
            App.AmbientLighting.Changed += OnAmbientChanged;
            App.HardwareControl.StateChanged += OnHardwareChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) => {
            _loaded = false;
            App.AmbientLighting.Changed -= OnAmbientChanged;
            App.HardwareControl.StateChanged -= OnHardwareChanged;
        };
    }
    private void OnHardwareChanged(object? sender, HardwareControlState state) =>
        DispatcherQueue.TryEnqueue(() => { if (_loaded) UpdateControls(); });
    private void OnAmbientChanged() => DispatcherQueue.TryEnqueue(async () => {
        if (!_loaded || _busy) return;
        await RefreshAsync();
        if (App.AmbientLighting.LastError is string error) Notice("动态灯效已停止", error, InfoBarSeverity.Error);
    });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true; UpdateControls();
        try {
            var version = await _client.ReadCommandAsync(6, TimeSpan.FromSeconds(2));
            var frame = await _client.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
            if (!Protocol.KeyboardLightingCodec.TryParseCmd1Reply(frame, DateTime.Now, out var state))
                throw new InvalidDataException("灯效状态格式不匹配。");
            if (!_loaded) return;
            _approved = version[5] == 1 && version[6] == 0x13 && state!.LedAreaMask == 0xB;
            ApplyState(state!);
            Notice(_approved ? "已同步键盘灯效" : "键盘版本尚不支持",
                App.AmbientLighting.ActiveEffect is not null ? "动态灯效正在运行，切换页面或转入后台后会继续。"
                : "选择效果后点击应用。常规效果保存在键盘；光影与光音效果在软件运行期间生效。",
                _approved ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception exception) { _approved = false; Notice("灯效读取失败", exception.Message, InfoBarSeverity.Error); }
        finally { _busy = false; UpdateControls(); }
    }
    private void ApplyState(KeyboardLightingState state)
    {
        _state = state; _updating = true;
        var saved = LightingProfile.FromState(state) ?? App.Settings.Current.LastLightingProfile;
        var id = App.AmbientLighting.ActiveEffect ?? (saved?.PaletteCode == 0xFF ? 23 : saved?.EffectCode ?? 1);
        EffectSelector.SelectedItem = LightingEffectChoice.All.FirstOrDefault(effect => effect.Id == id);
        PaletteComboBox.SelectedIndex = Math.Clamp((int)(saved?.PaletteCode ?? 0), 0, 2);
        DirectionComboBox.SelectedIndex = Math.Clamp((int)(saved?.DirectionCode ?? 0), 0, 2);
        BrightnessComboBox.SelectedIndex = Math.Clamp((int)(saved?.BrightnessCode ?? 3), 0, 3);
        SpeedComboBox.SelectedIndex = Math.Clamp((saved?.SpeedCode ?? 1)-1, 0, 2);
        LengthComboBox.SelectedIndex = Math.Clamp((saved?.LengthCode ?? 1)-1, 0, 1);
        if (saved is { PaletteCode: 0xFF, ColorsHex.Length: 6 }) {
            var rgb = Convert.FromHexString(saved.ColorsHex);
            EffectColor.Color = Windows.UI.Color.FromArgb(255, (byte)(rgb[0]*2), (byte)(rgb[1]*2), (byte)(rgb[2]*2));
        }
        PowerValueText.Text = state.IsEnabled ? "已开启" : "已关闭";
        PowerButton.Content = state.IsEnabled ? "关闭" : "开启";
        LightingPreviewText.Text = state.IsEnabled ? (EffectSelector.SelectedItem as LightingEffectChoice)?.Name ?? "当前灯效" : "键盘灯已关闭";
        LightingSourceText.Text = $"设备状态 · {state.ObservedAt:HH:mm:ss}";
        _updating = false;
    }
    private void Effect_Changed(object sender, SelectionChangedEventArgs e) {
        if (_updating) return;
        UpdateControls();
        if (EffectSelector.SelectedItem is LightingEffectChoice choice) LightingPreviewText.Text = choice.Name + " · 待应用";
    }
    private void UpdateControls()
    {
        var writable = _approved && !_busy && App.HardwareControl.State.CanWrite;
        var effect = (EffectSelector.SelectedItem as LightingEffectChoice)?.Id ?? 1;
        var direction = Math.Max(0, DirectionComboBox.SelectedIndex);
        DirectionComboBox.ItemsSource = effect == 6 ? new[] {"反向", "正向", "往返"} : new[] {"反向", "正向"};
        DirectionComboBox.SelectedIndex = Math.Min(direction, effect == 6 ? 2 : 1);
        EffectSelector.IsEnabled = writable;
        PaletteComboBox.IsEnabled = writable && effect < 20;
        DirectionComboBox.IsEnabled = writable && effect is 1 or 6 or 7;
        BrightnessComboBox.IsEnabled = writable && effect != 10;
        SpeedComboBox.IsEnabled = writable && effect is 1 or 3 or 6 or 7 or 8;
        LengthComboBox.IsEnabled = writable && effect is 3 or 6 or 8;
        EffectColor.Visibility = effect is 21 or 23 ? Visibility.Visible : Visibility.Collapsed;
        EffectColor.IsEnabled = writable;
        EffectDescription.Text = effect switch {
            21 => "随系统播放音量改变选定颜色的亮度；退出软件后恢复常规灯效。",
            22 => "根据主屏幕平均颜色实时调整键盘，不保存或上传屏幕画面；退出后恢复常规灯效。",
            23 => "使用自选颜色常亮，可调整亮度。",
            _ => "不适用于当前效果的参数已停用。应用后会保存，并记住下次启动要恢复的配置。"
        };
        ApplyButton.IsEnabled = writable; PowerButton.IsEnabled = writable;
        StopAmbientButton.Visibility = App.AmbientLighting.ActiveEffect is null ? Visibility.Collapsed : Visibility.Visible;
        StopAmbientButton.IsEnabled = !_busy; RefreshButton.IsEnabled = !_busy;
    }
    private LightingProfile SelectedProfile()
    {
        var effect = (EffectSelector.SelectedItem as LightingEffectChoice)?.Id ?? 1;
        var color = EffectColor.Color;
        var colors = $"{color.R/2:X2}{color.G/2:X2}{color.B/2:X2}";
        return new((byte)(effect >= 20 ? 11 : effect), effect >= 20 ? (byte)0xFF : (byte)Math.Max(0,PaletteComboBox.SelectedIndex),
            (byte)Math.Max(0,DirectionComboBox.SelectedIndex), effect == 10 ? (byte)3 : (byte)Math.Max(0,BrightnessComboBox.SelectedIndex),
            (byte)(SpeedComboBox.SelectedIndex+1), effect is 3 or 6 or 8 ? (byte)(LengthComboBox.SelectedIndex+1) : (byte)0,
            effect >= 20 ? colors : "");
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled || _busy) return;
        _busy = true; UpdateControls();
        try {
            var effect = ((LightingEffectChoice)EffectSelector.SelectedItem).Id;
            var profile = SelectedProfile();
            if (effect is 21 or 22) {
                await App.AmbientLighting.StartAsync(effect, profile.BrightnessCode, profile.ColorsHex);
                Notice("动态灯效已启动", "停止动态灯效或退出软件后恢复原来的常规效果。", InfoBarSeverity.Success);
            } else {
                await App.AmbientLighting.StopAsync();
                var applied = await _client.ApplyEffectAsync(profile, true, TimeSpan.FromSeconds(2));
                Remember(applied); ApplyState(applied);
                Notice("灯效已应用", "已从键盘读回确认，并保存设置。", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception) { Notice("灯效应用失败", exception.Message, InfoBarSeverity.Error); }
        finally { _busy = false; UpdateControls(); }
    }
    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (!PowerButton.IsEnabled || _busy) return;
        _busy = true; UpdateControls();
        try {
            var wasAmbient = App.AmbientLighting.ActiveEffect is not null;
            await App.AmbientLighting.StopAsync();
            var profile = wasAmbient ? App.Settings.Current.LastLightingProfile
                : _state is not null ? LightingProfile.FromState(_state) : null;
            profile ??= App.Settings.Current.LastLightingProfile ?? SelectedProfile();
            var enabled = _state?.IsEnabled != true;
            var applied = await _client.ApplyEffectAsync(profile, enabled, TimeSpan.FromSeconds(2));
            if (!enabled) App.Settings.Update(value => value.LastLightingProfile = profile);
            Remember(applied); ApplyState(applied);
            Notice(enabled ? "键盘灯已开启" : "键盘灯已关闭", "设置已保存。", InfoBarSeverity.Success);
        }
        catch (Exception exception) { Notice("灯效开关失败", exception.Message, InfoBarSeverity.Error); }
        finally { _busy = false; UpdateControls(); }
    }
    private async void StopAmbient_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true; UpdateControls();
        await App.AmbientLighting.StopAsync(); _busy = false; await RefreshAsync();
    }
    private static void Remember(KeyboardLightingState state) => App.Settings.Update(value => {
        value.LastLightingEnabled = state.IsEnabled;
        if (LightingProfile.FromState(state) is { } profile) value.LastLightingProfile = profile;
        if (WaveLightingProfile.FromState(state) is { } wave) value.LastWaveLighting = wave;
    });
    private void Notice(string title, string message, InfoBarSeverity severity) {
        if (!_loaded) return;
        StatusInfoBar.Title = title; StatusInfoBar.Message = message; StatusInfoBar.Severity = severity;
    }
}
