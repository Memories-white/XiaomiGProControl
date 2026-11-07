using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GProControl.Services;

/// <summary>WASAPI capture and FFT only; never saves or uploads captured audio.</summary>
internal sealed class AudioSpectrumReader : IDisposable
{
    private const int WindowSize = 2048;
    private readonly object _samplesGate = new(), _readGate = new();
    private readonly float[] _samples = new float[WindowSize];
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Exception? _failure;
    private int _position;
    private long _lastSample, _lastRead;
    private float[] _smoothed = [];
    private bool _disposed;
    public int SampleRate { get; private set; }
    public long SamplesCaptured { get; private set; }

    public AudioSpectrumReader(bool microphone = false)
    {
        _thread = new Thread(() => Capture(microphone)) { IsBackground = true, Name = "GProControl audio spectrum" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        try { _ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    public float[] ReadBands(int count = 17)
    {
        if (count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(count));
        lock (_readGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _failure) is { } failure)
                throw new IOException("音频设备采样已中断，请重新启动光音视界。", failure);
            var ordered = new float[WindowSize];
            var now = Stopwatch.GetTimestamp();
            lock (_samplesGate)
            {
                // Some render endpoints stop delivering loopback packets during silence.
                if (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds < .15)
                    for (var i = 0; i < WindowSize; i++) ordered[i] = _samples[(_position + i) % WindowSize];
            }
            var bands = AudioSpectrumDsp.ComputeBands(ordered, SampleRate, count);
            if (_smoothed.Length != count) _smoothed = new float[count];
            var elapsed = _lastRead == 0 ? .04 : Math.Clamp(Stopwatch.GetElapsedTime(_lastRead, now).TotalSeconds, 0, 1);
            _lastRead = now;
            for (var i = 0; i < count; i++)
            {
                var tau = bands[i] > _smoothed[i] ? .025 : .18;
                _smoothed[i] += (bands[i] - _smoothed[i]) * (float)(1 - Math.Exp(-elapsed / tau));
                if (_smoothed[i] < .001f) _smoothed[i] = 0;
            }
            return (float[])_smoothed.Clone();
        }
    }

    public void Dispose()
    {
        lock (_readGate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
        }
        // All WASAPI interfaces are stopped and released by their creating thread.
        if (Thread.CurrentThread != _thread) _thread.Join();
        _stop.Dispose();
    }

    private void Capture(bool microphone)
    {
        IAudioClient? audio = null;
        IAudioCaptureClient? capture = null;
        IDeviceEnumerator? enumerator = null;
        IDevice? device = null;
        var initialized = false;
        var started = false;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            initialized = true;
            enumerator = (IDeviceEnumerator)new DeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(microphone ? 1 : 0, 1, out device));
            var audioId = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref audioId, 23, IntPtr.Zero, out var service));
            audio = (IAudioClient)service;
            Marshal.ThrowExceptionForHR(audio.GetMixFormat(out var formatPointer));
            AudioSampleFormat format;
            try
            {
                var size = checked(18 + (ushort)Marshal.ReadInt16(formatPointer, 16));
                if (size > 4096) throw new InvalidDataException("音频混合格式长度无效。");
                var bytes = new byte[size];
                Marshal.Copy(formatPointer, bytes, 0, size);
                format = AudioSampleFormat.Parse(bytes);
                // Shared-mode loopback is valid only on a render endpoint.
                Marshal.ThrowExceptionForHR(audio.Initialize(0, microphone ? 0u : 0x20000u,
                    2_000_000, 0, formatPointer, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(formatPointer); }
            SampleRate = format.SampleRate;
            var captureId = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(audio.GetService(ref captureId, out service));
            capture = (IAudioCaptureClient)service;
            Marshal.ThrowExceptionForHR(audio.Start());
            started = true;
            _ready.TrySetResult();
            var packet = Array.Empty<byte>();
            while (!_stop.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var next));
                while (next != 0 && !_stop.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags,
                        out _, out _));
                    try
                    {
                        if (frames != 0)
                        {
                            var byteCount = checked((int)frames * format.BlockAlign);
                            if (byteCount > 16 * 1024 * 1024) throw new InvalidDataException("音频采样包过大。");
                            var silent = (flags & 2) != 0;
                            if (!silent)
                            {
                                if (data == IntPtr.Zero) throw new InvalidDataException("音频采样指针为空。");
                                if (packet.Length < byteCount) packet = new byte[byteCount];
                                Marshal.Copy(data, packet, 0, byteCount);
                            }
                            lock (_samplesGate)
                            {
                                // A discontinuity must not blend unrelated audio windows.
                                if ((flags & 1) != 0) Array.Clear(_samples);
                                for (var frame = 0; frame < frames; frame++)
                                {
                                    _samples[_position] = silent ? 0 : format.ReadMono(packet.AsSpan(frame * format.BlockAlign, format.BlockAlign));
                                    _position = (_position + 1) % WindowSize;
                                }
                                SamplesCaptured += frames;
                                _lastSample = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                    finally { if (frames != 0) Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames)); }
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out next));
                }
                _stop.Token.WaitHandle.WaitOne(10);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failure, exception);
            _ready.TrySetException(new IOException("无法初始化音频频谱采样：" + exception.Message, exception));
        }
        finally
        {
            if (started) audio!.Stop();
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (audio is not null) Marshal.ReleaseComObject(audio);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (initialized) CoUninitialize();
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
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
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong counterPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

internal readonly record struct AudioSampleFormat(int SampleRate, int Channels, int BlockAlign, int Bits, bool Float)
{
    public static AudioSampleFormat Parse(ReadOnlySpan<byte> format)
    {
        if (format.Length < 18) throw new InvalidDataException("音频格式头不完整。");
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
        var align = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(format[16..]);
        if (tag == 0xFFFE)
        {
            if (extra < 22 || format.Length < 40) throw new InvalidDataException("扩展音频格式不完整。");
            var subFormat = new Guid(format.Slice(24,16));
            if (subFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")) tag = 1;
            else if (subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")) tag = 3;
            else throw new NotSupportedException("当前音频设备的扩展采样格式不支持。");
            var validBits = BinaryPrimitives.ReadUInt16LittleEndian(format[18..]);
            if (validBits == 0 || validBits > bits) throw new InvalidDataException("音频有效位数无效。");
        }
        if (rate is < 8000 or > 384000 || channels is < 1 or > 32 || align != channels * (bits / 8)
            || (tag == 1 ? bits is not (16 or 24 or 32) : tag != 3 || bits is not (32 or 64)))
            throw new NotSupportedException("当前音频设备的采样格式不支持。");
        return new(rate, channels, align, bits, tag == 3);
    }

    public float ReadMono(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < BlockAlign) throw new InvalidDataException("音频帧不完整。");
        double sum = 0;
        for (var channel = 0; channel < Channels; channel++)
        {
            var value = frame[(channel * Bits / 8)..];
            double sample = Float ? Bits == 32
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(value))
                : Bits == 16 ? BinaryPrimitives.ReadInt16LittleEndian(value) / 32768d
                : Bits == 24 ? ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608d
                : BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648d;
            if (double.IsFinite(sample)) sum += Math.Clamp(sample, -1, 1);
        }
        return (float)(sum / Channels);
    }
}

internal static class AudioSpectrumDsp
{
    public static float[] ComputeBands(ReadOnlySpan<float> samples, int sampleRate, int count)
    {
        var size = samples.Length;
        if (size < 64 || (size & (size - 1)) != 0 || sampleRate < 8000 || count is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(samples));
        var values = new Complex[size];
        double mean = 0;
        foreach (var value in samples) mean += float.IsFinite(value) ? value : 0;
        mean /= size;
        for (var i = 0; i < size; i++)
            values[i] = ((float.IsFinite(samples[i]) ? samples[i] : 0) - mean) * (.5 - .5 * Math.Cos(2 * Math.PI * i / (size - 1)));
        for (int i = 1, j = 0; i < size; i++)
        {
            var bit = size >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var length = 2; length <= size; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var offset = 0; offset < size; offset += length)
            {
                var phase = Complex.One;
                for (var i = 0; i < length / 2; i++)
                {
                    var even = values[offset + i];
                    var odd = values[offset + i + length / 2] * phase;
                    values[offset + i] = even + odd;
                    values[offset + i + length / 2] = even - odd;
                    phase *= step;
                }
            }
        }
        var bands = new float[count];
        var high = Math.Min(16000d, sampleRate * .48);
        for (var band = 0; band < count; band++)
        {
            var startHz = 40 * Math.Pow(high / 40, (double)band / count);
            var endHz = 40 * Math.Pow(high / 40, (double)(band + 1) / count);
            var start = Math.Clamp((int)Math.Ceiling(startHz * size / sampleRate), 1, size / 2 - 1);
            var end = Math.Clamp((int)Math.Ceiling(endHz * size / sampleRate), start + 1, size / 2);
            double peak = 0;
            for (var bin = start; bin < end; bin++) peak = Math.Max(peak, values[bin].Magnitude * 4 / size);
            // Absolute -66 dBFS noise floor; no auto-normalization that could amplify silence.
            bands[band] = peak <= .0005 ? 0 : (float)Math.Clamp((20 * Math.Log10(peak) + 66) / 60, 0, 1);
        }
        return bands;
    }
}
