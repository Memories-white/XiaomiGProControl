using Microsoft.UI.Xaml;
using GProControl.Models;
using GProControl.Protocol;
using GProControl.Services;

namespace GProControl;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static IHardwareControlService HardwareControl { get; } = new XiaomiHardwareControlService();
    public static AppSettingsStore Settings { get; } = new();
    internal static AmbientLightingService AmbientLighting { get; } = new(() => HardwareControl.State.CanWrite);
    public static string StartupRestoreMessage { get; private set; } = "尚未执行启动恢复。";
    public static string LightingRestoreMessage { get; private set; } = "尚未恢复灯效。";
    public static bool GraphicModeRestartPending { get; set; }
    public static bool IsPackageSmoke { get; } = Environment.GetCommandLineArgs().Contains("--package-smoke");
    public static string StartupRegistrationMessage { get; private set; } = "登录 Windows 后以管理员权限自动启动。";

    public App()
    {
        StartupDiagnostics.Write("App constructor entered.");
        UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Write("Unhandled WinUI exception.", eventArgs.Exception);

        try
        {
            InitializeComponent();
            StartupDiagnostics.Write("App resources initialized.");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("App resource initialization failed.", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Write("OnLaunched entered.");
        try
        {
            MainWindow = new MainWindow();
            StartupDiagnostics.Write("Main window constructed.");
            MainWindow.Activate();
            StartupDiagnostics.Write("Main window activated.");
            MainWindow.ApplyTheme(Settings.Current.ThemeMode);
            if (!IsPackageSmoke)
            {
                StartupRegistrationService.SetEnabled(Settings.Current.StartWithWindows, out var startupMessage);
                StartupRegistrationMessage = startupMessage;
                _ = RestoreAllStartupStateAsync();
                if (Environment.GetCommandLineArgs().Contains("--startup") && Settings.Current.StartInBackground)
                    MainWindow.MoveToBackground();
            }
            else
            {
                StartupDiagnostics.Write("Package smoke mode: startup registration and hardware restoration skipped.");
                _ = MainWindow.VerifyBackgroundLifecycleAsync();
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Write("Window launch failed.", ex);
            throw;
        }
    }

    private static async Task RestoreAllStartupStateAsync()
    {
        await RestoreStartupStateAsync();
        if (!Settings.Current.RestoreLighting)
        {
            LightingRestoreMessage = "已关闭启动时恢复灯效。";
            return;
        }
        var profile = Settings.Current.LastLightingProfile;
        if (profile is null && Settings.Current.LastWaveLighting is { IsValidated: true } wave)
            profile = new LightingProfile(1, wave.PaletteCode, wave.DirectionCode, wave.BrightnessCode, wave.SpeedCode);
        if (profile is not { IsSupported: true } || Settings.Current.LastLightingEnabled is not bool enabled)
        {
            LightingRestoreMessage = "尚无保存的灯效；在灯效页应用一次即可记住。";
            return;
        }
        if (!HardwareControl.State.CanWrite)
        {
            LightingRestoreMessage = "硬件安全检查未通过，已跳过灯效恢复。";
            return;
        }
        try
        {
            await new B21HidClient().ApplyEffectAsync(profile, enabled, TimeSpan.FromSeconds(2));
            LightingRestoreMessage = enabled ? "已恢复上次键盘灯效。" : "已恢复键盘灯关闭状态。";
        }
        catch (Exception exception)
        {
            LightingRestoreMessage = $"灯效恢复未确认：{exception.Message}";
            StartupDiagnostics.Write("Lighting restore failed.", exception);
        }
    }

    private static async Task RestoreStartupStateAsync()
    {
        try
        {
            HardwareControlState? state = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                state = await HardwareControl.RefreshAsync();
                if (state.CanArmWrites)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            if (state?.CanArmWrites != true)
            {
                StartupRestoreMessage = "原厂服务未就绪，未恢复上次性能模式。";
                return;
            }

            HardwareControl.SetWriteArmed(true);
            var selfTest = await HardwareControl.EnsureWmiTransportAsync();
            StartupDiagnostics.Write($"Automatic device self-test: verified={selfTest.Verified}; {selfTest.Message}");
            var desired = Settings.Current.LastPerformanceServiceValue;
            if (desired is null)
            {
                var current = await HardwareControl.GetPerformanceModeAsync();
                if (current.Verified && current.ReadbackValue is int ecValue)
                {
                    var index = PerformanceModeCatalog.IndexOfEc(ecValue);
                    if (index >= 0)
                    {
                        desired = PerformanceModeCatalog.All[index].ServiceValue;
                        Settings.Update(value => value.LastPerformanceServiceValue = desired);
                        StartupRestoreMessage = $"已记住当前性能模式：{PerformanceModeCatalog.All[index].Name}。";
                    }
                }

                return;
            }

            if (!Settings.Current.RestorePerformanceMode)
            {
                StartupRestoreMessage = "已关闭启动时恢复性能模式。";
                return;
            }

            var target = PerformanceModeCatalog.FindByServiceValue(desired.Value);
            if (target is null)
            {
                StartupRestoreMessage = "保存的性能模式值无效，已跳过恢复。";
                return;
            }

            var result = await HardwareControl.SetPerformanceModeAsync(desired.Value);
            StartupRestoreMessage = result.Verified
                ? $"已恢复上次性能模式：{target.Value.Name}。"
                : $"恢复上次性能模式未确认：{result.Message}";
        }
        catch (Exception ex)
        {
            StartupRestoreMessage = $"启动恢复失败：{ex.Message}";
            StartupDiagnostics.Write("Startup state restore failed.", ex);
        }
    }
}
