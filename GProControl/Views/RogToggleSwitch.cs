using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

public static class RogToggleSwitch
{
    public static readonly DependencyProperty AdaptProperty = DependencyProperty.RegisterAttached(
        "Adapt", typeof(bool), typeof(RogToggleSwitch), new PropertyMetadata(false, OnAdaptChanged));

    public static bool GetAdapt(DependencyObject element) => (bool)element.GetValue(AdaptProperty);
    public static void SetAdapt(DependencyObject element, bool value) => element.SetValue(AdaptProperty, value);

    private static void OnAdaptChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ToggleSwitch toggle || args.NewValue is not true) return;

        MainWindow? window = null;
        void ApplyAppearance(object? sender, EventArgs args)
        {
            if (window is not null && window.ActiveAppearance != Services.AppAppearance.Windows)
                toggle.Template = (ControlTemplate)Application.Current.Resources["RogToggleTemplate"];
            else
                toggle.ClearValue(Control.TemplateProperty);
        }

        toggle.Loaded += (_, _) =>
        {
            window = App.MainWindow;
            if (window is not null) window.AppearanceChanged += ApplyAppearance;
            ApplyAppearance(null, EventArgs.Empty);
        };
        toggle.Unloaded += (_, _) =>
        {
            if (window is not null) window.AppearanceChanged -= ApplyAppearance;
            window = null;
        };
    }
}
