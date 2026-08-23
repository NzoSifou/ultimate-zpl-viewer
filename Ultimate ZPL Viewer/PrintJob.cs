using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using Microsoft.Win32;

namespace Ultimate_ZPL_Viewer;

/// <summary>How the label reaches the printer.</summary>
public enum SendMode
{
    /// <summary>The ZPL text itself, which a label printer interprets natively.</summary>
    Raw,
    /// <summary>The rendered label, printed as a page through Windows.</summary>
    Image,
}

public enum PrintLayout { Portrait, Landscape, PortraitFlipped, LandscapeFlipped }

/// <summary>Everything one press of "Imprimer" needs to know.</summary>
public sealed record PrintJob(
    string Printer,
    SendMode Mode,
    int Copies,
    PrintLayout Layout,
    // Both only mean something on a classic printer: a thermal printer is fed the
    // ZPL and picks its own media. PaperSize is a Windows paper name, empty for
    // "whatever the printer defaults to".
    string PaperSize,
    double MarginsMm);

// Printing, and the small amount of knowledge about printers the dialog needs.
//
// Two roads lead out of here, because the two kinds of printer want opposite
// things. A label printer speaks ZPL: handing it anything else throws away the
// fidelity that is the whole point of this application. Every other printer has
// never heard of ZPL and needs a page of pixels.
public static class PrintJobService
{
    // ── Which road a printer takes ───────────────────────────────────────────

    // Windows does not say "this is a label printer", so the driver name is the
    // only signal available. It is a guess, which is why the choice is offered in
    // the print dialog and remembered per printer once the user has made it.
    private static readonly string[] RawDriverHints =
    {
        "zdesigner", "zebra", "zpl", "epl", "godex", "tsc ", "tsc_", "datamax",
        "intermec", "honeywell", "sato", "toshiba tec", "bixolon", "citizen",
        "argox", "brother ql", "dymo", "generic / text only",
    };

    public static string? DriverOf(string printer)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Print\Printers\" + printer);
            return key?.GetValue("Printer Driver") as string;
        }
        catch { return null; }
    }

    /// <summary>The road this printer takes unless the user has said otherwise.</summary>
    public static SendMode DetectMode(string printer)
    {
        var driver = (DriverOf(printer) ?? "").ToLowerInvariant();
        var name = (printer ?? "").ToLowerInvariant();
        bool raw = RawDriverHints.Any(h => driver.Contains(h) || name.Contains(h));
        return raw ? SendMode.Raw : SendMode.Image;
    }

    /// <summary>The remembered choice for this printer, or the detected one.</summary>
    public static SendMode ModeFor(AppSettings settings, string printer)
    {
        if (settings.PrinterSendModes.TryGetValue(printer, out var saved))
            return saved == "raw" ? SendMode.Raw : SendMode.Image;
        return DetectMode(printer);
    }

    public static void RememberMode(AppSettings settings, string printer, SendMode mode)
    {
        settings.PrinterSendModes[printer] = mode == SendMode.Raw ? "raw" : "image";
        settings.Save();
    }

    // ── Raw ZPL ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The ZPL to send, with the job's copies and layout folded in.
    /// <para>
    /// Only what the language can actually express is applied. ^PQ sets the copy
    /// count and ^POI turns the label upside-down; there is NO command that turns a
    /// label sideways or rescales it, so a landscape or scaled job would mean
    /// rewriting every coordinate in the file. The dialog therefore does not offer
    /// those on this road, rather than silently ignoring them here.
    /// </para>
    /// </summary>
    public static string BuildRawZpl(string zpl, PrintJob job)
    {
        var text = zpl ?? string.Empty;

        if (job.Layout is PrintLayout.PortraitFlipped or PrintLayout.LandscapeFlipped)
            text = InsertAfterFormatStart(text, "^POI");

        if (job.Copies > 1)
            text = InsertBeforeFormatEnd(text, "^PQ" + job.Copies);

        return text;
    }

    private static int LastIndexOfCommand(string text, string cmd)
        => text.LastIndexOf(cmd, StringComparison.OrdinalIgnoreCase);

    private static string InsertAfterFormatStart(string text, string insert)
    {
        int i = text.IndexOf("^XA", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? insert + text : text.Insert(i + 3, "\n" + insert);
    }

    private static string InsertBeforeFormatEnd(string text, string insert)
    {
        int i = LastIndexOfCommand(text, "^XZ");
        return i < 0 ? text + "\n" + insert : text.Insert(i, insert + "\n");
    }

    // ── Rendered page ────────────────────────────────────────────────────────

    /// <summary>
    /// Prints a rendered label as a page. <paramref name="widthMm"/>/<paramref name="heightMm"/>
    /// are the label's real size: it comes out at that size, centred, and is only
    /// ever shrunk - never stretched - if the margins leave it too little room.
    /// </summary>
    public static void PrintImage(PrintJob job, RenderSnapshot snapshot, double widthMm, double heightMm)
    {
        using var bitmap = ToBitmap(snapshot);
        Rotate(bitmap, job.Layout);

        // A sideways label swaps its physical dimensions along with its pixels.
        bool sideways = job.Layout is PrintLayout.Landscape or PrintLayout.LandscapeFlipped;
        double wMm = sideways ? heightMm : widthMm;
        double hMm = sideways ? widthMm : heightMm;

        // GDI+ page units are hundredths of an inch.
        float wUnits = (float)(wMm / 25.4 * 100);
        float hUnits = (float)(hMm / 25.4 * 100);
        float marginUnits = (float)(Math.Max(0, job.MarginsMm) / 25.4 * 100);

        using var doc = new PrintDocument();
        doc.DocumentName = "Ultimate ZPL Viewer";
        doc.PrinterSettings.PrinterName = job.Printer;
        doc.PrinterSettings.Copies = (short)Math.Clamp(job.Copies, 1, short.MaxValue);
        doc.DefaultPageSettings.Landscape = sideways;
        doc.OriginAtMargins = false;
        if (!string.IsNullOrEmpty(job.PaperSize) && FindPaper(doc.PrinterSettings, job.PaperSize) is { } paper)
            doc.DefaultPageSettings.PaperSize = paper;

        doc.PrintPage += (_, e) =>
        {
            if (e.Graphics is null) return;
            var bounds = e.PageBounds;   // the sheet, not the printable area

            // The margins carve out the area the label may use. It keeps its real
            // size inside that area; only a label that no longer fits is scaled
            // down, because printing it clipped would be worse than printing it
            // slightly small.
            float availW = Math.Max(1, bounds.Width - 2 * marginUnits);
            float availH = Math.Max(1, bounds.Height - 2 * marginUnits);
            float shrink = Math.Min(1f, Math.Min(availW / wUnits, availH / hUnits));
            wUnits *= shrink; hUnits *= shrink;

            float x = (bounds.Width - wUnits) / 2f;
            float y = (bounds.Height - hUnits) / 2f;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(bitmap, new RectangleF(x, y, wUnits, hUnits));
            e.HasMorePages = false;
        };
        doc.Print();
    }

    private static PaperSize? FindPaper(PrinterSettings settings, string name)
    {
        foreach (PaperSize p in settings.PaperSizes)
            if (string.Equals(p.PaperName, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>The paper sizes this printer offers, in the order it lists them.</summary>
    public static List<(string Name, double WMm, double HMm)> PaperSizes(string printer)
    {
        var list = new List<(string, double, double)>();
        try
        {
            var settings = new PrinterSettings { PrinterName = printer };
            foreach (PaperSize p in settings.PaperSizes)
                if (p.Width > 0 && p.Height > 0)
                    list.Add((p.PaperName, p.Width * 25.4 / 100.0, p.Height * 25.4 / 100.0));
        }
        catch { /* driver not reachable - the caller falls back to the default */ }
        return list;
    }

    /// <summary>The paper the printer will use, in millimetres (portrait).</summary>
    public static (double W, double H)? PaperSizeMm(string printer, string? paperName = null)
    {
        try
        {
            var settings = new PrinterSettings { PrinterName = printer };
            var paper = (string.IsNullOrEmpty(paperName) ? null : FindPaper(settings, paperName!))
                        ?? settings.DefaultPageSettings.PaperSize;
            if (paper.Width <= 0 || paper.Height <= 0) return null;
            return (paper.Width * 25.4 / 100.0, paper.Height * 25.4 / 100.0);
        }
        catch { return null; }
    }

    private static Bitmap ToBitmap(RenderSnapshot snapshot)
    {
        var bmp = new Bitmap(snapshot.Width, snapshot.Height,
                             System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // Both sides are BGRA, but the snapshot rows are packed and the bitmap
            // rows are padded to Stride, so they go across one row at a time.
            int rowBytes = snapshot.Width * 4;
            for (int y = 0; y < snapshot.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    snapshot.BgraPixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    private static void Rotate(Bitmap bitmap, PrintLayout layout)
    {
        switch (layout)
        {
            // Landscape is the page's own orientation (DefaultPageSettings.Landscape),
            // so the pixels only need turning for the upside-down variants.
            case PrintLayout.PortraitFlipped:
            case PrintLayout.LandscapeFlipped:
                bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
        }
    }

    // ── Names ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<PrintLayout, string> LayoutKeys = new()
    {
        [PrintLayout.Portrait] = "portrait",
        [PrintLayout.Landscape] = "landscape",
        [PrintLayout.PortraitFlipped] = "portraitFlipped",
        [PrintLayout.LandscapeFlipped] = "landscapeFlipped",
    };

    public static string KeyOf(PrintLayout layout) => LayoutKeys[layout];

    public static PrintLayout LayoutFromKey(string? key)
        => LayoutKeys.FirstOrDefault(kv => kv.Value == key).Key;

    /// <summary>Localized layout name, for the dialog and the quick-print tooltip.</summary>
    public static string NameOf(PrintLayout layout)
        => LocalizationService.Get("settings.print.layout." + KeyOf(layout));
}
