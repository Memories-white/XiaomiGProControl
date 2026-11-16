using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public static class RogSection
{
    public static readonly DependencyProperty AdaptProperty = DependencyProperty.RegisterAttached(
        "Adapt", typeof(bool), typeof(RogSection), new PropertyMetadata(false, OnAdaptChanged));

    public static bool GetAdapt(DependencyObject item) => (bool)item.GetValue(AdaptProperty);
    public static void SetAdapt(DependencyObject item, bool value) => item.SetValue(AdaptProperty, value);

    private static void OnAdaptChanged(DependencyObject item, DependencyPropertyChangedEventArgs args)
    {
        if (item is not FrameworkElement element || args.NewValue is not true) return;

        var styles = element switch
        {
            Border => ("DashboardCardStyle", "RogSectionStyle", "LegionSectionStyle"),
            TextBlock => ("SectionTitleStyle", "RogSectionTitleStyle", "LegionSectionTitleStyle"),
            Button => ("SectionButtonStyle", "RogSectionButtonStyle", "LegionSectionButtonStyle"),
            Expander => ("SectionExpanderStyle", "RogSectionExpanderStyle", "LegionSectionExpanderStyle"),
            _ => throw new InvalidOperationException("Unsupported section element.")
        };

        MainWindow? window = null;
        void Apply(object? sender, EventArgs args)
        {
            var key = window?.ActiveAppearance switch
            {
                Services.AppAppearance.Rog => styles.Item2,
                Services.AppAppearance.Legion => styles.Item3,
                _ => styles.Item1
            };
            element.Style = (Style)Application.Current.Resources[key];
        }

        element.Loaded += (_, _) =>
        {
            window = App.MainWindow;
            if (window is not null) window.AppearanceChanged += Apply;
            Apply(null, EventArgs.Empty);
        };
        element.Unloaded += (_, _) =>
        {
            if (window is not null) window.AppearanceChanged -= Apply;
            window = null;
        };
    }
}
