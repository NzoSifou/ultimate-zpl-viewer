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
        // A token belongs to the field it STARTS in. Its arguments run to the next
        // command — the line break included — so the last command of a field ends
        // past the field's own span once the trailing whitespace is trimmed off it,
        // and asking for the whole token to fit dropped it. That is what hid a
        // graphic's ^GF from the properties when no ^FS followed it.
        return ZplRenderer.TokenizeForEditing(zpl).Where(t => t.Start >= start && t.Start < end);
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

    // ── Reading a field's properties ────────────────────────────────────────

    /// <summary>
    /// What the selected field is made of, as far as it can be changed from the
    /// properties bar. Every value is null when the field does not carry it.
    /// </summary>
    public sealed record FieldFacts(
        string? Data,           // the ^FD payload
        int DataStart,          // where that payload sits, -1 when absent
        int DataEnd,
        string? FontName,       // "0", "A"… from ^A
        double? FontHeight,
        double? FontWidth,
        string? Barcode,        // the symbology command: "BC", "BQ", …
        double? BarcodeHeight,
        bool? HumanReadable,
        double? ModuleWidth,    // ^BY
        bool Reverse,           // ^FR
        string? Shape,          // "GB", "GE", "GC", "GD"
        double[]? ShapeArgs,
        string? Graphic = null, // the ^GF payload, arguments included
        int GraphicStart = -1,  // the whole ^GF command, caret included
        int GraphicEnd = -1);

    /// <summary>
    /// The bar width and the fallback bar height in force at a point in the label.
    /// ^BY is MODAL: set once, it governs every barcode after it until the next one,
    /// and it is very often written outside the field it applies to — so a field's
    /// real bar width, and the height it falls back on when its own command leaves
    /// that out, are frequently written nowhere inside it.
    /// </summary>
    public static (double Width, double Height) BarDefaults(string zpl, int before)
    {
        double width = 2, height = 10;              // what a printer starts from
        foreach (var token in ZplRenderer.TokenizeForEditing(zpl ?? ""))
        {
            if (token.Start >= before) break;
            if (token.Command != "BY") continue;
            // Split by POSITION, not by "the numbers in it": an empty ratio would
            // otherwise shift the height along one place (^BY3,,150).
            var parts = token.Args.Split(',');
            if (parts.Length > 0 && double.TryParse(parts[0].Trim(), NumberStyles.Float,
                                                   CultureInfo.InvariantCulture, out var w) && w > 0)
                width = w;
            if (parts.Length > 2 && double.TryParse(parts[2].Trim(), NumberStyles.Float,
                                                   CultureInfo.InvariantCulture, out var h) && h > 0)
                height = h;
        }
        return (width, height);
    }

    public static FieldFacts Read(string zpl, int start, int end)
    {
        var tokens = InSpan(zpl, start, end).ToList();

        var fd = tokens.FirstOrDefault(t => t.Command is "FD" or "FV");
        var font = tokens.FirstOrDefault(t => t.Command.Length == 2 && t.Command[0] == 'A' && t.Command[1] != '@');
        var code = tokens.FirstOrDefault(t => t.Command.Length == 2 && t.Command[0] == 'B' && t.Command != "BY");
        var by = tokens.FirstOrDefault(t => t.Command == "BY");
        var shape = tokens.FirstOrDefault(t => t.Command is "GB" or "GE" or "GC" or "GD");
        var graphic = tokens.FirstOrDefault(t => t.Command is "GF" or "GFA");

        string? fontName = null; double? fontH = null, fontW = null;
        if (font is not null)
        {
            fontName = font.Command[1].ToString();
            // ^A0N,28,28 — the first argument is the ORIENTATION, not a number.
            // Reading the list as numbers turned that letter into a zero and made
            // the height read as the width.
            var parts = Parts(font.Args);
            if (parts.Count > 1) fontH = Number(parts[1]);
            if (parts.Count > 2 && Number(parts[2]) > 0) fontW = Number(parts[2]);
        }

        // Everything the running ^BY carries into this field, read up to the
        // barcode command itself — a ^BY after it belongs to the NEXT barcode.
        var bars = BarDefaults(zpl, code?.Start ?? end);

        double? codeH = null; bool? hrt = null;
        if (code is not null)
        {
            var parts = Parts(code.Args);
            int hi = HeightIndex(code.Command);
            if (hi < 0) hi = MagnificationIndex(code.Command);
            if (hi >= 0 && hi < parts.Count && parts[hi].Trim().Length > 0)
                codeH = Number(parts[hi]);
            // ^BCN,,N,N — the height left out. It is not "nothing": it is whatever
            // ^BY is carrying, which is what the printer will use and what the
            // preview already draws.
            if (codeH is null && HeightIndex(code.Command) >= 0) codeH = bars.Height;
            int fi = TextIndex(code.Command);
            if (fi >= 0 && fi < parts.Count)
                hrt = !parts[fi].Trim().StartsWith("N", StringComparison.OrdinalIgnoreCase);
        }

        return new FieldFacts(
            Data: fd?.Args,
            DataStart: fd is null ? -1 : fd.End - fd.Args.Length,
            DataEnd: fd?.End ?? -1,
            FontName: fontName, FontHeight: fontH, FontWidth: fontW,
            Barcode: code?.Command,
            BarcodeHeight: codeH,
            HumanReadable: hrt,
            // Read from the running ^BY rather than from the one inside the field:
            // there usually is none inside, and the width still is not 2.
            ModuleWidth: code is null ? null : bars.Width,
            Reverse: tokens.Any(t => t.Command == "FR"),
            Shape: shape?.Command,
            ShapeArgs: shape is null ? null : Numbers(shape.Args),
            Graphic: graphic?.Args,
            GraphicStart: graphic?.Start ?? -1,
            GraphicEnd: graphic?.End ?? -1);
    }

    // ── Changing them ───────────────────────────────────────────────────────

    /// <summary>Replaces what the field prints.</summary>
    public static Edit? SetData(string zpl, int start, int end, string value)
    {
        var fd = InSpan(zpl, start, end).FirstOrDefault(t => t.Command is "FD" or "FV");
        if (fd is null) return null;
        int at = fd.End - fd.Args.Length;
        var (_, _, trail) = SplitArgs(fd.Args);
        return new Edit(at, fd.End, value + trail);
    }

    /// <summary>
    /// Swaps the whole ^GF for another one — a picture re-imported at a different
    /// size, or converted a different way. Only that command is touched: the ^FO
    /// beside it keeps the graphic exactly where the user put it.
    /// </summary>
    public static Edit? SetGraphic(string zpl, int start, int end, string command)
    {
        var gf = InSpan(zpl, start, end).FirstOrDefault(t => t.Command is "GF" or "GFA");
        if (gf is null) return null;
        return new Edit(gf.Start, gf.End, command);
    }

    /// <summary>Rewrites the ^A that dresses a text field.</summary>
    public static Edit? SetFont(string zpl, int start, int end,
                                string? name, double? height, double? width)
    {
        var font = InSpan(zpl, start, end)
            .FirstOrDefault(t => t.Command.Length == 2 && t.Command[0] == 'A' && t.Command[1] != '@');
        if (font is null) return null;

        var (lead, body, trail) = SplitArgs(font.Args);
        var parts = body.Length == 0 ? new List<string> { "" } : body.Split(',').ToList();
        while (parts.Count < 3) parts.Add("");
        if (height is { } h) parts[1] = Whole(h);
        if (width is { } w) parts[2] = Whole(w);

        string newArgs = lead + string.Join(",", parts) + trail;
        int argsAt = font.End - font.Args.Length;
        // The font NAME lives in the command itself (^A0, ^AA…), not in its
        // arguments, so changing it means rewriting one character before them.
        if (name is null || name.Length == 0 || name[0] == font.Command[1])
            return new Edit(argsAt, font.End, newArgs);
        return new Edit(font.Start + 2, font.End, name[0] + newArgs);
    }

    /// <summary>Bar height for a 1D symbology, module size for a 2D one.</summary>
    public static Edit? SetBarcodeSize(string zpl, int start, int end, double value)
    {
        var code = FindBarcode(zpl, start, end);
        if (code is null) return null;
        int index = HeightIndex(code.Command);
        if (index < 0) index = MagnificationIndex(code.Command);
        if (index < 0) return null;
        return ReplacePart(code, index, Whole(value));
    }

    /// <summary>Shows or hides the human-readable line under a 1D symbol.</summary>
    public static Edit? SetHumanReadable(string zpl, int start, int end, bool on)
    {
        var code = FindBarcode(zpl, start, end);
        if (code is null) return null;
        int index = TextIndex(code.Command);
        if (index < 0) return null;
        return ReplacePart(code, index, on ? "Y" : "N");
    }

    /// <summary>^BY: the narrow bar width every 1D symbology is built from.</summary>
    public static Edit? SetModuleWidth(string zpl, int start, int end, double value)
    {
        var by = InSpan(zpl, start, end).FirstOrDefault(t => t.Command == "BY");
        if (by is not null) return ReplacePart(by, 0, Whole(value));
        // No ^BY of its own: give the field one, right after its origin, so the
        // change stays inside this field instead of leaking into the next.
        var origin = FindOrigin(zpl, start, end);
        if (origin is null) return null;
        return new Edit(origin.End, origin.End, "^BY" + Whole(value));
    }

    /// <summary>The size of a ^GB / ^GE box, and of a ^GC by its diameter.</summary>
    public static Edit? SetShape(string zpl, int start, int end,
                                 double? w, double? h, double? thickness)
    {
        var shape = InSpan(zpl, start, end)
            .FirstOrDefault(t => t.Command is "GB" or "GE" or "GC" or "GD");
        if (shape is null) return null;

        var (lead, body, trail) = SplitArgs(shape.Args);
        var parts = body.Length == 0 ? new List<string>() : body.Split(',').ToList();
        // ^GC is a diameter and a thickness; the others are width, height, thickness.
        int count = shape.Command == "GC" ? 2 : 3;
        while (parts.Count < count) parts.Add("");
        if (shape.Command == "GC")
        {
            if (w is { } d) parts[0] = Whole(d);
            if (thickness is { } t) parts[1] = Whole(t);
        }
        else
        {
            if (w is { } bw) parts[0] = Whole(bw);
            if (h is { } bh) parts[1] = Whole(bh);
            if (thickness is { } t) parts[2] = Whole(t);
        }
        int at = shape.End - shape.Args.Length;
        return new Edit(at, shape.End, lead + string.Join(",", parts) + trail);
    }

    /// <summary>Turns the field's ink inside out (^FR), or puts it back.</summary>
    public static Edit? SetReverse(string zpl, int start, int end, bool on)
    {
        var fr = InSpan(zpl, start, end).FirstOrDefault(t => t.Command == "FR");
        if (on)
        {
            if (fr is not null) return null;          // already reversed
            var fd = InSpan(zpl, start, end).FirstOrDefault(t => t.Command is "FD" or "FV");
            if (fd is null) return null;
            return new Edit(fd.Start, fd.Start, "^FR");
        }
        if (fr is null) return null;
        return new Edit(fr.Start, fr.End, fr.Args);   // ^FR itself goes, its trailing space stays
    }

    /// <summary>
    /// Swaps one symbology for another, rewriting the selector with that
    /// symbology's own parameter layout — the layouts do not line up, so the old
    /// arguments cannot simply be carried over.
    /// </summary>
    public static Edit? SetSymbology(string zpl, int start, int end, string command, string args)
    {
        var code = FindBarcode(zpl, start, end);
        if (code is null) return null;
        var (_, _, trail) = SplitArgs(code.Args);
        return new Edit(code.Start, code.End, "^" + command + args + trail);
    }

    // ── Removing and copying ────────────────────────────────────────────────

    /// <summary>
    /// Takes the field out. The line break that followed it goes too, so deleting
    /// an element does not leave a blank line behind every time.
    /// </summary>
    public static Edit? Delete(string zpl, int start, int end)
    {
        if (zpl is null || start < 0 || end > zpl.Length || start >= end) return null;
        int stop = end;
        if (stop < zpl.Length && zpl[stop] == '\r') stop++;
        if (stop < zpl.Length && zpl[stop] == '\n') stop++;
        return new Edit(start, stop, "");
    }

    /// <summary>
    /// Writes a copy of the field just after it, shifted so the two do not sit on
    /// top of each other. The copy's own ^FO is what moves — the original is not
    /// touched at all.
    /// </summary>
    public static Edit? Duplicate(string zpl, int start, int end,
                                  double dx, double dy, double dpmm)
    {
        if (zpl is null || start < 0 || end > zpl.Length || start >= end) return null;
        var origin = FindOrigin(zpl, start, end);
        if (origin is null) return null;

        double scale = UnitScaleAt(zpl, origin.Start, dpmm);
        if (scale <= 0) scale = 1;

        var (lead, body, trail) = SplitArgs(origin.Args);
        var parts = body.Length == 0 ? new List<string> { "", "" } : body.Split(',').ToList();
        while (parts.Count < 2) parts.Add("");
        parts[0] = Coord(Number(parts[0]) + dx / scale);
        parts[1] = Coord(Number(parts[1]) + dy / scale);

        // Rebuild the copy around its new origin, leaving everything else verbatim.
        int argsAt = origin.End - origin.Args.Length;
        string copy = zpl[start..argsAt]
                    + lead + string.Join(",", parts) + trail
                    + zpl[origin.End..end];
        return new Edit(end, end, "\n" + copy);
    }

    /// <summary>The span of the field, for a caller that needs to read it back.</summary>
    public static string Slice(string zpl, int start, int end)
        => zpl is null || start < 0 || end > zpl.Length || start >= end ? "" : zpl[start..end];

    // ── Shared bits ─────────────────────────────────────────────────────────

    private static ZplToken? FindBarcode(string zpl, int start, int end)
        => InSpan(zpl, start, end)
            .FirstOrDefault(t => t.Command.Length == 2 && t.Command[0] == 'B' && t.Command != "BY");

    private static Edit ReplacePart(ZplToken token, int index, string value)
    {
        var (lead, body, trail) = SplitArgs(token.Args);
        var parts = body.Length == 0 ? new List<string>() : body.Split(',').ToList();
        while (parts.Count <= index) parts.Add("");
        parts[index] = value;
        int at = token.End - token.Args.Length;
        return new Edit(at, token.End, lead + string.Join(",", parts) + trail);
    }

    private static List<string> Parts(string args)
    {
        var (_, body, _) = SplitArgs(args);
        return body.Length == 0 ? new List<string>() : body.Split(',').ToList();
    }

    private static double[] Numbers(string args) => Parts(args).Select(Number).ToArray();

    private static string Whole(double value)
        => ((int)Math.Round(Math.Clamp(value, 0, MaxCoord))).ToString(CultureInfo.InvariantCulture);

    // Where the bar height sits in each symbology's argument list. -1 = it has none
    // (the 2D symbols size themselves by module instead).
    private static int HeightIndex(string command) => command switch
    {
        "BC" or "BA" or "BE" or "B8" or "BU" or "B2" or "BI" or "BJ"
            or "BZ" or "B5" or "B9" or "BS" or "BL" => 1,
        "B3" or "BK" or "B1" or "BM" or "BP" => 2,
        _ => -1,
    };

    // Where the "print the interpretation line" flag sits. -1 = no such flag.
    private static int TextIndex(string command) => command switch
    {
        "BC" or "BA" or "BE" or "B8" or "BU" or "B2" or "BI" or "BJ"
            or "BZ" or "B5" or "B9" or "BS" => 2,
        "B3" or "BK" or "B1" or "BM" or "BP" => 3,
        _ => -1,
    };

    // Where the module size / magnification sits, for the 2D symbols.
    private static int MagnificationIndex(string command) => command switch
    {
        "BQ" => 2,
        "BX" or "BO" or "B0" or "B7" => 1,
        _ => -1,
    };

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
