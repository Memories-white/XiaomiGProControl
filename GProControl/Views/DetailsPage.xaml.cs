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
    private IReadOnlyList<HardwareDetailSection> _primarySections = [];
    private IReadOnlyList<HardwareDetailSection> _virtualSections = [];
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
            int gpuNumber = 0;
            _primarySections = details.Sections
                .Where(section => section.Kind != HardwareDetailSectionKind.VirtualDisplayAdapter)
                .Select(section => section.Kind == HardwareDetailSectionKind.GraphicsAdapter
                    ? section with { Title = ++gpuNumber == 1 ? "显卡" : $"显卡 {gpuNumber}" }
                    : section)
                .ToArray();
            _virtualSections = details.Sections
                .Where(section => section.Kind == HardwareDetailSectionKind.VirtualDisplayAdapter)
                .Select((section, index) => section with { Title = $"虚拟适配器 {index + 1}" })
                .ToArray();
            ArrangeSections(SectionGrid, _primarySections, SectionGrid.ActualWidth);
            VirtualAdapters.Visibility = _virtualSections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            VirtualAdapterHeader.Text = $"虚拟显示适配器（{_virtualSections.Count}）";
            ArrangeSections(VirtualAdapterGrid, _virtualSections, VirtualAdapterGrid.ActualWidth);
            RevealSerial_Toggled(this,new RoutedEventArgs());
            CopyButton.IsEnabled = true;
            SummaryText.Text = $"{details.Sections.Count} 个参数分组  ·  更新于 {details.ObservedAt:HH:mm:ss}";
            StatusBar.IsOpen = false;
        }
        catch(Exception exception) {StatusBar.Title="读取失败"; StatusBar.Message=exception.Message; StatusBar.Severity=InfoBarSeverity.Error;}
        finally {RefreshButton.IsEnabled=true;}
    }
    private void Header_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stackActions = e.NewSize.Width < 460;
        Grid.SetColumnSpan(HeaderText, stackActions ? 2 : 1);
        Grid.SetRow(HeaderActions, stackActions ? 1 : 0);
        Grid.SetColumn(HeaderActions, stackActions ? 0 : 1);
        Grid.SetColumnSpan(HeaderActions, stackActions ? 2 : 1);
    }

    private void SectionGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSectionColumns(SectionGrid, _primarySections, e.NewSize.Width);

    private void VirtualAdapterGrid_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSectionColumns(VirtualAdapterGrid, _virtualSections, e.NewSize.Width);

    private static int GetColumnCount(double width) => width >= 1120 ? 3 : width >= 660 ? 2 : 1;

    private void UpdateSectionColumns(Grid grid, IReadOnlyList<HardwareDetailSection> sections, double width)
    {
        if (width > 0 && sections.Count > 0 && grid.ColumnDefinitions.Count != GetColumnCount(width))
            ArrangeSections(grid, sections, width);
    }

    private void ArrangeSections(Grid grid, IReadOnlyList<HardwareDetailSection> sections, double width)
    {
        int columns = GetColumnCount(width);
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        for (int column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition());

        var template = (DataTemplate)Resources["SectionTemplate"];
        for (int index = 0; index < sections.Count; index++)
        {
            if (index % columns == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = new ContentPresenter { Content = sections[index], ContentTemplate = template };
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
            grid.Children.Add(card);
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
