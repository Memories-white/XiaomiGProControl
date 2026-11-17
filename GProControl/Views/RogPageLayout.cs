using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace GProControl.Views;

public sealed class RogPageLayout : Panel
{
    public static readonly DependencyProperty BandProperty = DependencyProperty.RegisterAttached(
        "Band", typeof(int), typeof(RogPageLayout), new PropertyMetadata(0));
    public static readonly DependencyProperty LaneProperty = DependencyProperty.RegisterAttached(
        "Lane", typeof(int), typeof(RogPageLayout), new PropertyMetadata(0));

    private readonly Rectangle _headerRule = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(255, 255, 47, 76)),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private MainWindow? _window;
    private bool _rog;

    public Thickness Padding { get; set; }
    public double Spacing { get; set; } = 18;
    public bool ResponsiveColumns { get; set; }
    internal bool IsRogLayout => _rog;
    public event EventHandler? LayoutModeChanged;

    public RogPageLayout()
    {
        Loaded += (_, _) =>
        {
            if (!Children.Contains(_headerRule)) Children.Add(_headerRule);
            _window = App.MainWindow;
            if (_window is not null) _window.AppearanceChanged += AppearanceChanged;
            ApplyAppearance();
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null) _window.AppearanceChanged -= AppearanceChanged;
            _window = null;
        };
    }

    public static int GetBand(DependencyObject item) => (int)item.GetValue(BandProperty);
    public static void SetBand(DependencyObject item, int value) => item.SetValue(BandProperty, value);
    public static int GetLane(DependencyObject item) => (int)item.GetValue(LaneProperty);
    public static void SetLane(DependencyObject item, int value) => item.SetValue(LaneProperty, value);

    private void AppearanceChanged(object? sender, EventArgs args) => ApplyAppearance();

    private void ApplyAppearance()
    {
        var useRog = _window?.IsRogAppearanceActive == true;
        if (_rog == useRog) return;

        _rog = useRog;
        _headerRule.Visibility = _rog ? Visibility.Visible : Visibility.Collapsed;
        LayoutModeChanged?.Invoke(this, EventArgs.Empty);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 960;
        return new Size(width, Layout(width, measure: true));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, measure: false);
        return finalSize;
    }

    private double Layout(double availableWidth, bool measure)
    {
        var padding = _rog ? new Thickness(24, 24, 24, 28) : Padding;
        double width = Math.Max(0, availableWidth - padding.Left - padding.Right);
        bool columns = (_rog || ResponsiveColumns) && width >= (ResponsiveColumns ? 680 : 650);
        double gap = _rog ? 14 : Spacing;
        double y = padding.Top;
        var content = Children.Where(child => child != _headerRule && child.Visibility == Visibility.Visible);

        if (_rog && measure) _headerRule.Measure(new Size(3, 30));
        if (_rog && !measure) _headerRule.Arrange(new Rect(padding.Left - 9, y + 3, 3, 30));

        if (!columns)
        {
            if (_rog || ResponsiveColumns) content = content.OrderBy(GetBand).ThenBy(GetLane);
            y = LayoutLane(content, padding.Left, y, width, gap, measure);
        }
        else
        {
            double primaryWidth = (width - gap) * (ResponsiveColumns ? 0.5 : 0.56);
            foreach (var band in content.GroupBy(GetBand).OrderBy(group => group.Key))
            {
                y = LayoutLane(band.Where(child => GetLane(child) == 0), padding.Left, y, width, gap, measure);
                double left = LayoutLane(band.Where(child => GetLane(child) == 1), padding.Left, y, primaryWidth, gap, measure);
                double right = LayoutLane(band.Where(child => GetLane(child) == 2), padding.Left + primaryWidth + gap,
                    y, width - primaryWidth - gap, gap, measure);
                y = Math.Max(left, right);
            }
        }

        return Math.Max(padding.Top, y - gap) + padding.Bottom;
    }

    private static double LayoutLane(IEnumerable<UIElement> children, double x, double y, double width, double gap, bool measure)
    {
        foreach (var child in children)
        {
            if (measure) child.Measure(new Size(width, double.PositiveInfinity));
            double height = child.DesiredSize.Height;
            if (!measure) child.Arrange(new Rect(x, y, width, height));
            y += height + gap;
        }
        return y;
    }
}
