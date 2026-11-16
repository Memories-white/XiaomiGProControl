using GProControl.Services;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace GProControl.Views;
public sealed partial class SupportPage : Page
{
    private readonly string _version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
    public SupportPage()
    {
        InitializeComponent(); LinkList.ItemsSource=SupportLinks.All;
        RoutineNotice.ApplyVisibility(StatusBar, routine: true);
        VersionText.Text="G Pro Control "+_version;
        Loaded += (_,_) => {
            var bridge=new OriginalControlCenterProbe().Capture();
            BridgeText.Text=$"原厂组件：{bridge.Version ?? "未安装"} · 后台服务：{(bridge.IsTransportAvailable ? "可用" : "未就绪")}";
        };
    }
    private async void OpenLink_Click(object sender,RoutedEventArgs e)
    {if(sender is Button {DataContext:SupportLink link}) await OpenAsync(link.Address);}
    private async void OpenAccount_Click(object sender,RoutedEventArgs e) => await OpenAsync("https://account.xiaomi.com/");
    private async Task OpenAsync(string address)
    {
        try {if(!await Launcher.LaunchUriAsync(new Uri(address))) throw new IOException("默认浏览器未能打开页面。");}
        catch(Exception exception) { ShowResult("页面打开失败", exception.Message, InfoBarSeverity.Error); }
    }
    private async void CopyDiagnostics_Click(object sender,RoutedEventArgs e)
    {
        CopyDiagnosticsButton.IsEnabled=false;
        try
        {
            var details=await new HardwareDetailsService().CaptureAsync();
            var bridge=new OriginalControlCenterProbe().Capture();
            var data=new DataPackage();
            data.SetText($"G Pro Control {_version}\n原厂组件：{bridge.Version ?? "未检测到"}\n后台通信可用：{bridge.IsTransportAvailable}\n\n"+details.ToReport());
            Clipboard.SetContent(data);
            ShowResult("诊断摘要已复制", "你可以检查内容后粘贴到反馈页面。", InfoBarSeverity.Success);
        }
        catch(Exception exception) { ShowResult("复制失败", exception.Message, InfoBarSeverity.Error); }
        finally {CopyDiagnosticsButton.IsEnabled=true;}
    }

    private async void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not { } window) return;
        ExportLogButton.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker(window.AppWindow.Id)
            {
                SuggestedFileName = $"GProControl-{DateTime.Now:yyyyMMdd-HHmmss}",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                DefaultFileExtension = ".txt",
                FileTypeChoices = { { "文本日志", new List<string> { ".txt" } } }
            };
            var selected = await picker.PickSaveFileAsync();
            if (selected is null) return;

            await StartupDiagnostics.ExportAsync(selected.Path, _version);
            ShowResult("运行日志已导出", selected.Path, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Runtime log export failed.", exception);
            ShowResult("运行日志导出失败",
                exception is FileNotFoundException ? "暂时没有可导出的运行日志。" : exception.Message,
                InfoBarSeverity.Error);
        }
        finally
        {
            ExportLogButton.IsEnabled = true;
        }
    }

    private void ShowResult(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        RoutineNotice.ApplyVisibility(StatusBar, routine: false);
        StatusBar.IsOpen = true;
    }
}
