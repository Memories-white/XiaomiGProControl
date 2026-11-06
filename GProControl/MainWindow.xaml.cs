using GProControl.Services;
using GProControl.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace GProControl;

public sealed partial class MainWindow : Window
{
    private TrayIconService? _tray;
    private bool _exiting;
    private bool _closePromptRunning;
    private readonly Dictionary<string, Type> _pages = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["performance"] = typeof(PerformancePage),
        ["lighting"] = typeof(LightingPage),
        ["device"] = typeof(DevicePage),
        ["settings"] = typeof(SettingsPage),
    };

    public MainWindow()
    {
        StartupDiagnostics.Write("MainWindow initialization started.");
        InitializeComponent();
        StartupDiagnostics.Write("MainWindow XAML loaded.");
        SystemBackdrop = new MicaBackdrop();
        StartupDiagnostics.Write("Mica backdrop initialized.");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "GProControl.ico"));
        Navigation.SelectedItem = Navigation.MenuItems[0];
        ContentFrame.Navigate(typeof(DashboardPage));
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => _tray?.Dispose();
        StartupDiagnostics.Write("MainWindow initialization completed.");
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || App.IsPackageSmoke) return;
        args.Cancel = true;
        if (_closePromptRunning) return;
        _closePromptRunning = true;
        try
        {
            var action = App.Settings.Current.CloseAction;
            if (action == CloseAction.Exit) { ExitApplication(); return; }
            if (action == CloseAction.Background)
            {
                if (!MoveToBackground()) await ShowTrayErrorAsync();
                return;
            }
            var remember = new CheckBox { Content = "不再询问，记住本次选择" };
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock
            {
                Text = "退出软件，或保留在后台运行？后台运行后可点击通知区域图标重新打开。",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(remember);
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "关闭 G Pro Control",
                Content = content,
                PrimaryButtonText = "退出软件",
                SecondaryButtonText = "转入后台",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Secondary
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;
            if (result == ContentDialogResult.Secondary)
            {
                if (!MoveToBackground()) { await ShowTrayErrorAsync(); return; }
                if (remember.IsChecked == true)
                    App.Settings.Update(value => value.CloseAction = CloseAction.Background);
            }
            else
            {
                if (remember.IsChecked == true)
                    App.Settings.Update(value => value.CloseAction = CloseAction.Exit);
                ExitApplication();
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Close action failed; window remains available.", exception);
        }
        finally { _closePromptRunning = false; }
    }

    public bool MoveToBackground()
    {
        try
        {
            if (_tray is null)
            {
                _tray = new TrayIconService(Path.Combine(AppContext.BaseDirectory, "Assets", "GProControl.ico"));
                _tray.ShowRequested += RestoreFromBackground;
                _tray.ExitRequested += ExitApplication;
            }
            // Never hide the only window unless a notification icon is registered.
            if (!_tray.Show()) return false;
            AppWindow.Hide();
            StartupDiagnostics.Write("Window moved to background; notification icon registered.");
            return true;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Unable to enter background mode.", exception);
            return false;
        }
    }

    private void RestoreFromBackground()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
        _tray?.Hide();
        StartupDiagnostics.Write("Window restored from background.");
    }

    private void ExitApplication()
    {
        _exiting = true;
        _tray?.Dispose();
        Close();
    }

    private async Task ShowTrayErrorAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "暂时无法转入后台",
            Content = "通知区域图标未能创建，主窗口已保留。请稍后重试。",
            CloseButtonText = "知道了"
        };
        await dialog.ShowAsync();
    }

    internal async Task VerifyBackgroundLifecycleAsync()
    {
        await Task.Delay(700);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var hidden = MoveToBackground() && !IsWindowVisible(handle);
        await Task.Delay(300);
        RestoreFromBackground();
        var restored = IsWindowVisible(handle);
        StartupDiagnostics.Write($"Background lifecycle smoke: hidden={hidden}; restored={restored}");
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

    public void ApplyTheme(string mode)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = mode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag && _pages.TryGetValue(tag, out var page))
        {
            ContentFrame.Navigate(page, tag);
        }
    }
}
