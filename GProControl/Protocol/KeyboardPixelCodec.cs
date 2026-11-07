using GProControl.Models;

namespace GProControl.Protocol;

/// <summary>Encodes the vendor's write-only B21 CMD 4 format; encoding is not hardware confirmation.</summary>
public static class KeyboardPixelCodec
{
    public const int Rows = 7;
    public const int Columns = 17;
    public const int PixelCount = Rows * Columns;
    public const int PixelsPerReport = 12;

    public static IReadOnlyList<byte[]> Encode(IReadOnlyList<KeyboardPixel> pixels)
    {
        Validate(pixels);
        var reports = new List<byte[]>((pixels.Count + PixelsPerReport - 1) / PixelsPerReport);
        for (var start = 0; start < pixels.Count; start += PixelsPerReport)
        {
            var report = new byte[65];
            report[1] = 0x57;
            report[2] = 0xAB;
            report[3] = 4;
            var count = Math.Min(PixelsPerReport, pixels.Count - start);
            for (var i = 0; i < count; i++)
            {
                var pixel = pixels[start + i];
                var offset = 4 + i * 5;
                report[offset] = pixel.Index;
                report[offset + 1] = 0x7F;
                report[offset + 2] = (byte)(pixel.Red >> 1);
                report[offset + 3] = (byte)(pixel.Green >> 1);
                report[offset + 4] = (byte)(pixel.Blue >> 1);
            }
            // A full report leaves its final byte zero. Only a partial report has a sentinel.
            if (count < PixelsPerReport) report[4 + count * 5] = 0xFF;
            reports.Add(report);
        }
        return reports;
    }

    public static void Validate(IReadOnlyList<KeyboardPixel> pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Count > PixelCount) throw new ArgumentOutOfRangeException(nameof(pixels), "The B21 matrix has 119 locations.");
        Span<bool> seen = stackalloc bool[PixelCount];
        seen.Clear();
        foreach (var pixel in pixels)
        {
            if (pixel.Index >= PixelCount || seen[pixel.Index])
                throw new ArgumentOutOfRangeException(nameof(pixels), "Matrix indices must be unique and between 0 and 118.");
            seen[pixel.Index] = true;
        }
    }
}
