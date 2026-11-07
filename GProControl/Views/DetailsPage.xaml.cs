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
    private int _columns;
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
        StatusBar.IsOpen = true;
        StatusBar.Title="正在读取设备参数"; StatusBar.Message=""; StatusBar.Severity=InfoBarSeverity.Informational;
        try
        {
            var details=await new HardwareDetailsService().CaptureAsync(force);
            if(!_loaded) return;
            _details = details;
            ArrangeSections(SectionGrid.ActualWidth >= 740 ? 2 : 1);
            RevealSerial_Toggled(this,new RoutedEventArgs());
            CopyButton.IsEnabled = true;
            SummaryText.Text = $"{details.Sections.Count} 个参数分组 · {details.ObservedAt:HH:mm:ss} 更新 · 复制报告不含序列号";
            StatusBar.IsOpen = false;
        }
        catch(Exception exception) {StatusBar.Title="读取失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error;}
        finally {RefreshButton.IsEnabled=true;}
    }
    private void SectionGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int columns = e.NewSize.Width >= 740 ? 2 : 1;
        if (_details is not null && columns != _columns) ArrangeSections(columns);
    }

    private void ArrangeSections(int columns)
    {
        if (_details is null) return;
        _columns = columns;
        SectionGrid.Children.Clear();
        SectionGrid.RowDefinitions.Clear();
        SectionGrid.ColumnDefinitions.Clear();
        for (int column = 0; column < columns; column++)
            SectionGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var template = (DataTemplate)Resources["SectionTemplate"];
        for (int index = 0; index < _details.Sections.Count; index++)
        {
            if (index % columns == 0)
                SectionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = new ContentPresenter { Content = _details.Sections[index], ContentTemplate = template };
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
            SectionGrid.Children.Add(card);
        }
    }
    private void RevealSerial_Toggled(object sender,RoutedEventArgs e)
    { if(SerialText is not null) SerialText.Text=RevealSerialToggle.IsOn ? _details?.SerialNumber ?? "未读取" : "隐藏 · 仅在本机显示"; }
    private void Copy_Click(object sender,RoutedEventArgs e)
    {
        if(_details is null) return;
        try {var data=new DataPackage(); data.SetText(_details.ToReport()); Clipboard.SetContent(data); StatusBar.Title="参数报告已复制"; StatusBar.Message="报告不含设备序列号。"; StatusBar.Severity=InfoBarSeverity.Success; StatusBar.IsOpen=true;}
        catch(Exception exception) {StatusBar.Title="复制失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error; StatusBar.IsOpen=true;}
    }
}
