using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GProControl.Views;

internal static class RoutineNotice
{
    internal static void ApplyVisibility(InfoBar notice, bool routine) =>
        notice.Visibility = routine && App.Settings.Current.HideRoutineNotices
            ? Visibility.Collapsed
            : Visibility.Visible;
}
