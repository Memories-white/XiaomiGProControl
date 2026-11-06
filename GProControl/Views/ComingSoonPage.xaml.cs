using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace GProControl.Views;

public sealed partial class ComingSoonPage : Page
{
    public ComingSoonPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        TitleText.Text = e.Parameter as string == "lighting" ? "灯效正在重建" : "设备控制正在重建";
    }
}
