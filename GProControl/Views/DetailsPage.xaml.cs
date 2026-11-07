using GProControl.Models;
using GProControl.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace GProControl.Views;
public sealed partial class DetailsPage : Page
{
    private HardwareDetails? _details;
    private bool _loaded;
    public DetailsPage()
    {
        InitializeComponent();
        Loaded += async (_,_) => { _loaded=true; await RefreshAsync(false); };
        Unloaded += (_,_) => _loaded=false;
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e) => await RefreshAsync(true);
    private async Task RefreshAsync(bool force)
    {
        RefreshButton.IsEnabled=false; CopyButton.IsEnabled=false;
        StatusBar.Title="正在读取设备参数"; StatusBar.Message=""; StatusBar.Severity=InfoBarSeverity.Informational;
        try
        {
            var details=await new HardwareDetailsService().CaptureAsync(force);
            if(!_loaded) return;
            _details=details; SectionList.ItemsSource=details.Sections;
            RevealSerial_Toggled(this,new RoutedEventArgs());
            CopyButton.IsEnabled=true; StatusBar.Title="设备参数已更新";
            StatusBar.Message=$"读取时间 {details.ObservedAt:HH:mm:ss} · 复制报告不含序列号。";
            StatusBar.Severity=InfoBarSeverity.Success;
        }
        catch(Exception exception) {StatusBar.Title="读取失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error;}
        finally {RefreshButton.IsEnabled=true;}
    }
    private void RevealSerial_Toggled(object sender,RoutedEventArgs e)
    { if(SerialText is not null) SerialText.Text=RevealSerialToggle.IsOn ? _details?.SerialNumber ?? "未读取" : "隐藏 · 仅在本机显示"; }
    private void Copy_Click(object sender,RoutedEventArgs e)
    {
        if(_details is null) return;
        try {var data=new DataPackage(); data.SetText(_details.ToReport()); Clipboard.SetContent(data); StatusBar.Title="参数报告已复制";}
        catch(Exception exception) {StatusBar.Title="复制失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error;}
    }
}
