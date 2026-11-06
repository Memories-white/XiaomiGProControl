using System.Runtime.InteropServices;
using System.Text;
using GProControl.Models;

namespace GProControl.Services;

/// <summary>
/// Reader for NVIDIA telemetry through NVML. <c>nvml.dll</c> is installed by the
/// display driver itself, so this needs no bundled dependency, no elevation and
/// no per-sample process launch (unlike shelling out to <c>nvidia-smi</c>).
/// Every failure path degrades to <see langword="null"/>; nothing here throws to
/// the caller.
/// </summary>
internal static class NvidiaGpuTelemetry
{
    private const int NvmlSuccess = 0;
    private const int SensorGpu = 0;
    private const int NameBufferLength = 96;

    private static readonly object Gate = new();
    private static bool _resolved;
    private static bool _unavailable;
    private static IntPtr _device = IntPtr.Zero;

    public static GpuSnapshot? TryCapture()
    {
        if (!EnsureDevice())
        {
            return null;
        }

        try
        {
            var name = ReadName();
            var utilization = TryGetUtilization();
            var temperature = TryGetTemperature();
            var power = TryGetPowerWatts();
            var (usedMemory, totalMemory) = TryGetMemory();
            if (name is null && utilization is null && temperature is null && power is null && usedMemory is null)
            {
                return null;
            }

            return new GpuSnapshot(name, utilization, temperature, power, usedMemory, totalMemory);
        }
        catch (Exception ex) when (IsLoadingFailure(ex))
        {
            MarkUnavailable();
            return null;
        }
    }

    private static bool EnsureDevice()
    {
        lock (Gate)
        {
            if (_resolved)
            {
                return !_unavailable && _device != IntPtr.Zero;
            }

            _resolved = true;
            try
            {
                if (NvmlInit() != NvmlSuccess)
                {
                    _unavailable = true;
                    return false;
                }

                if (NvmlDeviceGetCount(out var count) != NvmlSuccess || count == 0)
                {
                    _unavailable = true;
                    return false;
                }

                // Index 0 is the discrete adapter that NVML reports for this
                // class of laptop; there is normally exactly one.
                if (NvmlDeviceGetHandleByIndex(0, out var device) != NvmlSuccess || device == IntPtr.Zero)
                {
                    _unavailable = true;
                    return false;
                }

                _device = device;
                return true;
            }
            catch (Exception ex) when (IsLoadingFailure(ex))
            {
                _unavailable = true;
                return false;
            }
        }
    }

    private static void MarkUnavailable()
    {
        lock (Gate)
        {
            _unavailable = true;
            _device = IntPtr.Zero;
        }
    }

    private static string? ReadName()
    {
        var buffer = new byte[NameBufferLength];
        if (NvmlDeviceGetName(_device, buffer, (uint)buffer.Length) != NvmlSuccess)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, (byte)0);
        var text = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length).Trim();
        return text.Length == 0 ? null : text;
    }

    private static double? TryGetUtilization() =>
        NvmlDeviceGetUtilizationRates(_device, out var utilization) == NvmlSuccess
            ? utilization.Gpu
            : null;

    private static int? TryGetTemperature() =>
        NvmlDeviceGetTemperature(_device, SensorGpu, out var temperature) == NvmlSuccess
            ? (int)temperature
            : null;

    private static double? TryGetPowerWatts() =>
        NvmlDeviceGetPowerUsage(_device, out var milliwatts) == NvmlSuccess
            ? milliwatts / 1000d
            : null;

    private static (ulong? Used, ulong? Total) TryGetMemory()
    {
        if (NvmlDeviceGetMemoryInfo(_device, out var memory) != NvmlSuccess || memory.Total == 0)
        {
            return (null, null);
        }

        return (memory.Used, memory.Total);
    }

    private static bool IsLoadingFailure(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int NvmlDeviceGetCount(out uint deviceCount);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    private static extern int NvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }
}
