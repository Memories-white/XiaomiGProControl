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
    public AmbientLightingOptions Options { get; private set; } = new();
    public byte Brightness { get; private set; } = 2;
    public string SelectedColor { get; private set; } = "7F0000";
    public float[] SpectrumBands { get; private set; } = new float[17];
    public event Action? Changed;

    public async Task StartAsync(int effect, byte brightness, string color, AmbientLightingOptions? options = null)
    {
        if (effect is not 21 and not 22) throw new ArgumentOutOfRangeException(nameof(effect));
        options ??= new();
        if (!options.IsValid || brightness>3 || color.Length!=6 || !color.All(Uri.IsHexDigit)
            || Convert.FromHexString(color).Any(value=>value>127)) throw new ArgumentOutOfRangeException(nameof(options));
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
            Options = options;
            Brightness=brightness; SelectedColor=color;
            var cancellation = _cancellation.Token;
            _worker = Task.Run(async () =>
            {
                AudioSpectrumReader? audio = null;
                try
                {
                    if (effect == 21) audio = new AudioSpectrumReader(options.AudioSource == 1);
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (!canWrite()) break;
                        string rgb;
                        if(effect==22) rgb=SampleScreen(options.ScreenSamples);
                        else
                        {
                            var bands=audio!.ReadBands(); SpectrumBands=bands;
                            rgb=options.AudioStyle==1 ? SpectrumColor(bands,options.AudioPalette)
                                : AudioColor(bands.Max(),color);
                        }
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
                    SpectrumBands = new float[17];
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

    internal static string SampleScreen(int samples = 4)
    {
        if(samples is <1 or >5) throw new ArgumentOutOfRangeException(nameof(samples));
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new IOException("无法读取屏幕颜色。");
        try
        {
            long r=0, g=0, b=0; var count=0;
            var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
            for (var y=1; y<=samples; y++) for (var x=1; x<=samples; x++)
            {
                var pixel = GetPixel(dc, x*width/(samples+1), y*height/(samples+1));
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
    internal static string SpectrumColor(float[] bands,int palette)
    {
        // The physical output is uniform RGB; the 17-band preview remains separate.
        var stops = LightingPresetCatalog.AudioPalettes[palette].Stops!;
        double red=0,green=0,blue=0,total=0;
        for(var i=0;i<bands.Length;i++)
        {
            var position=(double)i/Math.Max(1,bands.Length-1);
            var upper=stops.FirstOrDefault(stop=>stop.Offset>=position) ?? stops[^1];
            var lower=stops.LastOrDefault(stop=>stop.Offset<=position) ?? stops[0];
            var fraction=upper.Offset==lower.Offset ? 0 : (position-lower.Offset)/(upper.Offset-lower.Offset);
            var from=Convert.FromHexString(lower.Color[3..]); var to=Convert.FromHexString(upper.Color[3..]);
            var weight=Math.Clamp(bands[i],0,1);
            red+=(from[0]+(to[0]-from[0])*fraction)*weight;
            green+=(from[1]+(to[1]-from[1])*fraction)*weight;
            blue+=(from[2]+(to[2]-from[2])*fraction)*weight;
            total+=weight;
        }
        if(total==0) return "000000";
        var scale=bands.Max()/total/2;
        return $"{(byte)Math.Clamp(red*scale,0,127):X2}{(byte)Math.Clamp(green*scale,0,127):X2}{(byte)Math.Clamp(blue*scale,0,127):X2}";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
