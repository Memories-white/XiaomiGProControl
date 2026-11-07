using System.Buffers.Binary;
using System.Runtime.InteropServices;
using GProControl.Services;

var sampleRate = 48000;
var silence = AudioSpectrumDsp.ComputeBands(new float[2048], sampleRate, 17);
Require(silence.All(value => value == 0), "Silence must remain black.");
foreach (var frequency in new[] {250, 1000, 4000})
{
    var signal = Enumerable.Range(0,2048).Select(i => .25f * MathF.Sin(2 * MathF.PI * frequency * i / sampleRate)).ToArray();
    var bands = AudioSpectrumDsp.ComputeBands(signal, sampleRate, 17);
    var index = Array.IndexOf(bands, bands.Max());
    var expected = (int)(Math.Log((double)frequency / 40) / Math.Log(16000d / 40) * 17);
    Require(Math.Abs(index - expected) <= 1, $"{frequency} Hz must peak in the expected logarithmic band.");
    Require(bands.Max() > .7, "Audible tone should light its band.");
    var quiet = AudioSpectrumDsp.ComputeBands(signal.Select(value => value * .0001f).ToArray(), sampleRate,17);
    Require(quiet.Max() == 0, "Sub-threshold noise must not be auto-amplified.");
    Console.WriteLine($"tone={frequency}; band={index}; expected={expected}");
}
var dc = AudioSpectrumDsp.ComputeBands(Enumerable.Repeat(.5f,2048).ToArray(), sampleRate,17);
Require(dc.Max() == 0, "DC offset must not light low-frequency bands.");
foreach (var bits in new[] {16,24,32})
{
    var format = Format(bits, false, false);
    var parsed = AudioSampleFormat.Parse(format);
    var frame = new byte[bits / 8];
    if (bits == 16) BinaryPrimitives.WriteInt16LittleEndian(frame, -16384);
    else if (bits == 24) { frame[2] = 0xC0; }
    else BinaryPrimitives.WriteInt32LittleEndian(frame, -1073741824);
    Require(Math.Abs(parsed.ReadMono(frame) + .5f) < .0001, $"Signed PCM {bits}-bit decoding failed.");
}
foreach (var extensible in new[] {false,true})
{
    var format = AudioSampleFormat.Parse(Format(32,true,extensible));
    var frame = BitConverter.GetBytes(.25f);
    Require(format.ReadMono(frame) == .25f, "Float format decoding failed.");
    Require(format.ReadMono(BitConverter.GetBytes(float.NaN)) == 0, "Nonfinite audio must be ignored.");
}
var unsupported = Format(16,false,false);
BinaryPrimitives.WriteUInt16LittleEndian(unsupported,6);
try { AudioSampleFormat.Parse(unsupported); throw new Exception("Compressed audio must be rejected."); }
catch (NotSupportedException) { }
Console.WriteLine("Audio FFT, silence, DC and PCM format tests passed.");

if(args.Contains("--tone"))
{
    using var reader=new AudioSpectrumReader();
    foreach(var frequency in new[]{400,4000})
    {
        const int rate=48000,frames=48000;
        var wave=new byte[44+frames*2];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wave,0); BitConverter.GetBytes(wave.Length-8).CopyTo(wave,4);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave,8); BitConverter.GetBytes(16).CopyTo(wave,16);
        BitConverter.GetBytes((short)1).CopyTo(wave,20); BitConverter.GetBytes((short)1).CopyTo(wave,22);
        BitConverter.GetBytes(rate).CopyTo(wave,24); BitConverter.GetBytes(rate*2).CopyTo(wave,28);
        BitConverter.GetBytes((short)2).CopyTo(wave,32); BitConverter.GetBytes((short)16).CopyTo(wave,34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wave,36); BitConverter.GetBytes(frames*2).CopyTo(wave,40);
        for(var i=0;i<frames;i++) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44+i*2),(short)(32767*.05*Math.Sin(2*Math.PI*frequency*i/rate)));
        var pin=GCHandle.Alloc(wave,GCHandleType.Pinned);
        try
        {
            Require(PlaySound(pin.AddrOfPinnedObject(),IntPtr.Zero,7),"Test tone playback failed.");
            var maxima=new float[17];
            for(var i=0;i<16;i++) {Thread.Sleep(50);var bands=reader.ReadBands();for(var b=0;b<17;b++)maxima[b]=Math.Max(maxima[b],bands[b]);}
            var index=Array.IndexOf(maxima,maxima.Max());
            var expected=(int)(Math.Log(frequency/40d)/Math.Log(16000d/40)*17);
            Require(reader.SamplesCaptured>0 && maxima.Max()>.05f && Math.Abs(index-expected)<=1,"Live playback frequency was not captured in the expected band.");
            Console.WriteLine($"live-tone={frequency}; band={index}; expected={expected}; frames={reader.SamplesCaptured}; verified=True");
        }
        finally {PlaySound(IntPtr.Zero,IntPtr.Zero,0);pin.Free();}
        Thread.Sleep(400);
    }
}

if (args.Contains("--capture"))
{
    using var reader = new AudioSpectrumReader(args.Contains("--microphone"));
    var maximum = 0f;
    for (var i = 0; i < 30; i++)
    {
        Thread.Sleep(50);
        var bands = reader.ReadBands();
        Require(bands.Length == 17 && bands.All(value => float.IsFinite(value) && value is >= 0 and <= 1), "Invalid captured spectrum.");
        maximum = Math.Max(maximum,bands.Max());
    }
    Console.WriteLine($"capture-initialized=True; rate={reader.SampleRate}; frames={reader.SamplesCaptured}; max={maximum:F3}");
}

static byte[] Format(int bits, bool floating, bool extensible)
{
    var bytes = new byte[extensible ? 40 : 18];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes,(ushort)(extensible ? 0xFFFE : floating ? 3 : 1));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2),1);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),48000);
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8),48000 * bits / 8);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12),(ushort)(bits / 8));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14),(ushort)bits);
    if (extensible)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16),22);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),(ushort)bits);
        new Guid(floating ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").TryWriteBytes(bytes.AsSpan(24));
    }
    return bytes;
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

[DllImport("winmm.dll",EntryPoint="PlaySoundW")]
static extern bool PlaySound(IntPtr sound,IntPtr module,uint flags);
