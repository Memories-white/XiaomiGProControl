using GProControl.Services;
using GProControl.Protocol;
using GProControl.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using Microsoft.UI.System;

namespace GProControl;

public sealed partial class MainWindow : Window
{
    private TrayIconService? _tray;
    private bool _exiting;
    private bool _closeApproved;
    private bool _closePromptRunning;
    private bool _syncingNavigation;
    private Storyboard? _sidebarTransition;
    private readonly HashSet<FrameworkElement> _queuedNavigationIcons = new();
    private ThemeSettings? _themeSettings;
    internal AppAppearance ActiveAppearance { get; private set; }
    internal bool IsRogAppearanceActive => ActiveAppearance == AppAppearance.Rog;
    private bool IsGamingAppearanceActive => ActiveAppearance != AppAppearance.Windows;
    internal event EventHandler? AppearanceChanged;
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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "GProControl.ico"));
        Navigation.SelectedItem = Navigation.MenuItems[0];
        AppWindow.Closing += OnClosing;
        Closed += (_, _) =>
        {
            _themeSettings = null;
            _tray?.Dispose();
        };
        try
        {
            var themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
            _themeSettings = themeSettings;
            themeSettings.Changed += OnWindowThemeChanged;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Window theme monitoring unavailable.", exception);
        }
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
        if (Environment.GetCommandLineArgs().Contains("--rog-ui-smoke"))
        {
            await VerifyRogDashboardAsync();
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--dashboard-startup-smoke"))
        {
            await VerifyDashboardStartupAsync();
            return;
        }

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
                    PageBackButton.Visibility != Visibility.Visible ||
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
                if(links.Items.Count!=6 || PageBackButton.Visibility!=Visibility.Collapsed)
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

    private async Task VerifyRogDashboardAsync()
    {
        try
        {
            App.Settings.Current.Appearance = AppAppearance.Rog;
            ApplyAppearance(adjustWindowSize: true);
            await Task.Delay(400);
            if (GamingSidebar.Visibility != Visibility.Visible || Navigation.IsPaneVisible
                || RogWorkspaceBackdrop.Visibility != Visibility.Visible || RogWorkspaceBackdrop.ActualWidth <= 0
                || ContentFrame.Content is not DashboardPage page
                || page.FindName("RogDashboard") is not Grid { ActualWidth: > 0, Visibility: Visibility.Visible }
                || page.FindName("ClassicDashboard") is not Grid { Visibility: Visibility.Collapsed })
                throw new InvalidOperationException("ROG dashboard and sidebar did not load.");
            RequireCenteredDashboard(page, "RogDashboard");
            RequireFittedDashboard(page, 1060, 684);
            await RequireStableTelemetryBarAsync(page);

            NavigateTo("details", asSubpage: true);
            if (ContentFrame.Content is not DetailsPage || PageBackButton.Visibility != Visibility.Visible
                || !ReferenceEquals(VisualTreeHelper.GetParent(PageBackButton), VisualTreeHelper.GetParent(ContentFrame)))
                throw new InvalidOperationException("ROG details navigation did not provide a back button.");
            await Task.Delay(200);
            RequireRogWorkspace("details");
            await RequireDetailsLayoutAsync((DetailsPage)ContentFrame.Content);
            var backPeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(PageBackButton);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)backPeer.GetPattern(
                Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
            if (ContentFrame.Content is not DashboardPage || GamingNavigation.SelectedIndex != 0)
                throw new InvalidOperationException("ROG navigation did not return to the dashboard.");

            foreach (var tag in new[] { "performance", "lighting", "device", "support", "settings" })
            {
                var item = GamingNavigation.Items.OfType<ListViewItem>().Single(candidate => candidate.Tag?.ToString() == tag);
                GamingNavigation.SelectedItem = item;
                await Task.Delay(110);
                if (AnimationsEnabled() && item.Content is StackPanel panel && panel.Children[0] is Grid icon
                    && ElementCompositionPreview.GetElementVisual(icon).TryGetAnimationController("Scale") is null)
                    throw new InvalidOperationException($"The sidebar icon for {tag} did not animate.");
                RequireRogWorkspace(tag);
                if (ContentFrame.Content is PerformancePage performance)
                    await RequirePerformanceLayoutAsync(performance);
            }

            var settingsWorkspace = (RogPageLayout)((Page)ContentFrame.Content).FindName("Workspace");
            var switchProbe = new ToggleSwitch { OnContent = "开启", OffContent = "关闭" };
            RogToggleSwitch.SetAdapt(switchProbe, true);
            settingsWorkspace.Children.Add(switchProbe);
            await Task.Delay(120);
            if (!ReferenceEquals(switchProbe.Template, Application.Current.Resources["RogToggleTemplate"]))
                throw new InvalidOperationException("ROG switch template did not load.");
            var switchPeer = new Microsoft.UI.Xaml.Automation.Peers.ToggleSwitchAutomationPeer(switchProbe);
            ((Microsoft.UI.Xaml.Automation.Provider.IToggleProvider)switchPeer.GetPattern(
                Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Toggle)).Toggle();
            await Task.Delay(100);
            if (!switchProbe.IsOn)
                throw new InvalidOperationException("ROG switch did not respond to the toggle action.");

            App.Settings.Current.Appearance = AppAppearance.Windows;
            ApplyAppearance();
            if (settingsWorkspace.IsRogLayout || !settingsWorkspace.Children.OfType<Border>()
                .Any(card => ReferenceEquals(card.Style, Application.Current.Resources["DashboardCardStyle"]))
                || ReferenceEquals(switchProbe.Template, Application.Current.Resources["RogToggleTemplate"]) || !switchProbe.IsOn)
                throw new InvalidOperationException("Settings did not return to its standard card layout.");
            settingsWorkspace.Children.Remove(switchProbe);

            var previousSidebarState = App.Settings.Current.GamingSidebarCollapsed;
            App.Settings.Current.Appearance = AppAppearance.Legion;
            App.Settings.Current.GamingSidebarCollapsed = true;
            ApplyAppearance(adjustWindowSize: true);
            if (!settingsWorkspace.Children.OfType<Border>()
                .Any(card => ReferenceEquals(card.Style, Application.Current.Resources["LegionSectionStyle"])))
                throw new InvalidOperationException("Legion settings styling did not load.");
            NavigateTo("dashboard");
            await Task.Delay(200);
            if (ContentFrame.Content is not DashboardPage legionPage
                || legionPage.FindName("LegionDashboard") is not Grid { ActualWidth: > 0, Visibility: Visibility.Visible }
                || GamingSidebar.Width != 56 || GamingNavigation.SelectedIndex != 0
                || RogWorkspaceBackdrop.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Legion dashboard or compact navigation did not load.");
            RequireCenteredDashboard(legionPage, "LegionDashboard");
            RequireFittedDashboard(legionPage, 1090, 624);
            var normalWindowSize = AppWindow.Size;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 460));
            await Task.Delay(180);
            RequireCenteredDashboard(legionPage, "LegionDashboard");
            var compactScroller = (ScrollViewer)legionPage.FindName("DashboardScroller");
            if (compactScroller.ScrollableHeight <= 0)
                throw new InvalidOperationException("The dashboard could not scroll in a short window.");
            AppWindow.Resize(normalWindowSize);
            if (GamingNavigation.SelectedItem is not ListViewItem activeItem || activeItem.ActualWidth <= 0
                || activeItem.ActualWidth > GamingSidebar.ActualWidth - 4
                || GamingSidebarClip.Rect.Width != GamingSidebar.ActualWidth)
                throw new InvalidOperationException("The selected compact sidebar item exceeded the sidebar bounds.");
            if (VisualTreeHelper.GetChild(activeItem, 0) is not Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter
                { SelectionIndicatorVisualEnabled: true, CornerRadius.TopLeft: 7 })
                throw new InvalidOperationException("The sidebar selection marker or rounded background was not loaded.");
            if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is { } display
                && (AppWindow.Size.Width > display.WorkArea.Width || AppWindow.Size.Height > display.WorkArea.Height))
                throw new InvalidOperationException("Theme resizing exceeded the available display area.");
            App.Settings.Current.GamingSidebarCollapsed = false;
            ApplySidebarWidth();
            if (GamingSidebar.Width != 176)
                throw new InvalidOperationException("The gaming sidebar could not expand.");
            App.Settings.Current.GamingSidebarCollapsed = previousSidebarState;
            App.Settings.Current.Appearance = AppAppearance.Windows;
            ApplyAppearance();
            await Task.Delay(200);
            if (GamingSidebar.Visibility != Visibility.Collapsed || !Navigation.IsPaneVisible
                || ContentFrame.Content is not DashboardPage classic
                || classic.FindName("ClassicDashboard") is not Grid { Visibility: Visibility.Visible })
                throw new InvalidOperationException("The standard layout did not return.");

            StartupDiagnostics.Write("ROG and Legion dashboards, sidebar, and page layout smoke passed.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("ROG dashboard smoke failed.", exception);
            Environment.ExitCode = 1;
        }
        finally
        {
            Close();
        }
    }

    private static void RequireCenteredDashboard(DashboardPage page, string name)
    {
        var root = (Grid)page.FindName("DashboardLayoutRoot");
        var dashboard = (Grid)page.FindName(name);
        var offset = dashboard.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var expectedX = (root.ActualWidth - dashboard.ActualWidth) / 2;
        var expectedY = (root.ActualHeight - dashboard.ActualHeight) / 2;
        if (Math.Abs(offset.X - expectedX) > 2 || Math.Abs(offset.Y - expectedY) > 2)
            throw new InvalidOperationException($"{name} was not centered in its layout area.");
    }

    private void RequireRogWorkspace(string tag)
    {
        if (ContentFrame.Content is not Page page || page.FindName("Workspace") is not RogPageLayout workspace
            || !workspace.IsRogLayout || workspace.ActualWidth <= 0
            || !workspace.Children.OfType<Border>()
                .Any(card => ReferenceEquals(card.Style, Application.Current.Resources["RogSectionStyle"])))
            throw new InvalidOperationException($"ROG layout did not load on {tag}.");
    }

    private async Task RequirePerformanceLayoutAsync(PerformancePage page)
    {
        var workspace = (RogPageLayout)page.FindName("Workspace");
        var modes = (Grid)page.FindName("ModeSelector");
        var firstMode = (RadioButton)modes.Children[0];
        var lastMode = (RadioButton)modes.Children[3];
        var gpu = (Border)page.FindName("GpuOverclockCard");
        var output = (Border)page.FindName("GraphicModeCard");
        var screen = (Border)page.FindName("ScreenAccelerationCard");
        var origin = new Windows.Foundation.Point();

        var appearance = App.Settings.Current.Appearance;
        var previouslyChecked = modes.Children.OfType<RadioButton>().FirstOrDefault(option => option.IsChecked == true);
        try
        {
            firstMode.IsChecked = true;
            lastMode.IsChecked = true;
            if (firstMode.IsChecked == true || GetModeSelection(lastMode).Visibility != Visibility.Visible)
                throw new InvalidOperationException("The performance mode buttons did not show a single selected option.");

            foreach (var theme in new[] { AppAppearance.Rog, AppAppearance.Legion, AppAppearance.Windows })
            {
                App.Settings.Current.Appearance = theme;
                ApplyAppearance();
                var selection = GetModeSelection(lastMode);
                var outline = (Border)selection.Children[1];
                var color = ((SolidColorBrush)outline.BorderBrush).Color;
                if (selection.Visibility != Visibility.Visible || outline.CornerRadius.TopLeft != (theme == AppAppearance.Rog ? 2 : 6)
                    || (theme == AppAppearance.Rog && color != Color.FromArgb(255, 250, 87, 115))
                    || (theme == AppAppearance.Legion && color != Color.FromArgb(255, 91, 209, 246))
                    || (theme == AppAppearance.Windows && lastMode.ReadLocalValue(Control.BorderBrushProperty) != DependencyProperty.UnsetValue))
                    throw new InvalidOperationException($"The selected performance mode did not follow {theme}.");

                var dialog = page.CreateGpuOverclockDialog();
                if (dialog.DefaultButton != ContentDialogButton.Close ||
                    (theme != AppAppearance.Windows && (!ReferenceEquals(dialog.Background, gpu.Background)
                        || dialog.CornerRadius.TopLeft != gpu.CornerRadius.TopLeft
                        || !ReferenceEquals(dialog.PrimaryButtonStyle, Application.Current.Resources[
                            theme == AppAppearance.Rog ? "RogSectionButtonStyle" : "LegionSectionButtonStyle"]))))
                    throw new InvalidOperationException($"The GPU overclock confirmation did not follow {theme}.");
                var shown = dialog.ShowAsync();
                await Task.Delay(180);
                dialog.Hide();
                await shown;
            }
        }
        finally
        {
            lastMode.IsChecked = false;
            if (previouslyChecked is not null) previouslyChecked.IsChecked = true;
            App.Settings.Current.Appearance = appearance;
            ApplyAppearance();
        }

        var toggle = (ToggleSwitch)page.FindName("GpuOverclockToggle");
        var progress = (ProgressRing)page.FindName("GpuOverclockProgress");
        var completion = new TaskCompletionSource<GProControl.Models.HardwareActionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var request = page.RequestGpuOverclockAsync(true, () => Task.FromResult(true), _ => completion.Task);
        var heldDuringReadback = toggle.IsOn && !toggle.IsEnabled && progress.IsActive;
        completion.SetResult(new GProControl.Models.HardwareActionResult(true, false, "Synthetic readback.", 0));
        await request;
        if (!heldDuringReadback || toggle.IsOn || progress.IsActive)
            throw new InvalidOperationException("The GPU overclock switch did not hold the target until readback or restore the reported state.");

        await page.RequestGpuOverclockAsync(true, () => Task.FromResult(true),
            _ => Task.FromResult(new GProControl.Models.HardwareActionResult(true, false, "Synthetic timeout.")));
        await page.RefreshGpuOverclockAsync(() => Task.FromResult(
            new GProControl.Models.HardwareActionResult(false, true, "Synthetic stale state.", 0)));
        if (!toggle.IsOn || toggle.IsEnabled || App.PendingGpuOverclockTarget != true)
            throw new InvalidOperationException("An old GPU overclock state ended the pending enable request.");
        await page.RefreshGpuOverclockAsync(() => Task.FromResult(
            new GProControl.Models.HardwareActionResult(false, true, "Synthetic confirmed state.", 1)));
        if (!toggle.IsOn || App.PendingGpuOverclockTarget is not null)
            throw new InvalidOperationException("A confirmed GPU overclock target did not clear the pending request.");

        if (workspace.ActualWidth >= 728)
        {
            var left = gpu.TransformToVisual(workspace).TransformPoint(origin);
            var right = output.TransformToVisual(workspace).TransformPoint(origin);
            var secondRow = screen.TransformToVisual(workspace).TransformPoint(origin);
            if (Grid.GetRow(lastMode) != 0 || Math.Abs(left.Y - right.Y) > 1
                || right.X <= left.X || secondRow.Y <= left.Y)
                throw new InvalidOperationException("Performance cards did not form two rows in the wide window.");
        }

        var size = AppWindow.Size;
        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(580, 520));
            await Task.Delay(140);
            var top = gpu.TransformToVisual(workspace).TransformPoint(origin);
            var bottom = output.TransformToVisual(workspace).TransformPoint(origin);
            if (Grid.GetRow(lastMode) != 1 || Grid.GetColumnSpan(lastMode) != 2
                || Math.Abs(top.X - bottom.X) > 1 || bottom.Y + 1 < top.Y + gpu.ActualHeight)
                throw new InvalidOperationException("Performance cards overlapped or mode buttons failed to wrap in the narrow window.");
        }
        finally
        {
            AppWindow.Resize(size);
        }
        await Task.Delay(140);
    }

    private static Grid GetModeSelection(RadioButton option) =>
        ((Grid)VisualTreeHelper.GetChild(option, 0)).Children.OfType<Grid>().Single();

    private static async Task RequireDetailsLayoutAsync(DetailsPage page)
    {
        var sections = (Grid)page.FindName("SectionGrid");
        for (int i = 0; sections.Children.Count == 0 && i < 25; i++)
            await Task.Delay(200);
        if (sections.Children.Count == 0 || ((Button)page.FindName("CopyButton")).IsEnabled == false)
            throw new InvalidOperationException("Details did not display the hardware report.");
        var virtualAdapters = (Expander)page.FindName("VirtualAdapters");
        if (virtualAdapters.Visibility == Visibility.Visible)
        {
            var adapterGrid = (Grid)page.FindName("VirtualAdapterGrid");
            if (virtualAdapters.IsExpanded || adapterGrid.Children.Count == 0
                || !ReferenceEquals(virtualAdapters.Template, Application.Current.Resources["GamingExpanderTemplate"]))
                throw new InvalidOperationException("Virtual adapters did not load in a collapsed group.");
            virtualAdapters.IsExpanded = true;
            await Task.Delay(150);
            if (adapterGrid.ActualHeight <= 0)
                throw new InvalidOperationException("The expanded graphics adapter list did not become visible.");
            virtualAdapters.IsExpanded = false;
        }
    }

    private async Task VerifyDashboardStartupAsync()
    {
        try
        {
            await Task.Delay(350);
            var dashboard = (DashboardPage)ContentFrame.Content;
            var modeText = (TextBlock)dashboard.FindName("DashboardModeText");
            if (modeText.Text != "等待原厂服务读回")
                throw new InvalidOperationException("Dashboard read the mode before startup restoration completed.");

            var state = await App.HardwareControl.RefreshAsync();
            var result = await App.HardwareControl.GetPerformanceModeAsync();
            if (!state.CanArmWrites || !result.Verified || result.ReadbackValue is not int ecValue)
                throw new InvalidOperationException("Cannot confirm the current performance mode.");
            var mode = PerformanceModeCatalog.All.FirstOrDefault(entry => entry.EcValue == ecValue);
            string expected = $"{mode.Name ?? "未知模式"} · EC {ecValue}";

            App.CompleteStartupPerformanceRestore();
            for (int attempt = 0; attempt < 100 && modeText.Text != expected; attempt++)
                await Task.Delay(100);
            if (!ReferenceEquals(ContentFrame.Content, dashboard) || modeText.Text != expected)
                throw new InvalidOperationException($"Dashboard expected {expected}, got {modeText.Text}.");

            StartupDiagnostics.Write($"Dashboard startup smoke passed: waited for restore, displayed {expected} without navigation.");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Dashboard startup smoke failed.", exception);
        }
        finally
        {
            App.CompleteStartupPerformanceRestore();
            Close();
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
            var shortcutBackHidden = PageBackButton.Visibility == Visibility.Collapsed;
            NavigateTo("dashboard");
            NavigateTo("performance", asSubpage: true);
            var childBackVisible = PageBackButton.Visibility == Visibility.Visible;
            GoBack();
            await Task.Delay(300);
            var back = ContentFrame.Content is DashboardPage
                && (Navigation.SelectedItem as NavigationViewItem)?.Tag?.ToString() == "dashboard";
            var historyClean = !ContentFrame.CanGoBack;
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>()
                .First(item => item.Tag?.ToString() == "performance");
            var rootBackHidden = ContentFrame.Content is PerformancePage && !ContentFrame.CanGoBack
                && PageBackButton.Visibility == Visibility.Collapsed;
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

    private void OnWindowThemeChanged(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => ApplyAppearance());

    internal void ApplyAppearance(bool adjustWindowSize = false)
    {
        var settings = App.Settings.Current;
        ActiveAppearance = AppAppearance.Windows;
        if (settings.EffectiveAppearance != AppAppearance.Windows)
        {
            try
            {
                if (_themeSettings?.HighContrast == false)
                    ActiveAppearance = settings.EffectiveAppearance;
            }
            catch (Exception exception)
            {
                StartupDiagnostics.Write("Cannot read the window contrast theme.", exception);
            }
        }
        Shell.RequestedTheme = IsGamingAppearanceActive ? ElementTheme.Dark : settings.ThemeMode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var visibility = IsGamingAppearanceActive ? Visibility.Visible : Visibility.Collapsed;
        GamingShellBackground.Visibility = visibility;
        GamingTitleBackground.Visibility = visibility;
        GamingTitleIcon.Visibility = visibility;
        GamingSidebar.Visibility = visibility;
        RogWorkspaceBackdrop.Visibility = IsRogAppearanceActive ? Visibility.Visible : Visibility.Collapsed;
        TitleBarRow.Height = new GridLength(IsGamingAppearanceActive ? 34 : 48);
        Navigation.IsPaneVisible = !IsGamingAppearanceActive;
        UpdateBackNavigation();
        DefaultTitleIcon.Visibility = IsGamingAppearanceActive ? Visibility.Collapsed : Visibility.Visible;
        SystemBackdrop = IsGamingAppearanceActive ? null : SystemBackdrop ?? new MicaBackdrop();
        if (IsGamingAppearanceActive)
        {
            ApplyGamingPalette(ActiveAppearance == AppAppearance.Legion);
            ApplySidebarWidth();
        }
        var cardStroke = (SolidColorBrush)Application.Current.Resources["CardStrokeBrush"];
        cardStroke.Color = IsRogAppearanceActive
            ? Color.FromArgb(255, 60, 58, 62)
            : Color.FromArgb(31, 127, 132, 144);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        if (adjustWindowSize) ResizeForAppearance();
    }

    private double WindowScale => Math.Max(96u, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96d;

    private void ResizeForAppearance()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;

        // Size the dashboard and sidebar separately so an expanded sidebar leaves the same room for the dashboard.
        var (windowWidth, windowHeight, stackedBelow) = ActiveAppearance switch
        {
            AppAppearance.Rog => (1060 + GamingSidebar.Width, 684, DashboardPage.RogStackedBelow),
            AppAppearance.Legion => (1090 + GamingSidebar.Width, 624, DashboardPage.LegionStackedBelow),
            _ => (1180d, 760, 0d)
        };
        var scale = WindowScale;
        var width = (int)Math.Round(windowWidth * scale);
        var height = (int)Math.Round(windowHeight * scale);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            return;
        }

        var work = display.WorkArea;
        width = Math.Min(width, work.Width);
        var stacks = IsGamingAppearanceActive && width / scale - GamingSidebar.Width < stackedBelow;
        height = stacks ? work.Height : Math.Min(height, work.Height);
        var position = AppWindow.Position;
        var x = Math.Clamp(position.X, work.X, work.X + work.Width - width);
        var y = Math.Clamp(position.Y, work.Y, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    private static async Task RequireStableTelemetryBarAsync(DashboardPage page)
    {
        var monitor = (StackPanel)page.FindName("RogMonitor");
        var bar = new TelemetryProgressBar
        {
            Style = (Style)page.Resources["TelemetryProgressBarStyle"],
            Width = 100,
            Height = 4,
            Value = 82,
            Opacity = 0,
            IsHitTestVisible = false
        };
        monitor.Children.Add(bar);
        try
        {
            await Task.Delay(40);
            var layout = (Grid)VisualTreeHelper.GetChild(bar, 0);
            var indicator = (Microsoft.UI.Xaml.Shapes.Rectangle)layout.Children[1];
            foreach (var value in new[] { 13d, 0d, 56d })
            {
                var previous = indicator.ActualWidth;
                bar.Value = value;
                await Task.Delay(55);
                var intermediate = indicator.ActualWidth;
                var origin = indicator.TransformToVisual(layout).TransformPoint(new Windows.Foundation.Point());
                if (Math.Abs(origin.X) > 0.5 || (AnimationsEnabled()
                    && (intermediate <= Math.Min(previous, value) || intermediate >= Math.Max(previous, value))))
                    throw new InvalidOperationException($"Telemetry fill did not animate from the left edge at {value}% (width {intermediate}).");
                await Task.Delay(160);
                if (Math.Abs(indicator.ActualWidth - value) > 1)
                    throw new InvalidOperationException($"Telemetry fill did not settle at {value}%.");
            }
        }
        finally
        {
            monitor.Children.Remove(bar);
        }
    }

    private void RequireFittedDashboard(DashboardPage page, int contentWidth, int windowHeight)
    {
        var scale = WindowScale;
        var width = (int)Math.Round((contentWidth + GamingSidebar.Width) * scale);
        var height = (int)Math.Round(windowHeight * scale);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is not null && (width > display.WorkArea.Width || height > display.WorkArea.Height)) return;

        var scroll = (ScrollViewer)page.FindName("DashboardScroller");
        bool overflowing = ActiveAppearance == AppAppearance.Legion
            ? scroll.ScrollableHeight > 0 || scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
            : scroll.ScrollableHeight > 2;
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height || overflowing)
            throw new InvalidOperationException($"{ActiveAppearance} overview did not fit its startup window: "
                + $"{AppWindow.Size.Width}×{AppWindow.Size.Height}, expected {width}×{height}, scroll {scroll.ScrollableHeight:F1}.");
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ApplyGamingPalette(bool legion)
    {
        GamingShellBackground.Background = new SolidColorBrush(legion ? Color.FromArgb(255, 13, 19, 28) : Color.FromArgb(255, 17, 17, 17));
        GamingSidebar.Background = GamingTitleBackground.Background =
            new SolidColorBrush(legion ? Color.FromArgb(255, 21, 31, 43) : Color.FromArgb(255, 24, 24, 24));
        GamingTitleIcon.Background = new SolidColorBrush(legion ? Color.FromArgb(255, 0, 119, 158) : Color.FromArgb(255, 206, 33, 67));
        ((SolidColorBrush)GamingNavigation.Resources["ListViewItemBackgroundSelected"]).Color =
            legion ? Color.FromArgb(255, 24, 60, 78) : Color.FromArgb(255, 62, 32, 38);
        ((SolidColorBrush)GamingNavigation.Resources["ListViewItemBackgroundSelectedPointerOver"]).Color =
            legion ? Color.FromArgb(255, 32, 76, 96) : Color.FromArgb(255, 80, 39, 47);
        ((SolidColorBrush)GamingNavigation.Resources["ListViewItemSelectionIndicatorBrush"]).Color =
            legion ? Color.FromArgb(255, 53, 217, 247) : Color.FromArgb(255, 255, 50, 79);
        var resources = Application.Current.Resources;
        ((SolidColorBrush)resources["GamingToggleFillBrush"]).Color =
            legion ? Color.FromArgb(255, 0, 103, 145) : Color.FromArgb(255, 199, 25, 63);
        ((SolidColorBrush)resources["GamingToggleHoverBrush"]).Color =
            legion ? Color.FromArgb(255, 0, 129, 174) : Color.FromArgb(255, 236, 49, 83);
        ((SolidColorBrush)resources["GamingTogglePressedBrush"]).Color =
            legion ? Color.FromArgb(255, 0, 77, 108) : Color.FromArgb(255, 165, 19, 51);
        ((SolidColorBrush)resources["GamingToggleStrokeBrush"]).Color =
            legion ? Color.FromArgb(255, 91, 209, 246) : Color.FromArgb(255, 250, 87, 115);
    }

    private void GamingSidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Update(settings => settings.GamingSidebarCollapsed = !settings.GamingSidebarCollapsed);
        ApplySidebarWidth(animate: true);
        AnimateNavigationIcon((FrameworkElement)GamingSidebarToggle.Content);
    }

    private void GamingSidebar_SizeChanged(object sender, SizeChangedEventArgs args) =>
        GamingSidebarClip.Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height);

    private void ApplySidebarWidth(bool animate = false)
    {
        bool collapsed = App.Settings.Current.GamingSidebarCollapsed;
        var previousWidth = GamingSidebar.ActualWidth;
        var targetWidth = collapsed ? 56d : 176d;
        _sidebarTransition?.Stop();
        GamingSidebar.Width = targetWidth;
        var action = collapsed ? "展开侧边栏" : "收起侧边栏";
        AutomationProperties.SetName(GamingSidebarToggle, action);
        ToolTipService.SetToolTip(GamingSidebarToggle, action);
        foreach (var item in GamingNavigation.Items.OfType<ListViewItem>())
        {
            var label = ((StackPanel)item.Content).Children.OfType<TextBlock>().Single();
            label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(item, collapsed ? label.Text : null);
        }

        if (!animate || previousWidth <= 0 || previousWidth == targetWidth || !AnimationsEnabled()) return;

        var widthAnimation = new DoubleAnimation
        {
            From = previousWidth,
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(180),
            EnableDependentAnimation = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(widthAnimation, GamingSidebar);
        Storyboard.SetTargetProperty(widthAnimation, nameof(FrameworkElement.Width));
        _sidebarTransition = new Storyboard();
        _sidebarTransition.Children.Add(widthAnimation);
        _sidebarTransition.Begin();
    }

    internal static bool AnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch (COMException) { return false; }
    }

    private static void AnimateNavigationIcon(FrameworkElement icon)
    {
        if (!AnimationsEnabled() || icon.ActualWidth <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(icon);
        visual.CenterPoint = new Vector3((float)icon.ActualWidth / 2, (float)icon.ActualHeight / 2, 0);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(320);
        animation.InsertKeyFrame(0f, new Vector3(0.79f, 0.79f, 1));
        animation.InsertKeyFrame(0.47f, new Vector3(1.13f, 1.13f, 1));
        animation.InsertKeyFrame(0.73f, new Vector3(0.97f, 0.97f, 1));
        animation.InsertKeyFrame(1f, Vector3.One);
        visual.StartAnimation(nameof(visual.Scale), animation);
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
        if (args.InvokedItemContainer is not NavigationViewItem { Tag: string tag } item) return;
        if (item.Icon is { } icon) AnimateNavigationIcon(icon);
        NavigateTo(tag);
    }

    private void GamingNavigation_ItemClick(object sender, ItemClickEventArgs args)
    {
        var item = args.ClickedItem as ListViewItem ?? GamingNavigation.Items.OfType<ListViewItem>()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Content, args.ClickedItem));
        if (item is not null) ActivateGamingNavigationItem(item);
    }

    private void GamingNavigation_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingNavigation && GamingNavigation.SelectedItem is ListViewItem item)
            ActivateGamingNavigationItem(item);
    }

    private void ActivateGamingNavigationItem(ListViewItem item)
    {
        if (item.Content is StackPanel panel && panel.Children.FirstOrDefault() is Grid icon
            && _queuedNavigationIcons.Add(icon))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _queuedNavigationIcons.Remove(icon);
                AnimateNavigationIcon(icon);
            });
        }

        if (item.Tag is string tag) NavigateTo(tag);
    }

    private void PageBackButton_Click(object sender, RoutedEventArgs e) => GoBack();

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
        try
        {
            Navigation.SelectedItem = item;
            GamingNavigation.SelectedItem = GamingNavigation.Items.OfType<ListViewItem>()
                .FirstOrDefault(candidate => candidate.Tag?.ToString() == tag);
        }
        finally { _syncingNavigation = false; }
        UpdateBackNavigation();
    }

    private void UpdateBackNavigation()
    {
        Navigation.IsBackEnabled = ContentFrame.CanGoBack;
        PageBackButton.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }
}
