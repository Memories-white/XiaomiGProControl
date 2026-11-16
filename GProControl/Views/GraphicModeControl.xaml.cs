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
        UpdateRestartNotice();
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
            ApplyModeReadback(result);
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
            var confirmation = $"固件当前设置为{ModeName(_mode.Value)}。切换到{ModeName(mode)}后，必须重启 Windows 才生效。请先保存工作。";
            if (mode == 0)
            {
                confirmation += "\n\n重启后，Windows 可能需要几分钟安装并加载 Intel 核显驱动。期间内屏的分辨率或刷新率可能异常，任务管理器也可能暂时只显示一个 GPU。请等待驱动安装完成后再检查。";
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"切换到{ModeName(mode)}？",
                Content = new TextBlock { Text = confirmation, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "切换，稍后重启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !_loaded) return;
            StatusText.Text = "正在写入并读取固件确认…";
            var result = await App.HardwareControl.SetGraphicModeAsync(mode);
            if (result.Submitted)
                App.PendingGraphicModeChange = (mode, result.Verified);
            if (!_loaded) return;
            _mode = result.Verified ? result.ReadbackValue : null;
            ModeText.Text = _mode is int actual ? $"固件设置：{ModeName(actual)}" : "切换结果未确认，请刷新";
            StatusText.Text = result.Message;
            UpdateRestartNotice();
            if (result.Submitted)
                await RefreshAfterSwitchAsync(mode);
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

    private async Task RefreshAfterSwitchAsync(int targetMode)
    {
        StatusText.Text = "正在自动刷新固件设置…";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            if (!_loaded) return;

            var result = await App.HardwareControl.GetGraphicModeAsync();
            if (!_loaded) return;
            if (result.Verified && result.ReadbackValue == targetMode)
            {
                ApplyModeReadback(result);
                return;
            }

            if (attempt == 2 && result.Verified)
                ApplyModeReadback(result);
        }

        StatusText.Text = App.PendingGraphicModeChange is { Confirmed: true }
            ? "固件已确认目标模式，重启 Windows 后生效。"
            : "自动刷新后仍未确认目标模式，请稍后点击「刷新」。";
    }

    private void ApplyModeReadback(HardwareActionResult result)
    {
        _mode = result.Verified ? result.ReadbackValue : null;
        ModeText.Text = _mode is int mode ? $"固件设置：{ModeName(mode)}" : "当前模式未确认";
        StatusText.Text = result.Message;
        if (App.PendingGraphicModeChange is { } pending && _mode is int savedMode)
            App.PendingGraphicModeChange = (pending.TargetMode, savedMode == pending.TargetMode);
        UpdateRestartNotice();
    }

    private void UpdateRestartNotice()
    {
        if (App.PendingGraphicModeChange is not { } pending)
        {
            RestartNotice.IsOpen = false;
            return;
        }

        var target = ModeName(pending.TargetMode);
        RestartNotice.Title = pending.Confirmed ? "需要重启生效" : "等待固件确认";
        RestartNotice.Message = pending.Confirmed
            ? $"已保存为{target}。请保存工作，然后重启 Windows 以应用此模式。"
            : $"已提交切换到{target}。固件确认后即可重启 Windows 生效。";
        RestartNotice.IsOpen = true;
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
