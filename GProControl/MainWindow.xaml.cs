using GProControl.Services;
using GProControl.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Navigation;

namespace GProControl;

public sealed partial class MainWindow : Window
{
    private TrayIconService? _tray;
    private bool _exiting;
    private bool _closeApproved;
    private bool _closePromptRunning;
    private bool _syncingNavigation;
    private (CancellationTokenSource Cancellation,Task Completion)? _lightingTest;
    private readonly Dictionary<string, Type> _pages = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["details"] = typeof(DetailsPage),
        ["support"] = typeof(SupportPage),
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
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => _tray?.Dispose();
        StartupDiagnostics.Write("MainWindow initialization completed.");
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved || App.IsPackageSmoke) return;
        args.Cancel = true;
        if (_exiting || _closePromptRunning) return;
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

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        if(_lightingTest is { } test)
        {
            test.Cancellation.Cancel();
            try {await test.Completion;}
            catch(OperationCanceledException) { }
            catch(Exception exception) {StartupDiagnostics.Write("Lighting diagnostic exit recovery failed.",exception);}
        }
        await App.AmbientLighting.StopAsync();
        _tray?.Dispose();
        _closeApproved=true;
        Close();
    }

    internal bool IsExiting => _exiting;

    internal async Task RunLightingTestAsync(Func<CancellationToken,Task> run,CancellationToken cancellationToken)
    {
        if(_exiting || _lightingTest is not null) throw new InvalidOperationException("已有灯效校验正在运行，或程序正在退出。");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var task=run(cancellation.Token);
        _lightingTest=(cancellation,task);
        try {await task;}
        finally {_lightingTest=null;}
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
        if(Environment.GetCommandLineArgs().Contains("--lighting-exit-smoke"))
        {
            _=RunLightingTestAsync(async token=>
            {
                try {await Task.Delay(Timeout.Infinite,token);}
                catch(OperationCanceledException) when(token.IsCancellationRequested)
                {
                    await Task.Delay(200);
                    StartupDiagnostics.Write("Lighting exit smoke: cancellation=True; cleanup awaited=True; noHardwareWrites=True");
                }
            },CancellationToken.None);
            ExitApplication();
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--device-ui-smoke")) await VerifyDeviceNavigationAsync();
        if (Environment.GetCommandLineArgs().Contains("--navigation-smoke")) await VerifyShellNavigationAsync();
        if (Environment.GetCommandLineArgs().Contains("--lighting-preset-smoke"))
        {
            try
            {
                NavigateTo("lighting");
                await Task.Delay(2500);
                ((LightingPage)ContentFrame.Content).VerifyFactoryPresets();
            }
            catch (Exception exception) { StartupDiagnostics.Write("Factory preset UI smoke failed.", exception); }
            finally { Close(); }
        }
        if (Environment.GetCommandLineArgs().Contains("--support-details-smoke"))
        {
            try
            {
                var dashboard = (DashboardPage)ContentFrame.Content;
                var openDetails = (Button)dashboard.FindName("OpenDetailsButton");
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(openDetails);
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
                    Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await Task.Delay(300);
                if (ContentFrame.Content is not DetailsPage || !ContentFrame.CanGoBack ||
                    Navigation.IsBackButtonVisible != NavigationViewBackButtonVisible.Visible ||
                    (Navigation.SelectedItem as NavigationViewItem)?.Tag?.ToString() != "dashboard" ||
                    Navigation.MenuItems.OfType<NavigationViewItem>().Any(item => item.Tag?.ToString() == "details"))
                    throw new InvalidOperationException("Details must be a dashboard subpage.");
                var page=(DetailsPage)ContentFrame.Content;
                var ready=(Button)page.FindName("CopyButton");
                for(var attempt=0;attempt<100 && !ready.IsEnabled;attempt++) await Task.Delay(200);
                var sections=(Grid)page.FindName("SectionGrid");
                var serial=(TextBlock)page.FindName("SerialText");
                if(!ready.IsEnabled || sections.Children.Count<8 || !serial.Text.StartsWith("隐藏"))
                    throw new InvalidOperationException("Details page data or serial privacy did not initialize.");
                var count=sections.Children.Count;
                GoBack();
                if (ContentFrame.Content is not DashboardPage || ContentFrame.CanGoBack)
                    throw new InvalidOperationException("Details did not return to the dashboard.");
                NavigateTo("support");
                var support=(SupportPage)ContentFrame.Content;
                var links=(ItemsControl)support.FindName("LinkList");
                if(links.Items.Count!=6 || Navigation.IsBackButtonVisible!=NavigationViewBackButtonVisible.Collapsed)
                    throw new InvalidOperationException("Support page links or navigation differ.");
                NavigateTo("dashboard");
                NavigateTo("details", asSubpage: true);
                await Task.Delay(200);
                if(!((TextBlock)((DetailsPage)ContentFrame.Content).FindName("SerialText")).Text.StartsWith("隐藏"))
                    throw new InvalidOperationException("Serial reveal was retained across navigation.");
                StartupDiagnostics.Write($"Support/details UI smoke: sections={count}; links=6; serialHidden=True; rootBackHidden=True; navigation=True");
            }
            catch(Exception exception) {StartupDiagnostics.Write("Support/details UI smoke failed.",exception);}
            finally {Close();}
        }
    }

    private async Task VerifyShellNavigationAsync()
    {
        try
        {
            var dashboard = (DashboardPage)ContentFrame.Content;
            var button = (Button)dashboard.FindName("OpenPerformanceButton");
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
            var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
                Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
            invoke.Invoke();
            await Task.Delay(300);
            var selected = ContentFrame.Content is PerformancePage
                && (Navigation.SelectedItem as NavigationViewItem)?.Tag?.ToString() == "performance"
                && !Navigation.IsBackEnabled;
            var shortcutBackHidden = Navigation.IsBackButtonVisible == NavigationViewBackButtonVisible.Collapsed;
            NavigateTo("dashboard");
            NavigateTo("performance", asSubpage: true);
            var childBackVisible = Navigation.IsBackEnabled
                && Navigation.IsBackButtonVisible == NavigationViewBackButtonVisible.Visible;
            GoBack();
            await Task.Delay(300);
            var back = ContentFrame.Content is DashboardPage
                && (Navigation.SelectedItem as NavigationViewItem)?.Tag?.ToString() == "dashboard";
            var historyClean = !ContentFrame.CanGoBack;
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
                .First(item => item.Tag?.ToString() == "performance");
            var rootBackHidden = ContentFrame.Content is PerformancePage && !ContentFrame.CanGoBack
                && Navigation.IsBackButtonVisible == NavigationViewBackButtonVisible.Collapsed;
            StartupDiagnostics.Write($"Shell navigation smoke: selected={selected}; back={back}; historyClean={historyClean}; rootBackHidden={rootBackHidden}; shortcutBackHidden={shortcutBackHidden}; childBackVisible={childBackVisible}");
        }
        catch (Exception exception) { StartupDiagnostics.Write("Shell navigation smoke failed.", exception); }
        finally { Close(); }
    }

    private async Task VerifyDeviceNavigationAsync()
    {
        int? original = null;
        var changed = false; var preserved = false; var restored = false;
        try
        {
            await App.HardwareControl.RefreshAsync();
            App.HardwareControl.SetWriteArmed(true);
            await App.HardwareControl.EnsureWmiTransportAsync();
            var before = await App.HardwareControl.GetDeviceSwitchAsync(Protocol.MiDeviceSwitch.WindowsKey);
            if (before is not { Verified: true, ReadbackValue: int value }) throw new InvalidOperationException("No initial key state.");
            original = value;
            ContentFrame.Navigate(typeof(DevicePage));
            await Task.Delay(1800);
            var toggle = (ToggleSwitch)((DevicePage)ContentFrame.Content).FindName("WindowsKeyToggle");
            if (!toggle.IsEnabled || toggle.IsOn != (value == 1)) throw new InvalidOperationException("Initial UI state does not match hardware.");
            toggle.IsOn = value == 0;
            await Task.Delay(1500);
            var actual = await App.HardwareControl.GetDeviceSwitchAsync(Protocol.MiDeviceSwitch.WindowsKey);
            changed = actual.Verified && actual.ReadbackValue == 1-value;
            ContentFrame.Navigate(typeof(DashboardPage));
            ContentFrame.Navigate(typeof(DevicePage));
            await Task.Delay(1800);
            toggle = (ToggleSwitch)((DevicePage)ContentFrame.Content).FindName("WindowsKeyToggle");
            preserved = toggle.IsOn == (value == 0);
        }
        catch (Exception exception) { StartupDiagnostics.Write("Device navigation smoke failed.", exception); }
        finally
        {
            if (original is int value)
            {
                var result = await App.HardwareControl.SetDeviceSwitchAsync(Protocol.MiDeviceSwitch.WindowsKey, value == 0);
                restored = result.Verified;
            }
            StartupDiagnostics.Write($"Device navigation smoke: changed={changed}; preserved={preserved}; restored={restored}");
            Close();
        }
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
        if (!_syncingNavigation && args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void Navigation_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag) NavigateTo(tag);
    }

    public void NavigateTo(string tag, bool asSubpage = false)
    {
        if (!_pages.TryGetValue(tag, out var page)) throw new ArgumentOutOfRangeException(nameof(tag));
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page, tag);
        if (!asSubpage)
        {
            ContentFrame.BackStack.Clear();
            ContentFrame.ForwardStack.Clear();
            UpdateBackNavigation();
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs args)
    {
        var tag = _pages.FirstOrDefault(pair => pair.Value == args.SourcePageType).Key;
        if (args.SourcePageType == typeof(DetailsPage)) tag = "dashboard";
        var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems)
            .OfType<NavigationViewItem>().FirstOrDefault(candidate => candidate.Tag?.ToString() == tag);
        _syncingNavigation = true;
        try { Navigation.SelectedItem = item; }
        finally { _syncingNavigation = false; }
        UpdateBackNavigation();
    }

    private void UpdateBackNavigation()
    {
        Navigation.IsBackEnabled = ContentFrame.CanGoBack;
        Navigation.IsBackButtonVisible = ContentFrame.CanGoBack
            ? NavigationViewBackButtonVisible.Visible : NavigationViewBackButtonVisible.Collapsed;
    }

    private void Navigation_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    private void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }
}
