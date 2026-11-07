using GProControl.Models;
using GProControl.Protocol;
using GProControl.Services;

foreach (var count in new[] {0, 1, 11, 12, 13, 24, 119})
{
    var pixels = Enumerable.Range(0, count).Select(index => new KeyboardPixel((byte)index, 255, 129, 1)).ToArray();
    var reports = KeyboardPixelCodec.Encode(pixels);
    Require(reports.Count == (count + 11) / 12, $"Wrong packet count for {count} pixels.");
    var decoded = new List<byte>();
    foreach (var (report, packet) in reports.Select((report, index) => (report, index)))
    {
        Require(report.Length == 65 && report.AsSpan(0,4).SequenceEqual(new byte[] {0,0x57,0xAB,4}), "Wrong HID envelope.");
        var entries = Math.Min(12, count - packet * 12);
        for (var i = 0; i < entries; i++)
        {
            var offset = 4 + i * 5;
            decoded.Add(report[offset]);
            Require(report.AsSpan(offset + 1, 4).SequenceEqual(new byte[] {127,127,64,0}), "RGB must be converted to 7-bit once.");
        }
        if (entries == 12) Require(report[64] == 0, "Full packets must not have a sentinel.");
        else Require(report[4 + entries * 5] == 255 && report.AsSpan(5 + entries * 5).ToArray().All(value => value == 0), "Partial packet sentinel/padding failed.");
    }
    Require(decoded.SequenceEqual(Enumerable.Range(0,count).Select(index => (byte)index)), "Packet boundaries lost/reordered pixels.");
}
Reject(() => KeyboardPixelCodec.Encode([new(119,0,0,0)]));
Reject(() => KeyboardPixelCodec.Encode([new(2,0,0,0),new(2,1,2,3)]));
Reject(() => KeyboardPixelCodec.Encode(Enumerable.Range(0,120).Select(index => new KeyboardPixel((byte)index,0,0,0)).ToArray()));

foreach (var direction in Enumerable.Range(0,4))
{
    var silence = KeyboardPixelRenderer.Spectrum(new float[17], 0, direction, 3);
    Require(silence.Length == 119 && silence.All(pixel => !Lit(pixel)), "Silence must produce a full black frame, clearing old bars.");
    var bands = new float[17]; bands[8] = .5f;
    var rendered = KeyboardPixelRenderer.Spectrum(bands, 0, direction, 3);
    Require(rendered.Any(Lit) && rendered.Any(pixel => !Lit(pixel)), "Tone must create a localized bar.");
    if (direction < 2)
    {
        Require(rendered.Where(Lit).All(pixel => pixel.Index % 17 == 8), "Vertical tone escaped its frequency column.");
        Require(Lit(rendered[(direction == 0 ? 6 : 0) * 17 + 8]), "Vertical bar origin is reversed.");
        Require(!Lit(rendered[(direction == 0 ? 0 : 6) * 17 + 8]), "Vertical bar extends past its level.");
    }
    else
    {
        Require(rendered.Where(Lit).All(pixel => pixel.Index / 17 == 3), "Horizontal tone escaped its pooled frequency row.");
        Require(Lit(rendered[3 * 17 + (direction == 2 ? 16 : 0)]), "Horizontal bar origin is reversed.");
        Require(!Lit(rendered[3 * 17 + (direction == 2 ? 0 : 16)]), "Horizontal bar extends past its level.");
    }
    var opposite = KeyboardPixelRenderer.Spectrum(bands,0,direction ^ 1,3);
    for (var row = 0; row < 7; row++) for (var column = 0; column < 17; column++)
    {
        var other = direction < 2 ? (6-row)*17+column : row*17+16-column;
        Require(rendered[row*17+column] with {Index=0} == opposite[other] with {Index=0}, "Opposite directions must mirror the same bar.");
    }
}

var fullBands = Enumerable.Repeat(1f,17).ToArray();
var full = KeyboardPixelRenderer.Spectrum(fullBands,0,0,3);
Require(full.All(Lit) && full.Select(pixel => (pixel.Red,pixel.Green,pixel.Blue)).Distinct().Count() > 8, "Frequency colors must remain independent.");
var mixedBands = new float[17]; mixedBands[1] = 1; mixedBands[15] = .2f;
var mixed = KeyboardPixelRenderer.Spectrum(mixedBands,0,0,3);
Require(mixed.Count(pixel => pixel.Index%17==1 && Lit(pixel)) > mixed.Count(pixel => pixel.Index%17==15 && Lit(pixel)), "Different levels must retain different bar heights.");
Require(!full.SequenceEqual(KeyboardPixelRenderer.Spectrum(fullBands,3,0,3)), "Palette selection must change colors.");
foreach (byte brightness in new byte[] {0,1,2,3})
{
    var scaled = KeyboardPixelRenderer.MapScreenColors([new(1,200,80,40),new(100,0,255,128)],brightness);
    Require(scaled[0] == new KeyboardPixel(1,(byte)(50*(brightness+1)),(byte)(20*(brightness+1)),(byte)(10*(brightness+1))), "Screen brightness scaling failed.");
    Require(scaled[1].Index == 100 && scaled[1].Green > 0, "Minimum brightness must remain lit and retain indices.");
}
Require(KeyboardPixelRenderer.MapScreenColors([new(0,200,50,100)],3)[0] == new KeyboardPixel(0,200,50,100), "Full brightness must preserve sampled colors.");
var nonfinite = new float[17]; nonfinite[0] = float.NaN; nonfinite[1] = float.PositiveInfinity;
Require(KeyboardPixelRenderer.Spectrum(nonfinite,0,0,3).All(pixel => !Lit(pixel)), "Nonfinite bands must not illuminate keys.");
Reject(() => KeyboardPixelRenderer.Spectrum(new float[16],0,0,3));
Reject(() => KeyboardPixelRenderer.Spectrum(fullBands,4,0,3));
Reject(() => KeyboardPixelRenderer.Spectrum(fullBands,0,4,3));
Reject(() => KeyboardPixelRenderer.Spectrum(fullBands,0,0,4));
Console.WriteLine("Pixel packet boundaries, RGB scaling, matrix directions, palettes, silence and brightness tests passed.");

static bool Lit(KeyboardPixel pixel) => pixel.Red != 0 || pixel.Green != 0 || pixel.Blue != 0;
static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
static void Reject(Action action)
{
    try {action();}
    catch(ArgumentOutOfRangeException) {return;}
    throw new InvalidOperationException("Invalid matrix input was accepted.");
}
