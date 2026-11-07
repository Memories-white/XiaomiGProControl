using GProControl.Models;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace GProControl.Views;
public sealed partial class LightingPage : Page
{
    private readonly B21HidClient _client = new();
    private KeyboardLightingState? _state;
    private bool _busy, _updating, _loaded, _approved;
    private int _parameterEffect = -1;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? _testCancellation;
    public LightingPage()
    {
        InitializeComponent();
        _updating = true; EffectSelector.ItemsSource = LightingEffectChoice.All;
        PresetList.ItemsSource = LightingPresetCatalog.All; _updating = false;
        AudioPaletteBox.ItemsSource=LightingPresetCatalog.AudioPalettes;
        var options=App.Settings.Current.AmbientLighting;
        if(options is null || !options.IsValid) options=new();
        AudioSourceBox.SelectedIndex=options.AudioSource; AudioStyleBox.SelectedIndex=options.AudioStyle;
        AudioPaletteBox.SelectedIndex=options.AudioPalette; ScreenSamplesSlider.Value=options.ScreenSamples;
        OutputModeBox.SelectedIndex=options.PerKeyOutput && App.Settings.Current.RealtimeLightingValidated ? 1 : 0;
        SpectrumDirectionBox.SelectedIndex=options.SpectrumDirection;
        _previewTimer.Tick += (_,_) => {
            SpectrumList.ItemsSource=App.AmbientLighting.SpectrumBands.Select(band => (double)Math.Max(1,band*55)).ToArray();
            if(_testCancellation is null) ShowMatrix(App.AmbientLighting.MatrixPreview);
        };
        Loaded += async (_, _) => {
            _loaded = true;
            _previewTimer.Start();
            App.AmbientLighting.Changed += OnAmbientChanged;
            App.HardwareControl.StateChanged += OnHardwareChanged;
            await RefreshAsync();
        };
        Unloaded += (_, _) => {
            _loaded = false;
            _previewTimer.Stop();
            _testCancellation?.Cancel();
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
        catch (Exception exception)
        {
            _approved = false;
            StartupDiagnostics.Write("Keyboard lighting read failed.", exception);
            Notice("灯效读取失败", exception.Message, InfoBarSeverity.Error);
        }
        finally { _busy = false; UpdateControls(); }
    }
    private void ApplyState(KeyboardLightingState state)
    {
        _state = state; _updating = true;
        var saved = LightingProfile.FromState(state) ?? App.Settings.Current.LastLightingProfile;
        var id = App.AmbientLighting.ActiveEffect ?? (saved?.PaletteCode == 0xFF ? 23 : saved?.EffectCode ?? 1);
        EffectSelector.SelectedItem = LightingEffectChoice.All.FirstOrDefault(effect => effect.Id == id);
        ConfigureParameterChoices(id);
        PaletteComboBox.SelectedIndex = Math.Clamp((int)(saved?.PaletteCode ?? 0), 0, PaletteComboBox.Items.Count-1);
        DirectionComboBox.SelectedIndex = Math.Clamp((int)(saved?.DirectionCode ?? 0), 0, DirectionComboBox.Items.Count-1);
        BrightnessComboBox.SelectedIndex = Math.Clamp((int)(saved?.BrightnessCode ?? 3), 0, 3);
        SpeedComboBox.SelectedIndex = Math.Clamp((saved?.SpeedCode ?? 1)-1, 0, 2);
        LengthComboBox.SelectedIndex = Math.Clamp((int)(saved?.LengthCode ?? 1), 0, LengthComboBox.Items.Count-1);
        if (saved is { PaletteCode: 0xFF, ColorsHex.Length: 6 }) {
            var rgb = Convert.FromHexString(saved.ColorsHex);
            EffectColor.Color = Windows.UI.Color.FromArgb(255, (byte)(rgb[0]*2), (byte)(rgb[1]*2), (byte)(rgb[2]*2));
        }
        if(App.AmbientLighting.ActiveEffect is not null)
        {
            var options=App.AmbientLighting.Options;
            AudioSourceBox.SelectedIndex=options.AudioSource; AudioStyleBox.SelectedIndex=options.AudioStyle;
            AudioPaletteBox.SelectedIndex=options.AudioPalette; ScreenSamplesSlider.Value=options.ScreenSamples;
            OutputModeBox.SelectedIndex=options.PerKeyOutput ? 1 : 0; SpectrumDirectionBox.SelectedIndex=options.SpectrumDirection;
            BrightnessComboBox.SelectedIndex=App.AmbientLighting.Brightness;
            var rgb=Convert.FromHexString(App.AmbientLighting.SelectedColor);
            EffectColor.Color=Windows.UI.Color.FromArgb(255,(byte)(rgb[0]*2),(byte)(rgb[1]*2),(byte)(rgb[2]*2));
        }
        PowerValueText.Text = state.IsEnabled ? "已开启" : "已关闭";
        PowerButton.Content = state.IsEnabled ? "关闭" : "开启";
        LightingPreviewText.Text = state.IsEnabled ? (EffectSelector.SelectedItem as LightingEffectChoice)?.Name ?? "当前灯效" : "键盘灯已关闭";
        LightingSourceText.Text = $"设备状态 · {state.ObservedAt:HH:mm:ss}";
        _updating = false;
    }
    private void Effect_Changed(object sender, SelectionChangedEventArgs e) {
        if (_updating) return;
        if (EffectSelector.SelectedItem is LightingEffectChoice choice) LoadPreset(LightingPresetCatalog.ForEffect(choice.Id));
    }
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && sender is Button { DataContext: LightingPreset preset }) LoadPreset(preset);
    }
    private void ResetPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy && EffectSelector.SelectedItem is LightingEffectChoice choice) LoadPreset(LightingPresetCatalog.ForEffect(choice.Id));
    }
    private void ShowMatrix(IReadOnlyList<KeyboardPixel> pixels)
    {
        MatrixPreviewPanel.Visibility=pixels.Count==0 ? Visibility.Collapsed : Visibility.Visible;
        MatrixPreviewList.ItemsSource=pixels.Select(pixel=>new SolidColorBrush(Windows.UI.Color.FromArgb(255,pixel.Red,pixel.Green,pixel.Blue))).ToArray();
    }
    private void OutputMode_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_updating || SpectrumDirectionBox is null) return;
        if(OutputModeBox.SelectedIndex==1 && !App.Settings.Current.RealtimeLightingValidated)
        {
            OutputModeBox.SelectedIndex=0;
            Notice("请先校验逐键显示","点击校验按钮，确认颜色分区和光带正常后即可使用。",InfoBarSeverity.Warning);
        }
        SpectrumDirectionBox.IsEnabled=!_busy && OutputModeBox.SelectedIndex==1;
    }
    private async void ValidateRealtime_Click(object sender,RoutedEventArgs e)
    {
        if(_busy || !RealtimeTestButton.IsEnabled) return;
        _busy=true; UpdateControls();
        _testCancellation=new();
        try
        {
            var window=App.MainWindow ?? throw new InvalidOperationException("主窗口不可用。");
            await window.RunLightingTestAsync(RunRealtimeDiagnosticAsync,_testCancellation.Token);
            if(!_loaded || window.IsExiting) return;
            var dialog=new ContentDialog {
                XamlRoot=XamlRoot,Title="逐键灯效显示是否正常？",
                Content="刚才应先出现左红、中绿、右蓝，然后出现移动的白色光带。原灯效已经恢复。只有实际颜色和位置正确时才选择显示正常。",
                PrimaryButtonText="显示正常，启用逐键输出",SecondaryButtonText="显示异常",CloseButtonText="稍后再试"
            };
            var result=await dialog.ShowAsync();
            if(result is ContentDialogResult.Primary or ContentDialogResult.Secondary)
            {
                var verified=result==ContentDialogResult.Primary;
                App.Settings.Update(value=>value.RealtimeLightingValidated=verified);
                OutputModeBox.SelectedIndex=verified ? 1 : 0;
                Notice(verified ? "逐键显示已校验" : "保持统一颜色输出",verified ? "可以使用逐键屏幕映射和四方向频谱柱。" : "逐键功能未启用，原灯效已恢复。",verified ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
        }
        catch(OperationCanceledException) {Notice("校验已取消","原灯效恢复已执行。",InfoBarSeverity.Informational);}
        catch(Exception exception) {Notice("逐键校验未通过",exception.Message,InfoBarSeverity.Error);}
        finally {_testCancellation.Dispose();_testCancellation=null;_busy=false;UpdateControls();ShowMatrix([]);}
    }
    private async Task RunRealtimeDiagnosticAsync(CancellationToken cancellationToken)
    {
        await App.AmbientLighting.StopAsync();
        await using var session=await _client.StartRealtimeAsync(cancellationToken);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(timer.Elapsed.TotalSeconds<16)
        {
            if(!App.HardwareControl.State.CanWrite) throw new InvalidOperationException("硬件控制已关闭。");
            var seconds=timer.Elapsed.TotalSeconds;
            Notice("正在校验逐键灯效",seconds<8 ? "请观察：左红、中绿、右蓝。结束后会恢复原灯效。" : "请观察：白色光带横向移动。结束后会恢复原灯效。",InfoBarSeverity.Informational);
            var pixels=KeyboardPixelRenderer.DiagnosticFrame(seconds);
            await session.WriteAsync(pixels,cancellationToken); ShowMatrix(pixels);
            await Task.Delay(60,cancellationToken);
        }
    }
    private void LoadPreset(LightingPreset preset)
    {
        _updating = true;
        EffectSelector.SelectedItem = LightingEffectChoice.All.First(choice => choice.Id == preset.EffectId);
        ConfigureParameterChoices(preset.EffectId);
        var profile = preset.Profile;
        PaletteComboBox.SelectedIndex = profile.PaletteCode == 255 ? 0 : profile.PaletteCode;
        DirectionComboBox.SelectedIndex = Math.Min(profile.DirectionCode, DirectionComboBox.Items.Count-1);
        BrightnessComboBox.SelectedIndex = profile.BrightnessCode;
        SpeedComboBox.SelectedIndex = Math.Max(0, profile.SpeedCode-1);
        LengthComboBox.SelectedIndex = Math.Min(profile.LengthCode, LengthComboBox.Items.Count-1);
        if(preset.EffectId==21) {AudioSourceBox.SelectedIndex=0; AudioStyleBox.SelectedIndex=1; AudioPaletteBox.SelectedIndex=0;}
        if(preset.EffectId==22) ScreenSamplesSlider.Value=4;
        if(preset.EffectId is 21 or 22) {OutputModeBox.SelectedIndex=App.Settings.Current.RealtimeLightingValidated ? 1 : 0;SpectrumDirectionBox.SelectedIndex=0;}
        if (profile.ColorsHex.Length == 6)
        {
            var rgb=Convert.FromHexString(profile.ColorsHex);
            EffectColor.Color=Windows.UI.Color.FromArgb(255,(byte)(rgb[0]*2),(byte)(rgb[1]*2),(byte)(rgb[2]*2));
        }
        _updating = false;
        UpdateControls();
        LightingPreviewText.Text = preset.Name + " · 预设待应用";
        Notice("已载入“"+preset.Name+"”预设", "已填入原厂默认参数，点击“应用灯效”生效。", InfoBarSeverity.Informational);
    }
    private void ConfigureParameterChoices(int effect)
    {
        if (_parameterEffect == effect) return;
        _parameterEffect = effect;
        PaletteComboBox.Items.Clear();
        foreach (var palette in LightingPresetCatalog.PalettesFor(effect))
        {
            Brush brush = new SolidColorBrush(ParseColor(palette.PreviewColor));
            if (palette.Stops is { } stops)
            {
                var gradient = new LinearGradientBrush {StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,0)};
                foreach(var stop in stops) gradient.GradientStops.Add(new GradientStop {Color=ParseColor(stop.Color),Offset=stop.Offset});
                brush=gradient;
            }
            var row=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8};
            row.Children.Add(new Border {Width=40,Height=12,CornerRadius=new CornerRadius(3),Background=brush,VerticalAlignment=VerticalAlignment.Center});
            row.Children.Add(new TextBlock {Text=palette.Name,VerticalAlignment=VerticalAlignment.Center});
            PaletteComboBox.Items.Add(new ComboBoxItem {Content=row,Tag=palette.Code});
        }
        DirectionComboBox.ItemsSource = effect == 6 ? new[] {"向右","向左","往返"}
            : effect == 7 ? new[] {"逆时针","顺时针"} : new[] {"向左","向右"};
        LengthComboBox.ItemsSource = effect == 8 ? new[] {"短","长"}
            : effect == 3 ? new[] {"细","正常","粗"} : effect == 6 ? new[] {"窄","正常","宽"} : new[] {"不适用"};
        PaletteComboBox.SelectedIndex=0; DirectionComboBox.SelectedIndex=0; LengthComboBox.SelectedIndex=0;
    }
    private static Windows.UI.Color ParseColor(string value)
    {
        var color=Convert.ToUInt32(value[1..],16);
        return Windows.UI.Color.FromArgb((byte)(color>>24),(byte)(color>>16),(byte)(color>>8),(byte)color);
    }
    private void UpdateControls()
    {
        var writable = _approved && !_busy && App.HardwareControl.State.CanWrite;
        var effect = (EffectSelector.SelectedItem as LightingEffectChoice)?.Id ?? 1;
        ConfigureParameterChoices(effect);
        PresetList.IsEnabled = !_busy;
        ResetPresetButton.IsEnabled = !_busy && EffectSelector.SelectedItem is not null;
        EffectSelector.IsEnabled = writable;
        PaletteComboBox.IsEnabled = writable && effect < 20;
        DirectionComboBox.IsEnabled = writable && effect is 1 or 6 or 7;
        BrightnessComboBox.IsEnabled = writable && effect != 10;
        SpeedComboBox.IsEnabled = writable && effect is 1 or 3 or 6 or 7 or 8;
        LengthComboBox.IsEnabled = writable && effect is 3 or 6 or 8;
        EffectColor.Visibility = effect is 21 or 23 ? Visibility.Visible : Visibility.Collapsed;
        EffectColor.IsEnabled = writable;
        AudioOptionsPanel.Visibility=effect==21 ? Visibility.Visible : Visibility.Collapsed;
        ScreenOptionsPanel.Visibility=effect==22 ? Visibility.Visible : Visibility.Collapsed;
        OutputOptionsPanel.Visibility=effect is 21 or 22 ? Visibility.Visible : Visibility.Collapsed;
        OutputModeBox.IsEnabled=RealtimeTestButton.IsEnabled=writable;
        SpectrumDirectionBox.IsEnabled=writable && OutputModeBox.SelectedIndex==1;
        RealtimeStatusText.Text=App.Settings.Current.RealtimeLightingValidated ? "逐键显示已校验；动画不写入闪存，停止后恢复原灯效。" : "先观察颜色分区及光带，校验后开放逐键输出。";
        AudioSourceBox.IsEnabled=AudioStyleBox.IsEnabled=AudioPaletteBox.IsEnabled=ScreenSamplesSlider.IsEnabled=writable;
        EffectDescription.Text = effect switch {
            21 => "支持系统声音或麦克风；统一混色或四方向逐键频谱柱。退出后恢复原灯效。",
            22 => "支持主屏幕平均色或逐键屏幕映射，不保存或上传屏幕画面；退出后恢复原灯效。",
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
            (byte)(SpeedComboBox.SelectedIndex+1), effect is 3 or 6 or 8 ? (byte)Math.Max(0,LengthComboBox.SelectedIndex) : (byte)0,
            effect >= 20 ? colors : "");
    }
    internal void VerifyFactoryPresets()
    {
        if (PresetList.Items.Count != 12) throw new InvalidOperationException("Factory preset cards are missing.");
        foreach (var preset in LightingPresetCatalog.All)
        {
            Preset_Click(new Button { DataContext = preset }, new RoutedEventArgs());
            if (((LightingEffectChoice)EffectSelector.SelectedItem).Id != preset.EffectId
                || SelectedProfile() != preset.Profile)
                throw new InvalidOperationException($"Factory preset controls differ: {preset.EffectId}");
            var count = LightingPresetCatalog.SupportsSolidPalette(preset.Profile.EffectCode) && preset.EffectId < 20 ? 7 : 3;
            if (PaletteComboBox.Items.Count != count) throw new InvalidOperationException("Palette count differs.");
            BrightnessComboBox.SelectedIndex = 0;
            ResetPreset_Click(this, new RoutedEventArgs());
            if (SelectedProfile() != preset.Profile) throw new InvalidOperationException("Reset defaults failed.");
        }
        StartupDiagnostics.Write("Factory preset UI smoke: cards=12; defaults=True; palettes=True; reset=True; hardwareWrites=False");
        LoadPreset(LightingPresetCatalog.ForEffect(21));
        OutputModeBox.SelectedIndex=1;
        if(!App.Settings.Current.RealtimeLightingValidated && OutputModeBox.SelectedIndex!=0)
            throw new InvalidOperationException("Unconfirmed per-key output was enabled.");
        ShowMatrix(KeyboardPixelRenderer.DiagnosticFrame(0));
        if(MatrixPreviewList.Items.Count!=119) throw new InvalidOperationException("Matrix preview is incomplete.");
        ShowMatrix([]);
        StartupDiagnostics.Write("Per-key controls UI smoke: matrix=119; calibrationGate=True; noHardwareWrites=True");
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled || _busy) return;
        _busy = true; UpdateControls();
        try {
            var effect = ((LightingEffectChoice)EffectSelector.SelectedItem).Id;
            var profile = SelectedProfile();
            StartupDiagnostics.Write($"Lighting requested: effect={effect}, palette={profile.PaletteCode}, brightness={profile.BrightnessCode}, speed={profile.SpeedCode}, direction={profile.DirectionCode}, length={profile.LengthCode}.");
            if (effect is 21 or 22) {
                var options=new AmbientLightingOptions(AudioSourceBox.SelectedIndex,AudioStyleBox.SelectedIndex,AudioPaletteBox.SelectedIndex,(int)ScreenSamplesSlider.Value,OutputModeBox.SelectedIndex==1,SpectrumDirectionBox.SelectedIndex);
                await App.AmbientLighting.StartAsync(effect, profile.BrightnessCode, profile.ColorsHex,options);
                App.Settings.Update(value => value.AmbientLighting=options);
                Notice("动态灯效已启动", "停止动态灯效或退出软件后恢复原来的常规效果。", InfoBarSeverity.Success);
            } else {
                await App.AmbientLighting.StopAsync();
                var applied = await _client.ApplyEffectAsync(profile, true, TimeSpan.FromSeconds(2));
                StartupDiagnostics.Write($"Keyboard lighting readback: effect={applied.EffectCode}, palette={applied.PaletteCode}, brightness={applied.BrightnessCode}, speed={applied.SpeedCode}.");
                Remember(applied); ApplyState(applied);
                if (profile.EffectCode != 10 && applied.BrightnessCode != profile.BrightnessCode)
                    Notice("灯效已应用，键盘保留原亮度",
                        $"请求 {profile.BrightnessCode + 1} 档，键盘读回 {applied.BrightnessCode + 1} 档。已保存实际设置。",
                        InfoBarSeverity.Warning);
                else
                    Notice("灯效已应用", "已从键盘读回确认，并保存设置。", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Lighting application failed.", exception);
            Notice("灯效应用失败", exception.Message, InfoBarSeverity.Error);
        }
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
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Keyboard lighting power change failed.", exception);
            Notice("灯效开关失败", exception.Message, InfoBarSeverity.Error);
        }
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
