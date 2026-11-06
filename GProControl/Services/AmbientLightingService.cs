using System.Runtime.InteropServices;
using GProControl.Models;

namespace GProControl.Services;

internal sealed class AmbientLightingService(Func<bool> canWrite)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    public int? ActiveEffect { get; private set; }
    public string? LastError { get; private set; }
    public event Action? Changed;

    public async Task StartAsync(int effect, byte brightness, string color)
    {
        if (effect is not 21 and not 22) throw new ArgumentOutOfRangeException(nameof(effect));
        if (!canWrite()) throw new InvalidOperationException("请先启用硬件控制。");
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            var client = new B21HidClient();
            var original = await client.ReadCommandAsync(1, TimeSpan.FromSeconds(2));
            _cancellation = new CancellationTokenSource();
            LastError = null;
            ActiveEffect = effect;
            var cancellation = _cancellation.Token;
            _worker = Task.Run(async () =>
            {
                CoreAudioPeakReader? audio = null;
                try
                {
                    if (effect == 21) audio = new CoreAudioPeakReader();
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (!canWrite()) break;
                        var rgb = effect == 22 ? SampleScreen() : AudioColor(audio!.ReadPeak(), color);
                        // Runtime animation is never committed to keyboard flash.
                        await client.ApplyEffectAsync(new LightingProfile(11, 0xFF, 0, brightness, 0, 0, rgb),
                            true, TimeSpan.FromSeconds(2), cancellation, persist:false);
                        await Task.Delay(125, cancellation);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { LastError = exception.Message; }
                finally
                {
                    audio?.Dispose();
                    try { await client.RestoreCapturedAsync(original, TimeSpan.FromSeconds(2)); }
                    catch (Exception exception) { LastError = "灯效恢复未确认：" + exception.Message; }
                    ActiveEffect = null;
                    Changed?.Invoke();
                }
            });
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _cancellation?.Cancel();
        if (_worker is not null) await _worker;
        _worker = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    internal static string SampleScreen()
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new IOException("无法读取屏幕颜色。");
        try
        {
            long r=0, g=0, b=0; var count=0;
            var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
            for (var y=1; y<=4; y++) for (var x=1; x<=4; x++)
            {
                var pixel = GetPixel(dc, x*width/5, y*height/5);
                if (pixel == uint.MaxValue) continue;
                r += pixel & 255; g += (pixel>>8)&255; b += (pixel>>16)&255; count++;
            }
            if (count == 0) throw new IOException("当前桌面无法读取，已停止光影随行。");
            return $"{r/count/2:X2}{g/count/2:X2}{b/count/2:X2}";
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    private static string AudioColor(float peak, string color)
    {
        var rgb = Convert.FromHexString(color);
        var level = Math.Clamp(MathF.Sqrt(peak), 0, 1);
        return string.Concat(rgb.Select(value => ((byte)(value*level)).ToString("X2")));
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}

internal sealed class CoreAudioPeakReader : IDisposable
{
    private readonly IMeter _meter;
    public CoreAudioPeakReader()
    {
        var enumerator = (IDeviceEnumerator)new DeviceEnumerator();
        IDevice? device = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
            var id = typeof(IMeter).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref id, 23, IntPtr.Zero, out var meter));
            _meter = (IMeter)meter;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }
    public float ReadPeak() { Marshal.ThrowExceptionForHR(_meter.GetPeakValue(out var peak)); return peak; }
    public void Dispose() => Marshal.ReleaseComObject(_meter);
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class DeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeter
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
