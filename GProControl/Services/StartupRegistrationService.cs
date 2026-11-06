using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GProControl.Services;

public static class StartupRegistrationService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "GProControl";
    private const string TaskName = "GProControl Startup";
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExecute = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskInstancesIgnoreNew = 2;

    public static bool IsEnabled()
    {
        try
        {
            dynamic root = ConnectRootFolder();
            _ = root.GetTask(TaskName);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            dynamic root = ConnectRootFolder();
            if (enabled)
            {
                var executable = Environment.GetEnvironmentVariable("GPROCONTROL_LAUNCHER_PATH") ?? Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法确定当前程序路径。");
                var userName = WindowsIdentity.GetCurrent().Name;
                dynamic service = CreateScheduleService();
                service.Connect();
                dynamic definition = service.NewTask(0);

                definition.RegistrationInfo.Description = "以管理员权限启动 G Pro Control 并恢复上次硬件设置。";
                definition.Principal.UserId = userName;
                definition.Principal.LogonType = TaskLogonInteractiveToken;
                definition.Principal.RunLevel = TaskRunLevelHighest;
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.ExecutionTimeLimit = "PT0S";
                definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;

                dynamic trigger = definition.Triggers.Create(TaskTriggerLogon);
                trigger.Enabled = true;
                trigger.UserId = userName;

                dynamic action = definition.Actions.Create(TaskActionExecute);
                action.Path = executable;
                action.Arguments = "--startup";
                action.WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;

                root.RegisterTaskDefinition(
                    TaskName,
                    definition,
                    TaskCreateOrUpdate,
                    null,
                    null,
                    TaskLogonInteractiveToken,
                    null);

                // Only remove the old UAC-blocked entry after registration succeeds.
                key.DeleteValue(LegacyValueName, false);
                message = "已设置登录 Windows 后以管理员权限自动启动（允许电池供电）。";
            }
            else
            {
                try
                {
                    root.DeleteTask(TaskName, 0);
                }
                catch (COMException)
                {
                    // Already absent is the requested final state.
                }

                key.DeleteValue(LegacyValueName, false);
                message = "已关闭开机启动。";
            }

            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            message = $"更新开机启动失败：{ex.Message}";
            return false;
        }
    }

    private static dynamic ConnectRootFolder()
    {
        dynamic service = CreateScheduleService();
        service.Connect();
        return service.GetFolder("\\");
    }

    private static dynamic CreateScheduleService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("当前系统没有任务计划程序 COM 服务。");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("无法创建任务计划程序 COM 服务。");
    }
}
