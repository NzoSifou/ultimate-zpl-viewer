using Microsoft.UI.Xaml;

namespace Ultimate_ZPL_Viewer;

// Application-wide accent override.
//
// Confirmed WinUI 3 workaround (microsoft-ui-xaml issue #6394): the accent
// brushes derive from the SystemAccentColor* COLOR resources — Light2 in dark
// theme, Dark1 in light theme. Those keys must exist as <Color> entries in
// App.xaml (declaring brushes, ThemeDictionaries, or building dictionaries in
// code either has no effect or crashes the framework). At runtime the colors
// are replaced, then the root element's theme is reloaded ("ReloadPageTheme"
// trick from Microsoft's fluent-xaml-theme-editor) so every accent brush —
// normal, hover and pressed — is re-resolved from the new values.
public static class AccentColorService
{
    // Sets the startup accent from the saved settings; must run before the main
    // window is created so controls resolve the colors on first load (no reload
    // needed). Defensive: a failure here must never block startup.
    public static void ApplyAtStartup(Application app)
    {
        try
        {
            var settings = AppSettings.Load();
            SetColors(app, settings.UseSystemAccent
                ? null
                : ZplColorSchemeService.ParseHexColor(settings.CustomAccent, Microsoft.UI.Colors.DodgerBlue));
        }
        catch
        {
            // Keep the system accent — never prevent the app from starting.
        }
    }

    // Applies a new accent at runtime. null → system accent.
    public static void Apply(Windows.UI.Color? custom, FrameworkElement root)
    {
        try
        {
            SetColors(Application.Current, custom);

            // Reload the theme so already-resolved accent brushes pick up the new colors.
            var requested = root.RequestedTheme;
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            root.RequestedTheme = requested;
        }
        catch
        {
            // Non-fatal: worst case the accent stays unchanged until restart.
        }
    }

    private static void SetColors(Application app, Windows.UI.Color? custom)
    {
        Windows.UI.Color light1, light2, light3, dark1, dark2, dark3;
        if (custom is null)
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            light1 = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1);
            light2 = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight2);
            light3 = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight3);
            dark1  = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark1);
            dark2  = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark2);
            dark3  = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentDark3);
        }
        else
        {
            var accent = custom.Value;
            light1 = Blend(accent, 0xFF, 0.2);
            light2 = Blend(accent, 0xFF, 0.4);
            light3 = Blend(accent, 0xFF, 0.6);
            dark1  = Blend(accent, 0x00, 0.2);
            dark2  = Blend(accent, 0x00, 0.4);
            dark3  = Blend(accent, 0x00, 0.6);
        }

        // NOTE: the root "SystemAccentColor" key is intentionally NOT overridden —
        // touching that special system-injected key crashes the framework.
        var res = app.Resources;
        res["SystemAccentColorLight1"] = light1;
        res["SystemAccentColorLight2"] = light2;
        res["SystemAccentColorLight3"] = light3;
        res["SystemAccentColorDark1"]  = dark1;
        res["SystemAccentColorDark2"]  = dark2;
        res["SystemAccentColorDark3"]  = dark3;
    }

    private static Windows.UI.Color Blend(Windows.UI.Color a, byte target, double t) =>
        Windows.UI.Color.FromArgb(255,
            (byte)(a.R + (target - a.R) * t),
            (byte)(a.G + (target - a.G) * t),
            (byte)(a.B + (target - a.B) * t));
}
