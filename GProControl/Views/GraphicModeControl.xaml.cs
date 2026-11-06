using GProControl.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public sealed partial class GraphicModeControl : UserControl
{
    private bool _loaded;
    private bool _busy;
    private int? _mode;

    public GraphicModeControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _loaded = false;
            App.HardwareControl.StateChanged -= OnHardwareStateChanged;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        App.HardwareControl.StateChanged += OnHardwareStateChanged;
        await RefreshAsync();
    }

    private void OnHardwareStateChanged(object? sender, HardwareControlState state) =>
        DispatcherQueue.TryEnqueue(() => { if (_loaded) UpdateButtons(); });

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!_loaded || _busy) return;
        _busy = true;
        _mode = null;
        UpdateButtons();
        try
        {
            var state = await App.HardwareControl.RefreshAsync();
            if (!state.CanArmWrites)
            {
                ModeText.Text = "暂时无法读取";
                StatusText.Text = state.Message;
                return;
            }
            StatusText.Text = "正在自动运行设备自检…";
            var probe = await App.HardwareControl.EnsureWmiTransportAsync();
            if (!_loaded) return;
            if (!probe.Verified)
            {
                ModeText.Text = "设备自检未通过";
                StatusText.Text = probe.Message + " 可在设备控制中重新自检。";
                return;
            }
            var result = await App.HardwareControl.GetGraphicModeAsync();
            if (!_loaded) return;
            _mode = result.Verified ? result.ReadbackValue : null;
            ModeText.Text = _mode is int mode ? $"固件设置：{ModeName(mode)}" : "当前模式未确认";
            StatusText.Text = result.Message;
            RestartNotice.IsOpen = App.GraphicModeRestartPending;
        }
        catch (Exception exception)
        {
            ModeText.Text = "读取失败";
            StatusText.Text = exception.Message;
        }
        finally
        {
            _busy = false;
            if (_loaded) UpdateButtons();
        }
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_loaded || _mode is null || !App.HardwareControl.State.CanWrite
            || sender is not Button button || !int.TryParse(button.Tag?.ToString(), out var mode)
            || mode is not 0 and not 1 || mode == _mode) return;
        _busy = true;
        UpdateButtons();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"切换到{ModeName(mode)}？",
                Content = $"固件当前设置为{ModeName(_mode.Value)}。切换到{ModeName(mode)}后，必须重启 Windows 才生效。请先保存工作。",
                PrimaryButtonText = "切换，稍后重启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_loaded) return;
            StatusText.Text = "正在写入并读取固件确认…";
            var result = await App.HardwareControl.SetGraphicModeAsync(mode);
            if (result.Verified && result.Submitted)
                App.GraphicModeRestartPending = true;
            if (!_loaded) return;
            _mode = result.Verified ? result.ReadbackValue : null;
            ModeText.Text = _mode is int actual ? $"固件设置：{ModeName(actual)}" : "切换结果未确认，请刷新";
            StatusText.Text = result.Message;
            RestartNotice.IsOpen = App.GraphicModeRestartPending;
        }
        catch (Exception exception)
        {
            _mode = null;
            StatusText.Text = $"切换失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
            if (_loaded) UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var enabled = !_busy && _loaded && _mode is not null
            && App.HardwareControl.State.CanWrite && App.HardwareControl.IsWmiTransportVerified;
        HybridButton.Content = _mode == 0 ? "混合输出 · 已设置" : "混合输出";
        DirectButton.Content = _mode == 1 ? "独显直连 · 已设置" : "独显直连";
        HybridButton.IsEnabled = enabled && _mode != 0;
        DirectButton.IsEnabled = enabled && _mode != 1;
        RefreshButton.IsEnabled = !_busy;
    }

    private static string ModeName(int mode) => mode == 0 ? "混合输出" : "独显直连";
}
