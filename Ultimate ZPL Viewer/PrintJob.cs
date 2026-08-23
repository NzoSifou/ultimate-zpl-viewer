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
    // These three only mean something on a classic printer: a thermal printer is
    // fed the ZPL and picks its own media. PaperSize is a Windows paper name, empty
    // for "whatever the printer defaults to"; PerPage repeats the label that many
    // times on one sheet.
    string PaperSize,
    double MarginsMm,
    int PerPage);

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
    /// Prints a rendered label as a page. The label FILLS the area the margins
    /// leave, centred, keeping its proportions - a landscape label on a portrait
    /// sheet spans the full width and takes whatever height that implies. It is
    /// never distorted; the margins are the way to give it less room.
    /// <para>
    /// With PerPage above 1 that area is split into as many equal cells, each
    /// holding one copy. The split follows the layout: a portrait page divides into
    /// rows, a landscape one into columns, so a cell keeps roughly the shape of the
    /// page it came from.
    /// </para>
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

            // The margins carve out the area the label may use.
            float availW = Math.Max(1, bounds.Width - 2 * marginUnits);
            float availH = Math.Max(1, bounds.Height - 2 * marginUnits);

            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            foreach (var cell in Cells(marginUnits, marginUnits, availW, availH, job, wUnits, hUnits))
            {
                // The label takes all of its cell: whichever dimension runs out
                // first decides the factor, so proportions hold and nothing is
                // stretched.
                float fill = Math.Min(cell.Width / wUnits, cell.Height / hUnits);
                float w = wUnits * fill, h = hUnits * fill;
                e.Graphics.DrawImage(bitmap, new RectangleF(
                    cell.X + (cell.Width - w) / 2f,
                    cell.Y + (cell.Height - h) / 2f, w, h));
            }
            e.HasMorePages = false;
        };
        doc.Print();
    }

    /// <summary>
    /// The area split into one cell per copy.
    /// <para>
    /// The split follows the layout: a portrait page divides into bands, a landscape
    /// one into columns. But a band that is far wider than the label it holds wastes
    /// the page - so when the room left beside a copy is as wide as the copy itself,
    /// a second one fits there, and the other axis is divided too. That repeats
    /// while it stays true, which is what turns eight labels on an A4 into two
    /// columns of four rather than eight thin strips.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RectangleF> Cells(float x, float y, float width, float height,
                                                  PrintJob job, float labelW, float labelH)
    {
        int n = Math.Max(1, job.PerPage);
        bool portrait = job.Layout is PrintLayout.Portrait or PrintLayout.PortraitFlipped;

        int cols = portrait ? 1 : n;
        int rows = portrait ? n : 1;

        for (int guard = 0; guard < n; guard++)
        {
            float cellW = width / cols, cellH = height / rows;
            float fit = Math.Min(cellW / Math.Max(0.01f, labelW), cellH / Math.Max(0.01f, labelH));
            float drawnW = labelW * fit, drawnH = labelH * fit;

            // The leftover is the whole gap across the cell - both sides together,
            // which is how the rule was framed.
            if (portrait)
            {
                if (cols >= n || cellW - drawnW < drawnW) break;
                cols++;
                rows = (int)Math.Ceiling(n / (double)cols);
            }
            else
            {
                if (rows >= n || cellH - drawnH < drawnH) break;
                rows++;
                cols = (int)Math.Ceiling(n / (double)rows);
            }
        }

        var cells = new List<RectangleF>(n);
        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            cells.Add(new RectangleF(x + width * c / cols, y + height * r / rows,
                                     width / cols, height / rows));
        }
        return cells;
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
