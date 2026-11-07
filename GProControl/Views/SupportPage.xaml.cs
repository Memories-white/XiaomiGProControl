using GProControl.Services;
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
        catch(Exception exception) {StatusBar.Title="页面打开失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error;}
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
            StatusBar.Title="诊断摘要已复制"; StatusBar.Message="你可以检查内容后粘贴到反馈页面。"; StatusBar.Severity=InfoBarSeverity.Success;
        }
        catch(Exception exception) {StatusBar.Title="复制失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error;}
        finally {CopyDiagnosticsButton.IsEnabled=true;}
    }
}
