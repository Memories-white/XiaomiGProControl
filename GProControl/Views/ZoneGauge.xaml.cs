using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace GProControl.Views;

public sealed partial class ZoneGauge : UserControl
{
    private const int SegmentCount = 32;
    private readonly Line[] _segments = new Line[SegmentCount];
    private readonly SolidColorBrush _active = new(Color.FromArgb(255, 38, 131, 255));
    private readonly SolidColorBrush _inactive = new(Color.FromArgb(255, 62, 69, 79));
    private int _filledSegments;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ZoneGauge), new PropertyMetadata(0d, OnValueChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ZoneGauge), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ReadingProperty = DependencyProperty.Register(
        nameof(Reading), typeof(string), typeof(ZoneGauge), new PropertyMetadata("—"));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Reading { get => (string)GetValue(ReadingProperty); set => SetValue(ReadingProperty, value); }

    public ZoneGauge()
    {
        InitializeComponent();
        var tickBrush = new SolidColorBrush(Color.FromArgb(255, 67, 78, 91));
        for (var index = 0; index < SegmentCount; index++)
        {
            var angle = (155 + 230d * index / (SegmentCount - 1)) * Math.PI / 180;
            var outer = CreateLine(angle, 92, 102, 6, _inactive);
            _segments[index] = outer;
            Dial.Children.Add(outer);
            Dial.Children.Add(CreateLine(angle, index % 4 == 0 ? 76 : 79, 83, 1, tickBrush));
        }
        ApplyValue();
    }

    private static Line CreateLine(double angle, double inner, double outer, double thickness, Brush brush)
    {
        var x = Math.Cos(angle);
        var y = Math.Sin(angle);
        return new Line
        {
            X1 = 110 + x * inner, Y1 = 103 + y * inner,
            X2 = 110 + x * outer, Y2 = 103 + y * outer,
            Stroke = brush, StrokeThickness = thickness
        };
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((ZoneGauge)sender).ApplyValue();

    private void ApplyValue()
    {
        if (_segments[0] is null) return;
        var count = double.IsFinite(Value) ? (int)Math.Round(Math.Clamp(Value, 0, 100) * SegmentCount / 100) : 0;
        for (int index = Math.Min(count, _filledSegments); index < Math.Max(count, _filledSegments); index++)
            _segments[index].Stroke = index < count ? _active : _inactive;
        _filledSegments = count;
    }
}
