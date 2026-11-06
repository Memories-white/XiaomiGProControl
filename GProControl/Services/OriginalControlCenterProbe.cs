using System.Runtime.InteropServices;
using GProControl.Models;

namespace GProControl.Services;

public sealed class OriginalControlCenterProbe
{
    private const string InstallRoot = @"C:\Program Files\MI\Xiaomi G Command Center";
    private const string MiServicePipe = @"\\.\pipe\E09DF953-5B84-4CB7-A23B-969218E24781";
    private const string MiDeviceServicePipe = @"\\.\pipe\349B49E2-996B-4B2B-8379-1075B23F1272";
    private const int ErrorSemTimeout = 121;

    public OriginalBridgeStatus Capture()
    {
        var installDirectory = FindNewestInstallDirectory();
        return new OriginalBridgeStatus(
            installDirectory is not null,
            installDirectory is null ? null : Path.GetFileName(installDirectory),
            installDirectory,
            IsPipePresent(MiServicePipe),
            IsPipePresent(MiDeviceServicePipe));
    }

    private static string? FindNewestInstallDirectory()
    {
        try
        {
            return Directory.EnumerateDirectories(InstallRoot)
                .Where(path => File.Exists(Path.Combine(path, "libXiaomiGCommandCenter.dll")))
                .OrderByDescending(path => ParseVersion(Path.GetFileName(path)))
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static bool IsPipePresent(string path)
    {
        if (WaitNamedPipe(path, 0))
        {
            return true;
        }

        return Marshal.GetLastWin32Error() == ErrorSemTimeout;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeout);
}
