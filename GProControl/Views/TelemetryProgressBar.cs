using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace GProControl.Views;

public sealed class TelemetryProgressBar : ProgressBar
{
    private Rectangle? _fill;
    private Storyboard? _transition;

    public TelemetryProgressBar()
    {
        ValueChanged += (_, _) => UpdateFill(animate: true);
        SizeChanged += (_, _) => UpdateFill(animate: false);
        Unloaded += (_, _) => UpdateFill(animate: false);
    }

    protected override void OnApplyTemplate()
    {
        _transition?.Stop();
        _transition = null;
        base.OnApplyTemplate();
        _fill = GetTemplateChild("TelemetryFill") as Rectangle;
        UpdateFill(animate: false);
    }

    private void UpdateFill(bool animate)
    {
        if (_fill is not { } fill) return;

        var fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        var target = ActualWidth * Math.Clamp(fraction, 0, 1);
        var previous = fill.ActualWidth;
        _transition?.Stop();
        _transition = null;
        fill.Width = target;
        if (!animate || !IsLoaded || Math.Abs(target - previous) < 0.5 || !MainWindow.AnimationsEnabled())
            return;

        var widthAnimation = new DoubleAnimation
        {
            From = previous,
            To = target,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(widthAnimation, fill);
        Storyboard.SetTargetProperty(widthAnimation, nameof(FrameworkElement.Width));
        _transition = new Storyboard { FillBehavior = FillBehavior.Stop };
        _transition.Children.Add(widthAnimation);
        _transition.Begin();
    }
}
