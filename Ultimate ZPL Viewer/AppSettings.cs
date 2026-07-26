using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Ultimate_ZPL_Viewer;

public enum LengthUnit
{
    Millimeters,
    Centimeters,
    Inches
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
    // Dark app chrome, but the preview area uses the light background.
    DarkLightPreview
}

public sealed class AppSettings
{
    // Persisted as a plain JSON file under %LOCALAPPDATA%\Ultimate ZPL Viewer.
    // (The app is unpackaged, so there is no MSIX LocalSettings container.)
    private static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultimate ZPL Viewer", "settings.json");

    public LengthUnit Unit { get; set; } = LengthUnit.Millimeters;
    public double DefaultDpmm { get; set; } = 8;      // default density (new documents AND opening a file)
    public string DefaultPrinter { get; set; } = "last";
    public string LastPrinter { get; set; } = string.Empty;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool UseSystemAccent { get; set; } = true;
    public string CustomAccent { get; set; } = "#0078D4";
    public string Language { get; set; } = "fr";
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowPreviewGrid { get; set; } = true;
    // Grid colour: default (faint, theme-based) or a custom ARGB (#AARRGGBB).
    public bool UseCustomGridColor { get; set; }
    public string CustomGridColor { get; set; } = "#40808080";
    public bool SkipPrinterInstallPrompt { get; set; }

    // Static analysis: show the low-priority warnings (clean-code hints) too.
    public bool ShowLowWarnings { get; set; } = true;

    // Editor
    public int EditorFontSize { get; set; } = 14;
    public bool EditorWordWrap { get; set; }
    public bool EditorMinimap { get; set; }

    // Preview
    public double PreviewGridSpacing { get; set; } = 24;
    public string PreviewGridSpacingUnit { get; set; } = "px"; // px | mm | cm | in
    public double DefaultRotation { get; set; }

    // Preview rulers (top / left), independently toggleable, measuring from the
    // label's top-left corner in the chosen unit.
    public bool ShowRulerHorizontal { get; set; }
    public bool ShowRulerVertical { get; set; }
    public string RulerUnit { get; set; } = "cm";  // px | mm | cm | in
    public int RulerSubdivisions { get; set; } = 4; // minor ticks per major (1..10)
    public int RulerBandSize { get; set; } = 1;     // 0 = thin, 1 = normal, 2 = large
    public double DefaultZoom { get; set; } // 0 = fit to window, otherwise a percentage

    // New document size: 0 = ask each time, 1 = use the default size below (in mm).
    public int NewDocSizeMode { get; set; }
    public double NewDocWidthMm { get; set; } = 100;
    public double NewDocHeightMm { get; set; } = 60;

    // General
    public bool ConfirmBeforePrint { get; set; } = true;
    public bool ReopenLastFile { get; set; }
    public string LastFilePath { get; set; } = string.Empty;
    // Most-recently-opened files, newest first (capped at RecentFilesMax).
    public List<string> RecentFiles { get; set; } = new();
    // Saved documents open at the last graceful exit (tab order); used by
    // ReopenLastFile to restore the whole tab set.
    public List<string> OpenFiles { get; set; } = new();
    public bool ShowFilePathInTitle { get; set; } = true;
    // Show the full path in parentheses in a tab's hover tooltip.
    public bool ShowPathInTabTooltip { get; set; } = true;
    // Show the size/dpi/zoom caption at the bottom-right of the preview.
    public bool ShowPreviewCaption { get; set; } = true;

    // Layout
    public bool SwapEditorPreview { get; set; }
    // Toolbar + editor visibility (title-bar / collapse-handle toggles); persist
    // across sessions (unless the app was launched with a --hide override).
    public bool ToolbarVisible { get; set; } = true;
    public bool EditorVisible { get; set; } = true;
    // Toolbar layout: up to 3 rows of group ids. Each row still wraps automatically
    // on narrow windows. Row 0 holds every group by default.
    public List<List<string>> ToolbarRows { get; set; } = new()
    {
        new List<string>(ToolbarItems.AllIds),
        new List<string>(),
        new List<string>(),
    }; // unknown ids from older versions are dropped by NormalizeRows

    // Manual physical screen sizes (monitor interface id → diagonal in inches),
    // used to render at real size when the EDID doesn't report a physical size.
    public Dictionary<string, double> ManualScreenSizesInches { get; set; } = new();
    public bool ScreenSizePromptDismissed { get; set; }

    // Automatic document sizing.
    // AutoDocSizeMode: 0 = follow ^PW/^LL only; 1 = ^PW/^LL, else computed from elements.
    public bool AutoDocSize { get; set; } = true;
    public int AutoDocSizeMode { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt/unreadable settings — fall back to defaults rather than crash.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Best effort: a failed save must never take the app down.
        }
    }

    // Resets every setting to its default value and persists.
    public void ResetToDefaults()
    {
        var defaults = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties())
            if (p.CanRead && p.CanWrite)
                p.SetValue(this, p.GetValue(defaults));
        Save();
    }

    public ElementTheme ToElementTheme()
    {
        return Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            ThemePreference.DarkLightPreview => ElementTheme.Dark, // dark chrome; preview handled separately
            _ => ElementTheme.Default
        };
    }
}
