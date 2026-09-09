using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// Rewriting a whole label rather than one field.
//
// Two transformations live here, and they are the same transformation twice: a
// density change scales every length, a rotation turns every position. Both are
// applied by rewriting NUMBERS WHERE THEY STAND — the comments, the line breaks
// and the commands the engine has never heard of come out byte for byte as they
// went in — which is the same rule the edit mode follows.
//
// The density half is local: a length is a length wherever it sits, so it only
// needs the ratio. The rotation half needs to know where things are, because a
// ^FO anchors the top-left corner of a field's box and a corner is not a point:
// turn the field and a different corner becomes the top-left one. That geometry
// is not re-derived here — the render model already measures every field, and
// this asks it.
//
// One case escapes the corner rule and is exact: ^FT anchors the baseline, which
// IS a material point of the glyphs, so it travels with them.
public static class ZplTransform
{
    /// <summary>Something the engine met and did not rewrite. Kind is a language key.</summary>
    public sealed record Note(string Kind, string Command, int Line);

    public sealed record Plan(IReadOnlyList<ZplPatcher.Edit> Edits, IReadOnlyList<Note> Notes)
    {
        public bool Empty => Edits.Count == 0;
    }

    public static readonly Plan Nothing =
        new(Array.Empty<ZplPatcher.Edit>(), Array.Empty<Note>());

    // ── Entry point ──────────────────────────────────────────────────────────

    /// <summary>
    /// The edits that turn <paramref name="zpl"/> into the same label at another
    /// density, another angle, or both. Every range is measured against the text as
    /// given, so the caller applies them as one change and one undo.
    /// </summary>
    /// <param name="currentDpmm">What the document is written for today.</param>
    /// <param name="targetDpmm">null leaves the lengths alone.</param>
    /// <param name="rotate">0, 90, 180 or 270, clockwise.</param>
    public static Plan Build(string zpl, double currentDpmm, double? targetDpmm, int rotate)
    {
        if (string.IsNullOrEmpty(zpl)) return Nothing;

        int rot = ((rotate % 360) + 360) % 360;
        if (rot % 90 != 0) rot = 0;
        double ratio = targetDpmm is { } target && currentDpmm > 0 && target > 0
            ? target / currentDpmm : 1;
        bool scaling = Math.Abs(ratio - 1) > 1e-9;
        if (!scaling && rot == 0) return Nothing;

        var tokens = ZplRenderer.TokenizeForEditing(zpl);
        var edits = new List<ZplPatcher.Edit>();
        var notes = new List<Note>();

        // Read once for the whole document. It is the reading that gets a ^DF/^XF
        // pair right — one label stores the format, another prints it, and neither
        // half draws anything on its own — and for the ordinary single-label file
        // it is the only reading needed.
        var whole = rot == 0 ? null : ZplRenderer.Parse(zpl, currentDpmm);

        foreach (var block in Blocks(zpl, tokens))
        {
            var fields = new List<Field>();
            double labelW = 0, labelH = 0;

            if (rot != 0)
            {
                fields = FieldsOf(whole!, block.Start, block.End);
                labelW = whole!.Size.WidthDots;
                labelH = whole.Size.HeightDots;

                if (fields.Count == 0)
                {
                    // A later label in a stream: the reader stops at the first one
                    // that printed something, so this block is read on its own —
                    // with whatever came before it still in scope, since a graphic
                    // downloaded by name outlives the label that downloaded it.
                    var upTo = ZplRenderer.Parse(UpTo(zpl, block), currentDpmm);
                    fields = FieldsOf(upTo, block.Start, block.End);
                    var local = ZplRenderer.Parse(zpl[block.Start..block.End], currentDpmm);
                    if (local.Drawables.Count > 0)
                    {
                        labelW = local.Size.WidthDots;
                        labelH = local.Size.HeightDots;
                    }
                }
            }

            Walk(zpl, tokens, block, fields, Bands(zpl, tokens, block),
                 labelW, labelH, ratio, rot, edits, notes);
        }

        // Same range twice is a contradiction, and an empty rewrite is noise.
        var clean = edits
            .Where(e => e.Start >= 0 && e.End <= zpl.Length && e.Start <= e.End)
            .Where(e => e.Text != zpl[e.Start..e.End])
            .GroupBy(e => (e.Start, e.End))
            .Select(g => g.First())
            .OrderBy(e => e.Start).ThenBy(e => e.End)
            .ToList();

        return new Plan(Coalesce(clean), Dedupe(notes));
    }

    /// <summary>The label's size once the plan has been applied, in dots.</summary>
    public static (double W, double H) SizeAfter(ZplRenderModel model, double ratio, int rot)
    {
        double w = model.Size.WidthDots * ratio, h = model.Size.HeightDots * ratio;
        return rot is 90 or 270 ? (h, w) : (w, h);
    }

    // ── One label ────────────────────────────────────────────────────────────

    private readonly record struct Block(int Start, int End, int OpenEnd);

    // Each ^XA…^XZ, and whatever lies outside them as one more block so a fragment
    // without a header is still converted.
    private static List<Block> Blocks(string zpl, IReadOnlyList<ZplToken> tokens)
    {
        var blocks = new List<Block>();
        int start = -1, openEnd = -1;
        foreach (var t in tokens)
        {
            if (t.Command is "XA") { start = t.Start; openEnd = t.Start + 3; }
            else if (t.Command is "XZ" && start >= 0)
            {
                blocks.Add(new Block(start, t.End, openEnd));
                start = -1;
            }
        }
        if (start >= 0) blocks.Add(new Block(start, zpl.Length, openEnd));
        if (blocks.Count == 0) blocks.Add(new Block(0, zpl.Length, -1));
        // Whatever stands in front of the first ^XA is a block of its own: a ~DG
        // downloads a graphic there, and the window should say it was left alone
        // rather than pass over it in silence.
        if (blocks[0].Start > 0) blocks.Insert(0, new Block(0, blocks[0].Start, -1));
        return blocks;
    }

    // Everything up to the end of this block, with every ^XZ in front of it turned
    // into blanks of the same length: the reader stops at the first end of format,
    // and a graphic downloaded for an earlier label would never be seen otherwise.
    private static string UpTo(string zpl, Block block)
    {
        if (block.Start == 0) return zpl[..block.End];
        var text = zpl[..block.End].ToCharArray();
        for (int i = 0; i + 2 < block.Start; i++)
            if (text[i] == '^' && (text[i + 1] is 'X' or 'x') && (text[i + 2] is 'Z' or 'z'))
                text[i] = text[i + 1] = text[i + 2] = ' ';
        return new string(text);
    }

    private sealed class Field
    {
        public int Start;
        public int End;
        public ZplRect Box;
        public bool TextOnly;
        public ZplImage? Image;
    }

    // Every field of one label, with the box it covers. Drawables carry the span of
    // the code that produced them, which is what ties a rectangle back to its ^FO.
    private static List<Field> FieldsOf(ZplRenderModel model, int from, int to)
    {
        var byStart = new Dictionary<int, Field>();
        const int offset = 0;
        foreach (var d in model.Drawables)
        {
            if (d.SourceStart < from || d.SourceStart >= to) continue;
            var box = ZplRenderer.BoundsOf(d);
            if (!byStart.TryGetValue(d.SourceStart, out var field))
            {
                byStart[d.SourceStart] = new Field
                {
                    Start = d.SourceStart + offset,
                    End = d.SourceEnd + offset,
                    Box = box,
                    TextOnly = d is ZplText,
                    Image = d as ZplImage,
                };
                continue;
            }
            field.Box = Union(field.Box, box);
            if (d.SourceEnd + offset > field.End) field.End = d.SourceEnd + offset;
            if (d is not ZplText) field.TextOnly = false;
            field.Image ??= d as ZplImage;
        }
        return byStart.Values.OrderBy(f => f.Start).ToList();
    }

    private static ZplRect Union(ZplRect a, ZplRect b)
    {
        if (a.Width <= 0 && a.Height <= 0) return b;
        if (b.Width <= 0 && b.Height <= 0) return a;
        double l = Math.Min(a.X, b.X), t = Math.Min(a.Y, b.Y);
        double r = Math.Max(a.Right, b.Right), bo = Math.Max(a.Bottom, b.Bottom);
        return new ZplRect(l, t, r - l, bo - t);
    }

    // ── The walk ─────────────────────────────────────────────────────────────

    // Commands that carry a size this engine does not know how to rewrite. Naming
    // them is the whole point: the window says what it left alone rather than
    // leaving the reader to find out on the printer.
    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "XG", "IM", "IL", "BR", "BB", "B4", "BT", "BD", "DG", "DY",
    };

    private static void Walk(string zpl, IReadOnlyList<ZplToken> tokens, Block block,
                             List<Field> fields, Dictionary<int, (double Along, double Across, bool Sideways)> bands,
                             double labelW, double labelH, double ratio, int rot,
                             List<ZplPatcher.Edit> edits, List<Note> notes)
    {
        bool scaling = Math.Abs(ratio - 1) > 1e-9;
        bool inDots = true;         // ^MU can express coordinates in inches or millimetres
        double lhX = 0, lhY = 0, lsX = 0, ltY = 0;
        bool sawFieldWidth = false;

        var byStart = fields.ToDictionary(f => f.Start);

        foreach (var t in tokens)
        {
            if (t.Start < block.Start || t.Start >= block.End) continue;
            var parts = Split(zpl, t);
            double At(int i) => i < parts.Count && Numeric(parts[i]) is { } n ? n.Value : 0;

            switch (t.Command)
            {
                // ── The frame ───────────────────────────────────────────────
                case "PW":
                    Write(edits, parts, 0, rot is 90 or 270 && labelH > 0
                        ? labelH * ratio : At(0) * ratio, 1);
                    break;
                case "LL":
                    Write(edits, parts, 0, rot is 90 or 270 && labelW > 0
                        ? labelW * ratio : At(0) * ratio, 1);
                    break;

                // A shift of the whole label, in the label's own frame: it keeps
                // meaning what it meant, only in bigger or smaller dots.
                case "LH":
                    lhX = At(0); lhY = At(1);
                    if (scaling) { Write(edits, parts, 0, lhX * ratio, 0); Write(edits, parts, 1, lhY * ratio, 0); }
                    break;
                case "LS":
                    lsX = At(0);
                    if (scaling) Write(edits, parts, 0, lsX * ratio, double.MinValue);
                    break;
                case "LT":
                    ltY = At(0);
                    if (scaling) Write(edits, parts, 0, ltY * ratio, double.MinValue);
                    break;

                // ── Where a field starts ────────────────────────────────────
                case "FO":
                case "FT":
                {
                    if (!inDots) break;
                    double rawX = At(0), rawY = At(1);
                    double x = rawX + lhX + lsX, y = rawY + lhY + ltY;
                    double nx = x, ny = y;

                    if (rot != 0)
                    {
                        var field = byStart.TryGetValue(t.Start, out var f) ? f : null;
                        bool point = t.Command == "FT" && (field is null || field.TextOnly);
                        if (point)
                        {
                            (nx, ny) = TurnPoint(x, y, labelW, labelH, rot);
                        }
                        else
                        {
                            var box = field?.Box ?? new ZplRect(x, y, 0, 0);
                            // A ^FB field covers the band it RESERVES, not the words
                            // that happen to fill it: the band is what the origin
                            // anchors, and it starts AT the origin however the text
                            // inside it is justified. The model only measures ink.
                            if (bands.TryGetValue(t.Start, out var band))
                                box = new ZplRect(x, y,
                                    band.Sideways ? band.Across : band.Along,
                                    band.Sideways ? band.Along : band.Across);
                            var turned = TurnBox(box.X, box.Y, box.Width, box.Height, labelW, labelH, rot);
                            // ^FO anchors the top-left of the box; ^FT on a graphic
                            // anchors its bottom-left, and turning the label picks a
                            // different corner for each.
                            nx = turned.X;
                            ny = t.Command == "FT" ? turned.Y + turned.H : turned.Y;
                        }
                    }

                    Write(edits, parts, 0, (nx - lhX - lsX) * ratio, double.MinValue, fill: true);
                    Write(edits, parts, 1, (ny - lhY - ltY) * ratio, double.MinValue, fill: true);
                    break;
                }

                // ── Which way a field reads ─────────────────────────────────
                case "FW":
                    sawFieldWidth = true;
                    if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: true);
                    break;

                // ── Type ────────────────────────────────────────────────────
                case "CF":
                    if (scaling) { Write(edits, parts, 1, At(1) * ratio, 1); Write(edits, parts, 2, At(2) * ratio, 0); }
                    break;

                case "FB":
                    if (scaling)
                    {
                        Write(edits, parts, 0, At(0) * ratio, 0);
                        Write(edits, parts, 2, At(2) * ratio, 0);
                        Write(edits, parts, 4, At(4) * ratio, 0);
                    }
                    break;

                case "FP":
                    if (scaling) Write(edits, parts, 1, At(1) * ratio, 0);
                    break;

                // ── Shapes ──────────────────────────────────────────────────
                case "GB":
                case "GE":
                {
                    // A box turned a quarter turn is a box on its side.
                    double w = At(0), h = At(1);
                    if (rot is 90 or 270) (w, h) = (h, w);
                    Write(edits, parts, 0, w * ratio, 0);
                    Write(edits, parts, 1, h * ratio, 0);
                    if (scaling) Write(edits, parts, 2, At(2) * ratio, 1);
                    break;
                }
                case "GD":
                {
                    double w = At(0), h = At(1);
                    if (rot is 90 or 270)
                    {
                        (w, h) = (h, w);
                        FlipDiagonal(edits, parts, 4);
                    }
                    Write(edits, parts, 0, w * ratio, 0);
                    Write(edits, parts, 1, h * ratio, 0);
                    if (scaling) Write(edits, parts, 2, At(2) * ratio, 1);
                    break;
                }
                case "GC":
                    if (scaling) { Write(edits, parts, 0, At(0) * ratio, 3); Write(edits, parts, 1, At(1) * ratio, 1); }
                    break;
                case "GS":
                    if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: false);
                    if (scaling) { Write(edits, parts, 1, At(1) * ratio, 1); Write(edits, parts, 2, At(2) * ratio, 1); }
                    break;

                // ── Barcodes ────────────────────────────────────────────────
                case "BY":
                    if (scaling) { Write(edits, parts, 0, At(0) * ratio, 1); Write(edits, parts, 2, At(2) * ratio, 1); }
                    break;

                case "GF":
                case "GFA":
                    if (!RewriteGraphic(zpl, t, fields, ratio, rot, edits))
                        notes.Add(new Note("image", Named(zpl, t), LineOf(zpl, t.Start)));
                    break;

                default:
                    if (t.Command == "MU")
                    {
                        var unit = parts.Count > 0 ? parts[0].Text.Trim().ToUpperInvariant() : "U";
                        inDots = unit.Length == 0 || unit[0] == 'U';
                        if (!inDots) notes.Add(new Note("units", "^MU", LineOf(zpl, t.Start)));
                    }
                    else if (t.Command == "JM")
                    {
                        notes.Add(new Note("declared", "^JM", LineOf(zpl, t.Start)));
                    }
                    else if (Unsupported.Contains(t.Command))
                    {
                        notes.Add(new Note("unsupported", Named(zpl, t), LineOf(zpl, t.Start)));
                    }
                    else if (t.Command.StartsWith("A", StringComparison.Ordinal) && t.Command.Length <= 2)
                    {
                        // ^A0N,30,30 / ^ADN,18,10 / ^A@N,50,50,E:F.TTF — the height and
                        // the width always follow the orientation, whatever the font.
                        if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: false);
                        if (scaling) { Write(edits, parts, 1, At(1) * ratio, 1); Write(edits, parts, 2, At(2) * ratio, 0); }
                    }
                    else if (HeightAt.TryGetValue(t.Command, out int at))
                    {
                        if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: false);
                        if (scaling) Write(edits, parts, at, At(at) * ratio, 1);
                    }
                    break;
            }
        }

        // Nothing said which way the fields that never said so should read. One
        // ^FW at the top of the label says it once, for all of them.
        if (rot != 0 && !sawFieldWidth && block.OpenEnd > 0)
        {
            // On its own line where ^XA had one, so the document keeps the shape it
            // was written in rather than growing a command on the end of a line.
            int at = block.OpenEnd;
            var line = "^FW" + Advance('N', rot);
            if (at + 1 < zpl.Length && zpl[at] == '\r' && zpl[at + 1] == '\n')
            { at += 2; line += "\r\n"; }
            else if (at < zpl.Length && zpl[at] == '\n')
            { at += 1; line += "\n"; }
            edits.Add(new ZplPatcher.Edit(at, at, line));
        }
    }

    private static bool HasLetter(string part)
    {
        foreach (var c in part) if ("NRIBnrib".IndexOf(c) >= 0) return true;
        return false;
    }

    private static int Degrees(string part)
    {
        foreach (var c in part)
        {
            int i = "NRIB".IndexOf(char.ToUpperInvariant(c));
            if (i >= 0) return i * 90;
        }
        return 0;
    }

    // The band each ^FB field reserves, keyed by the ^FO/^FT that opens the field.
    //
    // ^FB sets a width along the line and a COUNT of lines; the height that count
    // stands for is the type size in force at that point, which is why this walks
    // the ^CF and ^A in front of it rather than reading ^FB on its own.
    private static Dictionary<int, (double Along, double Across, bool Sideways)> Bands(
        string zpl, IReadOnlyList<ZplToken> tokens, Block block)
    {
        var bands = new Dictionary<int, (double, double, bool)>();
        double defaultHeight = 9;      // power-on font A
        double height = defaultHeight;
        int fieldWide = 0;             // ^FW default reading direction
        int turn = 0;                  // this field's own
        int field = -1;

        foreach (var t in tokens)
        {
            if (t.Start < block.Start || t.Start >= block.End) continue;
            var parts = Split(zpl, t);
            double At(int i) => i < parts.Count && Numeric(parts[i]) is { } n ? n.Value : 0;

            switch (t.Command)
            {
                case "FO":
                case "FT":
                    field = t.Start;
                    height = defaultHeight;
                    turn = fieldWide;
                    break;
                case "FS":
                    field = -1;
                    height = defaultHeight;
                    turn = fieldWide;
                    break;
                case "FW":
                    fieldWide = turn = Degrees(parts.Count > 0 ? parts[0].Text : "");
                    break;
                case "CF":
                    if (At(1) > 0) defaultHeight = height = At(1);
                    break;
                case "FB":
                    if (field >= 0 && At(0) > 0)
                        bands[field] = (At(0), Math.Max(1, At(1)) * (height + At(2)), turn is 90 or 270);
                    break;
                default:
                    if (t.Command.StartsWith("A", StringComparison.Ordinal) && t.Command.Length <= 2)
                    {
                        if (At(1) > 0) height = At(1);
                        if (parts.Count > 0 && Degrees(parts[0].Text) is var d && HasLetter(parts[0].Text)) turn = d;
                    }
                    break;
            }
        }
        return bands;
    }

    // Where a symbology writes the height (or, for the 2D ones, the module size)
    // among its comma-separated parameters. Read off the parser, so the engine and
    // the preview agree on what every number means.
    private static readonly Dictionary<string, int> HeightAt = new(StringComparer.Ordinal)
    {
        ["BC"] = 1, ["B2"] = 1, ["BE"] = 1, ["BU"] = 1, ["B8"] = 1, ["B9"] = 1,
        ["BS"] = 1, ["BA"] = 1, ["BI"] = 1, ["BJ"] = 1, ["BZ"] = 1, ["B5"] = 1,
        ["BL"] = 1, ["BF"] = 1, ["BX"] = 1, ["B7"] = 1, ["BO"] = 1, ["B0"] = 1,
        ["B3"] = 2, ["B1"] = 2, ["BK"] = 2, ["BM"] = 2, ["BP"] = 2,
        ["BQ"] = 2,
    };

    // ── Geometry ─────────────────────────────────────────────────────────────

    // The label turns clockwise inside its own frame; a quarter turn swaps the two
    // sides of the media with it.
    private static (double X, double Y) TurnPoint(double x, double y, double w, double h, int rot) => rot switch
    {
        90 => (h - y, x),
        180 => (w - x, h - y),
        270 => (y, w - x),
        _ => (x, y),
    };

    private static (double X, double Y, double W, double H) TurnBox(
        double x, double y, double bw, double bh, double w, double h, int rot) => rot switch
    {
        90 => (h - y - bh, x, bh, bw),
        180 => (w - x - bw, h - y - bh, bw, bh),
        270 => (y, w - x - bw, bh, bw),
        _ => (x, y, bw, bh),
    };

    private static char Advance(char c, int rot)
    {
        const string order = "NRIB";     // 0°, 90°, 180°, 270°
        int i = order.IndexOf(char.ToUpperInvariant(c));
        return i < 0 ? c : order[(i + rot / 90) % 4];
    }

    // ── Rewriting ────────────────────────────────────────────────────────────

    private readonly record struct Part(int Start, int End, string Text);

    // The comma-separated arguments, each knowing where it sits in the document.
    private static List<Part> Split(string zpl, ZplToken t)
    {
        var parts = new List<Part>();
        int at = t.End - t.Args.Length;
        if (at < 0 || t.End > zpl.Length) return parts;
        int from = at;
        for (int i = at; i <= t.End; i++)
        {
            if (i == t.End || zpl[i] == ',')
            {
                parts.Add(new Part(from, i, zpl[from..i]));
                from = i + 1;
            }
        }
        return parts;
    }

    // The number inside one argument, and exactly where it starts and stops — the
    // rest of the argument (a font path, a stray space) is never touched.
    private static (int Start, int End, double Value)? Numeric(Part p)
    {
        var text = p.Text;
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        int s = i;
        if (i < text.Length && (text[i] == '-' || text[i] == '+')) i++;
        int digits = 0;
        while (i < text.Length && char.IsDigit(text[i])) { i++; digits++; }
        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsDigit(text[i])) i++;
        }
        if (digits == 0) return null;
        return double.TryParse(text[s..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (p.Start + s, p.Start + i, v) : null;
    }

    private static void Write(List<ZplPatcher.Edit> edits, List<Part> parts, int index,
                              double value, double min, bool fill = false)
    {
        if (index < 0 || parts.Count == 0) return;
        // ^FO50 leaves the y out, which means zero — until the label turns and zero
        // is no longer where it belongs, so a coordinate is written rather than lost.
        // Everywhere else an absent argument means "the printer's own default", and
        // filling it in would be inventing a value nobody asked for: ^BY3 says
        // nothing about the bar height, and ^BY3,,1 says it is one dot tall.
        if (index >= parts.Count)
        {
            if (!fill) return;
            long missing = (long)Math.Round(min == double.MinValue ? value : Math.Max(min, value),
                                            MidpointRounding.AwayFromZero);
            if (missing == 0) return;
            var tail = parts[^1];
            edits.Add(new ZplPatcher.Edit(tail.End, tail.End,
                new string(',', index - parts.Count + 1) + missing.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        var part = parts[index];
        double clamped = min == double.MinValue ? value : Math.Max(min, value);
        long rounded = (long)Math.Round(clamped, MidpointRounding.AwayFromZero);
        var text = rounded.ToString(CultureInfo.InvariantCulture);

        if (Numeric(part) is { } n) edits.Add(new ZplPatcher.Edit(n.Start, n.End, text));
        else if (rounded != 0) edits.Add(new ZplPatcher.Edit(part.End, part.End, text));
    }

    // Advances the N/R/I/B an argument carries. With insert, one is written where
    // the argument had none — that is how a bare ^FW is given a direction.
    private static void TurnLetter(List<ZplPatcher.Edit> edits, List<Part> parts, int index, int rot, bool insert)
    {
        if (index >= parts.Count) return;
        var part = parts[index];
        for (int i = 0; i < part.Text.Length; i++)
        {
            char c = char.ToUpperInvariant(part.Text[i]);
            if ("NRIB".IndexOf(c) < 0) continue;
            edits.Add(new ZplPatcher.Edit(part.Start + i, part.Start + i + 1, Advance(c, rot).ToString()));
            return;
        }
        if (insert) edits.Add(new ZplPatcher.Edit(part.End, part.End, Advance('N', rot).ToString()));
    }

    // ^GD reads its slope from an R/L, and a quarter turn turns one into the other.
    private static void FlipDiagonal(List<ZplPatcher.Edit> edits, List<Part> parts, int index)
    {
        if (index >= parts.Count) return;
        var part = parts[index];
        for (int i = 0; i < part.Text.Length; i++)
        {
            char c = char.ToUpperInvariant(part.Text[i]);
            if (c is not ('R' or 'L')) continue;
            edits.Add(new ZplPatcher.Edit(part.Start + i, part.Start + i + 1, c == 'R' ? "L" : "R"));
            return;
        }
    }

    // ── Pictures ─────────────────────────────────────────────────────────────

    // A bitmap has no numbers to scale: it has pixels, and the only way to keep a
    // picture the same size on the label at another density is to draw it again
    // with more or fewer of them. The decoded image comes from the render model,
    // so this never re-implements the ^GF reader.
    private static bool RewriteGraphic(string zpl, ZplToken t, List<Field> fields,
                                       double ratio, int rot, List<ZplPatcher.Edit> edits)
    {
        if (Math.Abs(ratio - 1) < 1e-9 && rot == 0) return true;

        var field = fields.FirstOrDefault(f => f.Image is not null
                                               && f.Start <= t.Start && t.Start < f.End);
        var image = field?.Image;
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0) return false;

        try
        {
            using var bitmap = ZplImageImport.FromBits(image.PixelWidth, image.PixelHeight, image.Bits);
            switch (rot)
            {
                case 90: bitmap.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone); break;
                case 180: bitmap.RotateFlip(System.Drawing.RotateFlipType.Rotate180FlipNone); break;
                case 270: bitmap.RotateFlip(System.Drawing.RotateFlipType.Rotate270FlipNone); break;
            }

            int w = (int)Math.Max(1, Math.Round(bitmap.Width * ratio));
            int h = (int)Math.Max(1, Math.Round(bitmap.Height * ratio));
            if (w > ZplImageImport.MaxSide || h > ZplImageImport.MaxSide) return false;

            var mono = ZplImageImport.Convert(bitmap, w, h, dither: false, threshold: 0.5, invert: false);
            var command = ZplImageImport.ToGraphicField(mono, ZplImageImport.FormatOf(t.Args));

            // Up to the end of the payload only: the line break behind it belongs to
            // the document, not to the command.
            int end = t.End;
            while (end > t.Start && char.IsWhiteSpace(zpl[end - 1])) end--;
            edits.Add(new ZplPatcher.Edit(t.Start, end, command));
            return true;
        }
        catch { return false; }
    }

    // ── Small change ─────────────────────────────────────────────────────────

    // Rewrites that meet end to end become one. The editor resolves a batch of
    // ranges against the document as it stands, and a zero-length range sitting on
    // another range's edge reads as a conflict there: it drops the whole batch, and
    // the document would keep its old text while the preview moved on without it.
    private static List<ZplPatcher.Edit> Coalesce(List<ZplPatcher.Edit> sorted)
    {
        var merged = new List<ZplPatcher.Edit>();
        foreach (var edit in sorted)
        {
            if (merged.Count > 0 && edit.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = new ZplPatcher.Edit(last.Start, Math.Max(last.End, edit.End),
                                                 last.Text + edit.Text);
                continue;
            }
            merged.Add(edit);
        }
        return merged;
    }

    // The command as it is written, ~DG and ^XG included: the sigil is part of the
    // name, and half the pair would be a different command.
    private static string Named(string zpl, ZplToken t)
        => (t.Start < zpl.Length ? zpl[t.Start] : '^') + t.Command;

    private static int LineOf(string zpl, int at)
    {
        int line = 1;
        for (int i = 0; i < at && i < zpl.Length; i++) if (zpl[i] == '\n') line++;
        return line;
    }

    private static List<Note> Dedupe(List<Note> notes) => notes
        .GroupBy(n => (n.Kind, n.Command))
        .Select(g => g.OrderBy(n => n.Line).First())
        .OrderBy(n => n.Line)
        .ToList();
}
