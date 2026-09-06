using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// ── Little pictures of what you are about to place ──────────────────────────
// A list of names tells you nothing about what a Data Matrix looks like next to
// an Aztec. Each entry in the insertion menus carries a miniature instead.
//
// They are DRAWN, not fetched: the application renders labels offline and has no
// business reaching out for artwork. Black on a white plate, because that is what
// a symbol on a label looks like — and it reads the same in either theme.
internal static class SymbolPreview
{
    private const double PlateW = 62;
    private const double PlateH = 38;
    private const double Pad = 5;

    /// <summary>A miniature of one symbology, on its own little label.</summary>
    public static FrameworkElement Barcode(string key)
    {
        var canvas = new Canvas { Width = PlateW, Height = PlateH };
        double w = PlateW - 2 * Pad, h = PlateH - 2 * Pad;

        switch (key)
        {
            case "qr": DrawQr(canvas, w, h); break;
            case "datamatrix": DrawDataMatrix(canvas, w, h); break;
            case "aztec": DrawAztec(canvas, w, h); break;
            case "pdf417": DrawStacked(canvas, w, h); break;
            case "ean13":
            case "ean8":
            case "upca": DrawEan(canvas, w, h); break;
            default: DrawLinear(canvas, w, h); break;
        }
        return Plate(canvas);
    }

    /// <summary>A miniature of one shape tool.</summary>
    public static FrameworkElement Shape(string key)
    {
        var canvas = new Canvas { Width = PlateW, Height = PlateH };
        double w = PlateW - 2 * Pad, h = PlateH - 2 * Pad;
        var ink = new SolidColorBrush(Colors.Black);

        switch (key)
        {
            case "line":
                Put(canvas, new Rectangle { Width = w, Height = 2.5, Fill = ink },
                    Pad, Pad + h / 2 - 1.25);
                break;
            case "ellipse":
                Put(canvas, new Ellipse { Width = w, Height = h * 0.72, Stroke = ink, StrokeThickness = 2 },
                    Pad, Pad + h * 0.14);
                break;
            case "circle":
                Put(canvas, new Ellipse { Width = h, Height = h, Stroke = ink, StrokeThickness = 2 },
                    Pad + (w - h) / 2, Pad);
                break;
            default:
                Put(canvas, new Rectangle { Width = w, Height = h * 0.72, Stroke = ink, StrokeThickness = 2 },
                    Pad, Pad + h * 0.14);
                break;
        }
        return Plate(canvas);
    }

    /// <summary>A solid block, for the fill tool.</summary>
    public static FrameworkElement Fill()
    {
        var canvas = new Canvas { Width = PlateW, Height = PlateH };
        double w = PlateW - 2 * Pad, h = PlateH - 2 * Pad;
        Put(canvas, new Rectangle { Width = w, Height = h * 0.72, Fill = new SolidColorBrush(Colors.Black) },
            Pad, Pad + h * 0.14);
        return Plate(canvas);
    }

    // ── The drawings ────────────────────────────────────────────────────────

    // Bar widths, as multiples of the narrow module. Not any real encoding — a
    // barcode's job here is to be recognised as one.
    private static readonly int[] LinearPattern = { 1, 1, 2, 1, 1, 3, 1, 2, 1, 1, 1, 2, 3, 1, 1, 2, 1, 1 };

    private static void DrawLinear(Canvas canvas, double w, double h)
    {
        DrawBars(canvas, w, h * 0.86, Pad, Pad, LinearPattern);
    }

    // EAN and UPC are the ones people recognise: guard bars running past the rest,
    // and a line of digits underneath.
    private static void DrawEan(Canvas canvas, double w, double h)
    {
        double barsH = h * 0.66;
        DrawBars(canvas, w, barsH, Pad, Pad, LinearPattern);
        var ink = new SolidColorBrush(Colors.Black);
        foreach (double x in new[] { Pad, Pad + w / 2 - 1, Pad + w - 1.5 })
            Put(canvas, new Rectangle { Width = 1.5, Height = barsH + 3, Fill = ink }, x, Pad);
        // A suggestion of the human-readable line, not real digits.
        Put(canvas, new Rectangle { Width = w * 0.34, Height = 3, Fill = ink },
            Pad + w * 0.06, Pad + barsH + 5);
        Put(canvas, new Rectangle { Width = w * 0.34, Height = 3, Fill = ink },
            Pad + w * 0.58, Pad + barsH + 5);
    }

    private static void DrawBars(Canvas canvas, double w, double h, double x0, double y0, int[] pattern)
    {
        int units = pattern.Sum() + pattern.Length;      // bars plus a gap after each
        double unit = w / units;
        double x = x0;
        var ink = new SolidColorBrush(Colors.Black);
        foreach (int width in pattern)
        {
            Put(canvas, new Rectangle { Width = unit * width, Height = h, Fill = ink }, x, y0);
            x += unit * (width + 1);
        }
    }

    // Three finder squares and some scatter: the shape everyone reads as "QR".
    private static void DrawQr(Canvas canvas, double w, double h)
    {
        double side = Math.Min(w, h);
        double x0 = Pad + (w - side) / 2, y0 = Pad + (h - side) / 2;
        double m = side / 9;                              // one module
        var ink = new SolidColorBrush(Colors.Black);

        void Finder(double fx, double fy)
        {
            Put(canvas, new Rectangle { Width = m * 3, Height = m * 3, Stroke = ink, StrokeThickness = m * 0.8 },
                fx, fy);
            Put(canvas, new Rectangle { Width = m, Height = m, Fill = ink }, fx + m, fy + m);
        }
        Finder(x0, y0);
        Finder(x0 + side - m * 3, y0);
        Finder(x0, y0 + side - m * 3);

        // A handful of data modules in the free quarter.
        foreach (var (cx, cy) in new[] { (5, 5), (7, 5), (6, 6), (5, 7), (7, 7), (4, 6), (6, 4) })
            Put(canvas, new Rectangle { Width = m, Height = m, Fill = ink }, x0 + cx * m, y0 + cy * m);
    }

    // The L-shaped solid finder down two sides, dashes on the other two.
    private static void DrawDataMatrix(Canvas canvas, double w, double h)
    {
        double side = Math.Min(w, h);
        double x0 = Pad + (w - side) / 2, y0 = Pad + (h - side) / 2;
        double m = side / 8;
        var ink = new SolidColorBrush(Colors.Black);

        Put(canvas, new Rectangle { Width = m, Height = side, Fill = ink }, x0, y0);
        Put(canvas, new Rectangle { Width = side, Height = m, Fill = ink }, x0, y0 + side - m);
        for (int i = 0; i < 8; i += 2)
        {
            Put(canvas, new Rectangle { Width = m, Height = m, Fill = ink }, x0 + i * m, y0);
            Put(canvas, new Rectangle { Width = m, Height = m, Fill = ink }, x0 + side - m, y0 + i * m);
        }
        foreach (var (cx, cy) in new[] { (2, 2), (4, 2), (3, 3), (5, 4), (2, 5), (4, 5), (6, 3) })
            Put(canvas, new Rectangle { Width = m, Height = m, Fill = ink }, x0 + cx * m, y0 + cy * m);
    }

    // Concentric rings around a centre: the Aztec bullseye.
    private static void DrawAztec(Canvas canvas, double w, double h)
    {
        double side = Math.Min(w, h);
        double x0 = Pad + (w - side) / 2, y0 = Pad + (h - side) / 2;
        double m = side / 9;
        var ink = new SolidColorBrush(Colors.Black);

        for (int ring = 0; ring <= 2; ring++)
        {
            double inset = ring * 2 * m;
            Put(canvas, new Rectangle
            {
                Width = side - 2 * inset,
                Height = side - 2 * inset,
                Stroke = ink,
                StrokeThickness = m,
                Fill = null,
            }, x0 + inset, y0 + inset);
        }
        Put(canvas, new Rectangle { Width = m, Height = m, Fill = ink },
            x0 + side / 2 - m / 2, y0 + side / 2 - m / 2);
    }

    // Rows of short bars, which is what a PDF417 looks like from a distance.
    private static void DrawStacked(Canvas canvas, double w, double h)
    {
        int[][] rows =
        {
            new[] { 1, 2, 1, 1, 3, 1, 2 },
            new[] { 2, 1, 3, 1, 1, 2, 1 },
            new[] { 1, 3, 1, 2, 1, 1, 2 },
            new[] { 2, 1, 1, 3, 2, 1, 1 },
        };
        double rowH = (h - (rows.Length - 1) * 1.5) / rows.Length;
        double y = Pad;
        foreach (var row in rows)
        {
            DrawBars(canvas, w, rowH, Pad, y, row);
            y += rowH + 1.5;
        }
    }

    // ── Plumbing ────────────────────────────────────────────────────────────

    private static void Put(Canvas canvas, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    // The white card the symbol sits on, so black ink reads in either theme.
    private static Border Plate(Canvas content) => new()
    {
        Width = PlateW,
        Height = PlateH,
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Colors.White),
        Child = content,
    };
}
