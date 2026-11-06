using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: IconGenerator <output.ico>");
    return 1;
}

var outputPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var images = sizes.Select(RenderIcon).ToArray();

using var stream = File.Create(outputPath);
using var writer = new BinaryWriter(stream);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)images.Length);

var offset = 6 + images.Length * 16;
for (var i = 0; i < images.Length; i++)
{
    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(images[i].Length);
    writer.Write(offset);
    offset += images[i].Length;
}

foreach (var image in images)
{
    writer.Write(image);
}

File.WriteAllBytes(Path.ChangeExtension(outputPath, ".preview.png"), images[^1]);
Console.WriteLine(outputPath);
return 0;

static byte[] RenderIcon(int size)
{
    const double canvas = 256;
    var visual = new DrawingVisual();
    using (var drawing = visual.RenderOpen())
    {
        var background = new LinearGradientBrush(
            Color.FromRgb(21, 31, 54),
            Color.FromRgb(19, 96, 122),
            new Point(0, 0),
            new Point(1, 1));
        drawing.DrawRoundedRectangle(background, null, new Rect(8, 8, 240, 240), 54, 54);

        var glow = new RadialGradientBrush
        {
            Center = new Point(0.72, 0.2),
            GradientOrigin = new Point(0.72, 0.2),
            RadiusX = 0.75,
            RadiusY = 0.75
        };
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(150, 54, 231, 255), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 54, 231, 255), 1));
        drawing.DrawRoundedRectangle(glow, null, new Rect(8, 8, 240, 240), 54, 54);

        var ringPen = new Pen(new SolidColorBrush(Color.FromRgb(124, 238, 255)), 22)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var ring = new StreamGeometry();
        using (var context = ring.Open())
        {
            context.BeginFigure(new Point(174, 75), false, false);
            context.BezierTo(new Point(145, 45), new Point(90, 48), new Point(66, 83), true, true);
            context.BezierTo(new Point(39, 122), new Point(51, 178), new Point(92, 200), true, true);
            context.BezierTo(new Point(125, 217), new Point(169, 207), new Point(188, 178), true, true);
        }
        drawing.DrawGeometry(null, ringPen, ring);

        var bolt = Geometry.Parse("M 130,83 L 103,139 L 137,139 L 116,190 L 181,121 L 145,121 L 170,83 Z");
        drawing.DrawGeometry(new SolidColorBrush(Colors.White), null, bolt);
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    visual.Transform = new ScaleTransform(size / canvas, size / canvas);
    bitmap.Render(visual);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var output = new MemoryStream();
    encoder.Save(output);
    return output.ToArray();
}
