using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// ── Editing the ZPL without rewriting it ────────────────────────────────────
// Dragging an element on the preview has to end up in the text, and the text is
// the user's file: comments, indentation, the commands this renderer does not
// model, the order everything is written in. So nothing here REGENERATES ZPL from
// the model. Every operation returns the smallest possible edit — a range and its
// replacement — that changes the numbers of ONE command and leaves every other
// byte of the file exactly where it was.
//
// The edit is handed to Monaco, which applies it on its own undo stack: a drag
// then undoes with Ctrl+Z like anything typed, and the two views cannot drift
// apart because there is only ever one document.
public static class ZplPatcher
{
    /// <summary>A replacement of [Start, End) by <see cref="Text"/>.</summary>
    public readonly record struct Edit(int Start, int End, string Text);

    // ZPL coordinates are whole dots, 0..32000.
    private const int MaxCoord = 32000;

    private static readonly string[] Orientations = { "N", "R", "I", "B" };

    // ── Moving ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shifts a field by (<paramref name="dx"/>, <paramref name="dy"/>) DOTS on the
    /// label. Returns null when the field has no origin of its own to move — it
    /// inherits the previous field's position, and moving it would move that one.
    /// </summary>
    /// <param name="snapDots">
    /// When &gt; 0, the resulting coordinates are rounded to this many dots.
    /// </param>
    public static Edit? Move(string zpl, int start, int end,
                             double dx, double dy, double dpmm, double snapDots = 0)
    {
        var origin = FindOrigin(zpl, start, end);
        if (origin is null) return null;

        double scale = UnitScaleAt(zpl, origin.Start, dpmm);
        if (scale <= 0) scale = 1;

        var (lead, body, trail) = SplitArgs(origin.Args);
        var parts = body.Length == 0 ? new List<string> { "", "" } : body.Split(',').ToList();
        while (parts.Count < 2) parts.Add("");

        // The delta arrives in dots; the command may be written in inches or
        // millimetres (^MU), so it goes back through the same scale the renderer
        // multiplied by.
        double x = Number(parts[0]) + dx / scale;
        double y = Number(parts[1]) + dy / scale;

        if (snapDots > 0)
        {
            double step = snapDots / scale;
            if (step > 0) { x = Math.Round(x / step) * step; y = Math.Round(y / step) * step; }
        }

        parts[0] = Coord(x);
        parts[1] = Coord(y);

        int argsStart = origin.End - origin.Args.Length;
        return new Edit(argsStart, origin.End, lead + string.Join(",", parts) + trail);
    }

    /// <summary>Whether this field can be dragged at all.</summary>
    public static bool CanMove(string zpl, int start, int end) => FindOrigin(zpl, start, end) is not null;

    /// <summary>
    /// The field's origin as written — useful for showing the user where the
    /// element sits. Null when the field has none.
    /// </summary>
    public static (double X, double Y)? Origin(string zpl, int start, int end, double dpmm)
    {
        var origin = FindOrigin(zpl, start, end);
        if (origin is null) return null;
        double scale = UnitScaleAt(zpl, origin.Start, dpmm);
        var (_, body, _) = SplitArgs(origin.Args);
        var parts = body.Split(',');
        return (Number(parts.ElementAtOrDefault(0)) * scale, Number(parts.ElementAtOrDefault(1)) * scale);
    }

    // ── Rotating ────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns a field a quarter turn clockwise. ZPL has no free rotation: a field
    /// is Normal, Rotated, Inverted or Bottom-up, so this cycles N → R → I → B.
    /// Returns null when the field carries no command that holds an orientation.
    /// </summary>
    public static Edit? Rotate(string zpl, int start, int end)
    {
        var token = FindOrientationHolder(zpl, start, end);
        if (token is null) return null;

        var (lead, body, trail) = SplitArgs(token.Args);
        var parts = body.Length == 0 ? new List<string> { "" } : body.Split(',').ToList();

        // An empty first parameter means the field follows the ^FW default, which
        // is Normal unless the file says otherwise; either way the next quarter
        // turn from what is drawn is R.
        var current = parts[0].Trim().ToUpperInvariant();
        int index = Array.IndexOf(Orientations, current);
        parts[0] = Orientations[index < 0 ? 1 : (index + 1) % 4];

        int argsStart = token.End - token.Args.Length;
        return new Edit(argsStart, token.End, lead + string.Join(",", parts) + trail);
    }

    public static bool CanRotate(string zpl, int start, int end)
        => FindOrientationHolder(zpl, start, end) is not null;

    // ── Reading the command stream ──────────────────────────────────────────

    private static IEnumerable<ZplToken> InSpan(string zpl, int start, int end)
    {
        if (zpl is null || start < 0 || end > zpl.Length || start >= end) return Enumerable.Empty<ZplToken>();
        return ZplRenderer.TokenizeForEditing(zpl).Where(t => t.Start >= start && t.End <= end);
    }

    /// <summary>The field's own ^FO / ^FT, which is where its position lives.</summary>
    private static ZplToken? FindOrigin(string zpl, int start, int end)
        => InSpan(zpl, start, end).FirstOrDefault(t => t.Command is "FO" or "FT");

    /// <summary>
    /// The command whose first parameter is the field's orientation. A barcode
    /// selector wins over the font: a barcode field usually carries BOTH (the ^A
    /// dresses its human-readable line), and it is the symbol that turns.
    /// </summary>
    private static ZplToken? FindOrientationHolder(string zpl, int start, int end)
    {
        var tokens = InSpan(zpl, start, end).ToList();
        // ^BY is the bar-width setting, not a symbology, and has no orientation.
        var barcode = tokens.FirstOrDefault(
            t => t.Command.Length == 2 && t.Command[0] == 'B' && t.Command != "BY");
        if (barcode is not null) return barcode;
        // ^A0N, ^AAN… A scalable ^A@ splits differently (the designator lands in
        // the arguments) and is left alone rather than mangled.
        return tokens.FirstOrDefault(
            t => t.Command.Length == 2 && t.Command[0] == 'A' && t.Command[1] != '@');
    }

    /// <summary>
    /// Dots per unit of the coordinates at that point in the file. ^MU switches
    /// the whole stream that follows to inches or millimetres, so a command's
    /// numbers only mean dots until someone says otherwise.
    /// </summary>
    private static double UnitScaleAt(string zpl, int offset, double dpmm)
    {
        double scale = 1;
        foreach (var t in ZplRenderer.TokenizeForEditing(zpl))
        {
            if (t.Start >= offset) break;
            if (t.Command != "MU") continue;
            var mu = t.Args.Trim().ToUpperInvariant().FirstOrDefault();
            scale = mu switch { 'I' => dpmm * 25.4, 'M' => dpmm, _ => 1d };
        }
        return scale;
    }

    // ── Argument surgery ────────────────────────────────────────────────────

    // A token's arguments run up to the next ^, so they carry whatever whitespace
    // separated this command from the next one — a line break, usually. That has
    // to survive the edit, or every drag would reflow the file.
    private static (string Lead, string Body, string Trail) SplitArgs(string args)
    {
        int a = 0, b = args.Length;
        while (a < b && char.IsWhiteSpace(args[a])) a++;
        while (b > a && char.IsWhiteSpace(args[b - 1])) b--;
        return (args[..a], args[a..b], args[b..]);
    }

    private static double Number(string? part)
        => double.TryParse((part ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : 0;

    private static string Coord(double value)
        => ((int)Math.Round(Math.Clamp(value, 0, MaxCoord))).ToString(CultureInfo.InvariantCulture);
}
