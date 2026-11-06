using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using GProControl.Models;

namespace GProControl.Services;

public sealed class WindowsSystemMonitorService : ISystemMonitorService
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousReceived;
    private long _previousSent;
    private DateTimeOffset _previousNetworkAt = DateTimeOffset.UtcNow;

    public SystemSnapshot Capture()
    {
        var now = DateTimeOffset.UtcNow;
        var cpu = CaptureCpu();

        var memory = new MemoryStatusEx();
        memory.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        GlobalMemoryStatusEx(ref memory);

        var systemDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
            ?? DriveInfo.GetDrives().First(d => d.IsReady);

        var (received, sent) = GetNetworkTotals();
        var seconds = Math.Max(0.1, (now - _previousNetworkAt).TotalSeconds);
        var download = _previousReceived == 0 ? 0 : Math.Max(0, received - _previousReceived) * 8d / 1000d / seconds;
        var upload = _previousSent == 0 ? 0 : Math.Max(0, sent - _previousSent) * 8d / 1000d / seconds;
        _previousReceived = received;
        _previousSent = sent;
        _previousNetworkAt = now;

        return new SystemSnapshot(
            cpu,
            memory.TotalPhysical - memory.AvailablePhysical,
            memory.TotalPhysical,
            systemDrive.TotalSize - systemDrive.AvailableFreeSpace,
            systemDrive.TotalSize,
            download,
            upload,
            now,
            NvidiaGpuTelemetry.TryCapture());
    }

    private double CaptureCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        var idleDelta = idleValue - _previousIdle;
        var totalDelta = kernelValue - _previousKernel + userValue - _previousUser;
        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        return totalDelta == 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static (long Received, long Sent) GetNetworkTotals()
    {
        long received = 0;
        long sent = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var stats = adapter.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }
        return (received, sent);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
