using GProControl.Models;
using GProControl.Protocol;

namespace GProControl.Services;

/// <summary>Pure software matrix rendering. Physical key positions require separate hardware validation.</summary>
public static class KeyboardPixelRenderer
{
    public static KeyboardPixel[] DiagnosticFrame(double seconds) => Enumerable.Range(0,KeyboardPixelCodec.PixelCount).Select(index =>
    {
        var x=index%KeyboardPixelCodec.Columns;
        if(seconds<8) return new KeyboardPixel((byte)index,x<6 ? (byte)160 : (byte)0,x is >=6 and <12 ? (byte)160 : (byte)0,x>=12 ? (byte)160 : (byte)0);
        var lit=x==(int)((seconds-8)*3)%KeyboardPixelCodec.Columns;
        return new KeyboardPixel((byte)index,lit ? (byte)160 : (byte)0,lit ? (byte)160 : (byte)0,lit ? (byte)160 : (byte)0);
    }).ToArray();
    /// <param name="direction">0: up from the bottom; 1: down from the top; 2: left from the right; 3: right from the left.</param>
    public static KeyboardPixel[] Spectrum(float[] bands, int palette, int direction, byte brightness)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Length != KeyboardPixelCodec.Columns) throw new ArgumentOutOfRangeException(nameof(bands), "Expected 17 logarithmic frequency bands.");
        if ((uint)palette >= LightingPresetCatalog.AudioPalettes.Count) throw new ArgumentOutOfRangeException(nameof(palette));
        if (direction is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(direction));
        var gain = Gain(brightness);
        var result = new KeyboardPixel[KeyboardPixelCodec.PixelCount];
        for (var row = 0; row < KeyboardPixelCodec.Rows; row++)
        for (var column = 0; column < KeyboardPixelCodec.Columns; column++)
        {
            double level, distance, position;
            var vertical = direction < 2;
            if (vertical)
            {
                level = Level(bands[column]);
                distance = direction == 0 ? KeyboardPixelCodec.Rows - 1 - row : row;
                position = column / (double)(KeyboardPixelCodec.Columns - 1);
            }
            else
            {
                // Seven display rows cover all 17 input bins. Max pooling keeps narrow tones visible.
                var start = row * bands.Length / KeyboardPixelCodec.Rows;
                var end = (row + 1) * bands.Length / KeyboardPixelCodec.Rows;
                level = 0;
                for (var band = start; band < end; band++) level = Math.Max(level, Level(bands[band]));
                distance = direction == 2 ? KeyboardPixelCodec.Columns - 1 - column : column;
                position = row / (double)(KeyboardPixelCodec.Rows - 1);
            }
            var coverage = Math.Clamp(level * (vertical ? KeyboardPixelCodec.Rows : KeyboardPixelCodec.Columns) - distance, 0, 1);
            var color = PaletteColor(palette, position);
            result[row * KeyboardPixelCodec.Columns + column] = Scale(
                new((byte)(row * KeyboardPixelCodec.Columns + column), color.Red, color.Green, color.Blue), gain * coverage);
        }
        return result;
    }

    public static KeyboardPixel[] MapScreenColors(IReadOnlyList<KeyboardPixel> sampled, byte brightness)
    {
        KeyboardPixelCodec.Validate(sampled);
        var gain = Gain(brightness);
        return sampled.Select(pixel => Scale(pixel, gain)).ToArray();
    }

    private static double Level(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    private static double Gain(byte brightness) => brightness <= 3 ? (brightness + 1) / 4d : throw new ArgumentOutOfRangeException(nameof(brightness));
    private static KeyboardPixel Scale(KeyboardPixel pixel, double factor) => new(pixel.Index,
        (byte)Math.Clamp(Math.Round(pixel.Red * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(pixel.Green * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(pixel.Blue * factor), 0, 255));

    private static KeyboardPixel PaletteColor(int palette, double position)
    {
        var stops = LightingPresetCatalog.AudioPalettes[palette].Stops!;
        var upper = stops.FirstOrDefault(stop => stop.Offset >= position) ?? stops[^1];
        var lower = stops.LastOrDefault(stop => stop.Offset <= position) ?? stops[0];
        var fraction = upper.Offset == lower.Offset ? 0 : (position - lower.Offset) / (upper.Offset - lower.Offset);
        var from = Convert.FromHexString(lower.Color[3..]);
        var to = Convert.FromHexString(upper.Color[3..]);
        return new(0, (byte)Math.Round(from[0] + (to[0] - from[0]) * fraction),
            (byte)Math.Round(from[1] + (to[1] - from[1]) * fraction),
            (byte)Math.Round(from[2] + (to[2] - from[2]) * fraction));
    }
}
