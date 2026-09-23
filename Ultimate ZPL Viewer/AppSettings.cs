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

    // ── Printing ─────────────────────────────────────────────────────────────
    // Copies, layout and scale each choose between reusing whatever was used last
    // ("last") and always applying one value ("fixed"). Default is "fixed" with the
    // neutral values, so the defaults are predictable and quick print is usable
    // straight away - "last" is what makes them non-deterministic.
    public string CopiesMode { get; set; } = "fixed";     // last | fixed
    public int DefaultCopies { get; set; } = 1;
    public string LayoutMode { get; set; } = "fixed";     // last | fixed
    public string DefaultLayout { get; set; } = "portrait";
    // Copies per sheet: the same label repeated N times on one page. Classic
    // printers only - a thermal printer prints one label per feed.
    public string PerPageMode { get; set; } = "fixed";    // last | fixed
    public int DefaultPerPage { get; set; } = 1;
    public string MarginsMode { get; set; } = "fixed";    // last | fixed
    public double DefaultMarginsMm { get; set; }          // 0 = no margin

    // What the last print actually used, for the "last" modes.
    public int LastCopies { get; set; } = 1;
    public string LastLayout { get; set; } = "portrait";
    public int LastPerPage { get; set; } = 1;
    public double LastMarginsMm { get; set; }
    // Unit the margin box is shown in: "mm" or "cm". Purely a display choice.
    public string MarginsUnit { get; set; } = "mm";

    // Which way a converted length goes when it lands exactly between two whole
    // dots. It only ever matters for a barcode module, where the half is
    // multiplied by every module of the symbol - and a supplier states a MINIMUM
    // module width, so going up stays within it and going down can fall under.
    public bool TransformRoundUp { get; set; } = true;

    // Skips the print dialog and prints straight away with the defaults. Only
    // meaningful while all three settings above are on "fixed".
    public bool QuickPrint { get; set; }

    // How each printer is driven: "raw" hands it the ZPL untouched (a label
    // printer speaks it natively), "image" prints the rendered label through
    // Windows. Keyed by printer name; absent means "work it out from the driver".
    public Dictionary<string, string> PrinterSendModes { get; set; } = new();
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool UseSystemAccent { get; set; } = true;
    public string CustomAccent { get; set; } = "#0078D4";
    public string Language { get; set; } = "fr";
    public bool ShowLineNumbers { get; set; } = true;
    public bool ShowPreviewGrid { get; set; } = true;
    // Thickness, in screen pixels, of the selection frame drawn in edit mode (1..10).
    public int InspectFrameThickness { get; set; } = 2;

    // ---- View / edit mode -------------------------------------------------
    // Which mode a document opens in: 0 = view (default), 1 = edit,
    // 2 = whichever mode was in use when the application last closed. Someone who
    // only ever writes labels should not have to flip the switch every morning.
    public int StartMode { get; set; }
    // Remembered for StartMode == 2 only; written whenever the mode changes.
    public bool LastModeEdit { get; set; }

    // ---- The floating plates over the preview -----------------------------
    // Where each plate sits, and which way it is stacked. An anchor is one of
    // the eight edge/corner positions ("topLeft" … "bottomRight"); "free" means
    // the plate keeps the exact spot it was dragged to (X/Y, in dips from the
    // preview's top-left corner, -1 until it has been dragged once). Locked
    // hides the drag grips without giving the free position up.
    public string ModePlateAnchor { get; set; } = "topRight";
    public bool ModePlateFree { get; set; }
    public double ModePlateX { get; set; } = -1;
    public double ModePlateY { get; set; } = -1;
    public bool ModePlateLocked { get; set; }
    public bool ModePlateHorizontal { get; set; }

    public string ToolPlateAnchor { get; set; } = "topLeft";
    public bool ToolPlateFree { get; set; }
    public double ToolPlateX { get; set; } = -1;
    public double ToolPlateY { get; set; } = -1;
    public bool ToolPlateLocked { get; set; }
    public bool ToolPlateHorizontal { get; set; }

    // Which of the two comes first when both are pinned to the SAME place: they
    // stand one above the other rather than on top of each other, and this says
    // which one is above. The mode switch, by default.
    public bool ModePlateFirst { get; set; } = true;

    // Which side of the selected element its strip of tools prefers. The other
    // side is still used when there is no room on this one.
    public bool ElementPlateAbove { get; set; }
    // Grid colour: default (faint, theme-based) or a custom ARGB (#AARRGGBB).
    public bool UseCustomGridColor { get; set; }
    public string CustomGridColor { get; set; } = "#40808080";
    public bool SkipPrinterInstallPrompt { get; set; }

    // PNG export quality. Mode "ask" (default) pops the quality dialog on each
    // export; "default" silently uses PngQualityStep. Step 1..5 maps to a linear
    // resolution factor: 1=÷2, 2=÷1.5, 3=original (default), 4=×1.5, 5=×2.
    public string PngExportMode { get; set; } = "ask";   // "ask" | "default"
    public int PngQualityStep { get; set; } = 3;

    // Offer, at startup, to make Ultimate ZPL Viewer the default handler for .zpl.
    public bool AskZplAssociation { get; set; } = true;

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

    // ---- The home page ----------------------------------------------------
    // What closing the LAST tab of a window does: 0 = close the window (and with
    // it the app, when it was the only one), 1 = fall back to the home page. The
    // same choice decides whether a launch with nothing to open lands on the home
    // page or straight on a document: someone who never wants the home page does
    // not want it at startup either.
    public int LastTabClosed { get; set; } = 1;

    // Which label the home page's "sample" button opens: 0 = the one shipped with
    // the app, 1 = SampleLabelZpl below.
    public int SampleLabelMode { get; set; }
    public string SampleLabelZpl { get; set; } = string.Empty;

    // The label the sample button opens unless the user wrote their own: every
    // symbology the renderer draws, one per tile, each named on a reversed tab —
    // what the application can do, shown rather than told. Kept here rather than
    // in the page so the settings screen can show it, and so there is one copy.
    public const string DefaultSampleZpl = """
        ^XA
        ^CI28
        ^PW812
        ^LL1218

        ^FX ===== En-tete =====
        ^FO0,0^GB812,132,132^FS
        ^FO24,20^FR^A0N,62,62^FDULTIMATE ZPL VIEWER^FS
        ^FO24,88^FR^A0N,26,26^FD15 symbologies  -  rendu 100 % local, aucune API externe^FS
        ^FO690,22^FR^GC92,46^FS
        ^FO690,50^A0N,38,38^FB92,1,0,C^FDZPL^FS

        ^FX ===== Grille =====
        ^FO20,146^GB772,1022,3^FS
        ^FO406,146^GB3,768,3^FS
        ^FO20,274^GB772,3,3^FS
        ^FO20,402^GB772,3,3^FS
        ^FO20,530^GB772,3,3^FS
        ^FO20,658^GB772,3,3^FS
        ^FO20,786^GB772,3,3^FS
        ^FO20,914^GB772,3,3^FS
        ^FO276,914^GB3,254,3^FS
        ^FO534,914^GB3,254,3^FS

        ^FX ----- Ligne 1 -----
        ^FO32,158^GB150,26,26^FS
        ^FO32,161^FR^A0N,22,22^FB150,1,0,C^FDCODE 128^FS
        ^FO40,192^BY2^BCN,56,Y,N,N^FDZPL-VIEWER^FS
        ^FO418,158^GB150,26,26^FS
        ^FO418,161^FR^A0N,22,22^FB150,1,0,C^FDGS1-128^FS
        ^FO426,192^BY2^BCN,56,N,N,N^FD>;>80103761234567890^FS
        ^FO426,250^A0N,20,20^FD(01) 03761234567890^FS

        ^FX ----- Ligne 2 -----
        ^FO32,286^GB150,26,26^FS
        ^FO32,289^FR^A0N,22,22^FB150,1,0,C^FDEAN-13^FS
        ^FO52,320^BY2^BEN,56,Y,N^FD376123456789^FS
        ^FO418,286^GB150,26,26^FS
        ^FO418,289^FR^A0N,22,22^FB150,1,0,C^FDUPC-A^FS
        ^FO438,320^BY2^BUN,56,Y,N^FD03600029145^FS

        ^FX ----- Ligne 3 -----
        ^FO32,414^GB150,26,26^FS
        ^FO32,417^FR^A0N,22,22^FB150,1,0,C^FDEAN-8^FS
        ^FO52,448^BY2^B8N,56,Y,N^FD5512345^FS
        ^FO418,414^GB150,26,26^FS
        ^FO418,417^FR^A0N,22,22^FB150,1,0,C^FDUPC-E^FS
        ^FO438,448^BY2^B9N,56,Y,N^FD0425261^FS

        ^FX ----- Ligne 4 -----
        ^FO32,542^GB150,26,26^FS
        ^FO32,545^FR^A0N,22,22^FB150,1,0,C^FDCODE 39^FS
        ^FO40,576^BY2,3^B3N,N,56,Y,N^FDZPL-39^FS
        ^FO418,542^GB150,26,26^FS
        ^FO418,545^FR^A0N,22,22^FB150,1,0,C^FDCODE 93^FS
        ^FO426,576^BY2^BAN,56,Y,N^FDZPL-93^FS

        ^FX ----- Ligne 5 -----
        ^FO32,670^GB170,26,26^FS
        ^FO32,673^FR^A0N,22,22^FB170,1,0,C^FD2/5 ENTRELACÉ^FS
        ^FO40,704^BY2,3^B2N,56,Y,N^FD12345678^FS
        ^FO418,670^GB150,26,26^FS
        ^FO418,673^FR^A0N,22,22^FB150,1,0,C^FDCODABAR^FS
        ^FO426,704^BY2,3^BKN,N,56,Y,N,A,B^FD40156^FS

        ^FX ----- Ligne 6 -----
        ^FO32,798^GB150,26,26^FS
        ^FO32,801^FR^A0N,22,22^FB150,1,0,C^FDPOSTNET^FS
        ^FO40,846^BY2^BZN,40,Y,N^FD75011^FS
        ^FO418,798^GB150,26,26^FS
        ^FO418,801^FR^A0N,22,22^FB150,1,0,C^FDPDF417^FS
        ^FO426,834^BY2^B7N,4,3,4^FDUltimate ZPL Viewer - PDF417^FS

        ^FX ----- Ligne 7 : codes 2D -----
        ^FO32,926^GB150,26,26^FS
        ^FO32,929^FR^A0N,22,22^FB150,1,0,C^FDQR CODE^FS
        ^FO88,966^BQN,2,4^FDMA,https://github.com/NzoSifou/ultimate-zpl-viewer^FS
        ^FO290,926^GB150,26,26^FS
        ^FO290,929^FR^A0N,22,22^FB150,1,0,C^FDAZTEC^FS
        ^FO330,970^BON,6,N,0,N,1,^FDULTIMATE ZPL VIEWER - AZTEC^FS
        ^FO548,926^GB150,26,26^FS
        ^FO548,929^FR^A0N,22,22^FB150,1,0,C^FDDATA MATRIX^FS
        ^FO590,970^BXN,7,200^FDUltimate ZPL Viewer^FS

        ^FX ----- Usages -----
        ^FO224,161^A0N,20,20^FB170,1,0,R^FDLogistique^FS
        ^FO610,161^A0N,20,20^FB170,1,0,R^FDTraçabilité GS1^FS
        ^FO224,289^A0N,20,20^FB170,1,0,R^FDCommerce (Europe)^FS
        ^FO610,289^A0N,20,20^FB170,1,0,R^FDCommerce (USA)^FS
        ^FO224,417^A0N,20,20^FB170,1,0,R^FDPetits emballages^FS
        ^FO610,417^A0N,20,20^FB170,1,0,R^FDUPC compact^FS
        ^FO224,545^A0N,20,20^FB170,1,0,R^FDIndustrie, défense^FS
        ^FO610,545^A0N,20,20^FB170,1,0,R^FDCode 39 dense^FS
        ^FO224,673^A0N,20,20^FB170,1,0,R^FDCartons, palettes^FS
        ^FO610,673^A0N,20,20^FB170,1,0,R^FDSanté, bibliothèques^FS
        ^FO224,801^A0N,20,20^FB170,1,0,R^FDCourrier (USPS)^FS
        ^FO610,801^A0N,20,20^FB170,1,0,R^FDDocuments, billets^FS
        ^FO20,1136^A0N,20,20^FB258,1,0,C^FDLiens, mobile^FS
        ^FO278,1136^A0N,20,20^FB258,1,0,C^FDBillets de transport^FS
        ^FO536,1136^A0N,20,20^FB258,1,0,C^FDMarquage de pièces^FS

        ^FX ===== Pied =====
        ^FO0,1180^GB812,38,38^FS
        ^FO0,1187^FR^A0N,24,24^FB812,1,0,C^FDÉtiquette d'exemple  -  tous les codes sont dessinés par Ultimate ZPL Viewer^FS
        ^XZ
        """;

    /// <summary>The ZPL the sample button opens, falling back to the shipped one.</summary>
    public string SampleLabelText()
    {
        var custom = SampleLabelZpl;
        var text = SampleLabelMode == 1 && !string.IsNullOrWhiteSpace(custom) ? custom : DefaultSampleZpl;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // General
    public bool ReopenLastFile { get; set; }
    public string LastFilePath { get; set; } = string.Empty;
    // Most-recently-opened files, newest first (capped at RecentFilesMax).
    public List<string> RecentFiles { get; set; } = new();
    // Saved documents open at the last graceful exit (tab order); used by
    // ReopenLastFile to restore the whole tab set. Kept for the single-window
    // sessions written by earlier versions — WindowSessions supersedes it.
    public List<string> OpenFiles { get; set; } = new();

    // One entry per window at the last graceful exit, each holding that window's
    // documents in tab order, so the whole arrangement comes back.
    public List<List<string>> WindowSessions { get; set; } = new();

    // Where a document opens when the app is ALREADY running. "tab" adds it to the
    // active window, "window" gives it a window of its own.
    public string OpenFromExplorer { get; set; } = "tab";   // double-click, "Open with"
    public string OpenFromToolbar { get; set; } = "tab";    // the "Open a file" button
    // Starting the app with no file while a window is already open: "window" opens
    // an empty one, "focus" just brings the existing window to the front.
    public string LaunchWithoutFile { get; set; } = "window";
    public bool ShowFilePathInTitle { get; set; } = true;
    // Show the full path in parentheses in a tab's hover tooltip.
    public bool ShowPathInTabTooltip { get; set; } = true;
    // Show the size/dpi/zoom caption at the bottom-right of the preview.
    public bool ShowPreviewCaption { get; set; } = true;

    // Updates
    // Looks at the project's GitHub releases at startup. The only network call the
    // application makes; switching it off leaves the manual button in "À propos".
    public bool CheckUpdatesOnStartup { get; set; } = true;
    // A release the user chose to ignore — its asset hash, or its name when the
    // release publishes no hash. Cleared as soon as a newer one appears.
    public string SkippedUpdate { get; set; } = string.Empty;
    public string LastUpdateCheck { get; set; } = string.Empty;

    // Layout
    public bool SwapEditorPreview { get; set; }
    // Toolbar + editor visibility (title-bar / collapse-handle toggles); persist
    // across sessions (unless the app was launched with a --hide override).
    public bool ToolbarVisible { get; set; } = true;
    public bool EditorVisible { get; set; } = true;
    // Where the user left the editor/preview splitter, in pixels. Stored as a width
    // rather than a share of the window because that is what the drag manipulates:
    // a window resized during the session leaves the editor where it was put, and
    // reopening the app the same size puts it back exactly there.
    public double EditorWidth { get; set; } = 420;
    // Toolbar layout: three rows of slots, a slot being one button or a named
    // group of them. Each row still wraps automatically on narrow windows.
    //
    // Null until somebody arranges one, which is also how a layout written by a
    // version that knew nothing of groups is handled: the key it used is not this
    // one, so the default - three named groups - is what comes up.
    public List<List<ToolbarSlot>>? ToolbarLayout { get; set; }

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
