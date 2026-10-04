using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// The settings' search box, drawn like Windows' Settings: a rounded field, the
/// magnifier inside it on the left, the accent line under it while typing. One
/// lives in the title bar, its twin at the top of the settings' categories in
/// full screen (where there is no title bar).
/// </summary>
internal static class SettingsSearchBox
{
    public static (Grid Host, TextBox Box) Create(double width)
    {
        var box = new TextBox
        {
            Height = 34,
            CornerRadius = new CornerRadius(8),
            // Room on the left for the magnifier.
            Padding = new Thickness(38, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = LocalizationService.Get("settings.search.placeholder"),
        };
        var icon = new FontIcon
        {
            Glyph = "", FontSize = 14, Opacity = 0.85,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var host = new Grid { Width = width, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(box);
        host.Children.Add(icon);
        return (host, box);
    }
}
