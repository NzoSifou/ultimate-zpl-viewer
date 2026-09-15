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
    /// <param name="roundUp">Which way a length that lands on a half goes.</param>
    public static Plan Build(string zpl, double currentDpmm, double? targetDpmm, int rotate,
                             bool roundUp = true)
    {
        _roundUp = roundUp;
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
        //
        // It is read for a density change as well as for a rotation, because the
        // fields carry the DECODED pictures: a ^GF has no numbers to scale, only
        // pixels, and the only way to redraw it is to be handed the image back.
        var whole = ZplRenderer.Parse(zpl, currentDpmm);

        foreach (var block in Blocks(zpl, tokens))
        {
            var fields = FieldsOf(whole, block.Start, block.End);
            double labelW = whole.Size.WidthDots;
            double labelH = whole.Size.HeightDots;

            if (fields.Count == 0)
            {
                // A later label in a stream: the reader stops at the first one that
                // printed something, so this block is read on its own — with
                // whatever came before it still in scope, since a graphic
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

            Walk(zpl, tokens, block, fields, Bands(zpl, tokens, block),
                 labelW, labelH, ratio, rot, edits, notes);
        }

        // Same range rewritten twice is a contradiction, and an empty rewrite is
        // noise. Two INSERTIONS at one spot are neither: a label can need both a
        // ^FW and a ^BY written in at the top, and they are not each other's
        // duplicate just because they go in at the same place.
        var kept = edits
            .Where(e => e.Start >= 0 && e.End <= zpl.Length && e.Start <= e.End)
            .Where(e => e.Text != zpl[e.Start..e.End])
            .ToList();
        var clean = kept.Where(e => e.Start < e.End)
            .GroupBy(e => (e.Start, e.End))
            .Select(g => g.First())
            .Concat(kept.Where(e => e.Start == e.End))
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
        // The barcode this field draws, when it draws one: its ^FT does not anchor
        // the bottom of the block the way every other graphic's does.
        public ZplBars? Bars;
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
                    TextOnly = ReadsAsText(d),
                    Image = d as ZplImage,
                    Bars = d as ZplBars,
                };
                continue;
            }
            field.Box = Union(field.Box, box);
            if (d.SourceEnd + offset > field.End) field.End = d.SourceEnd + offset;
            if (!ReadsAsText(d)) field.TextOnly = false;
            field.Image ??= d as ZplImage;
            field.Bars ??= d as ZplBars;
        }
        return byStart.Values.OrderBy(f => f.Start).ToList();
    }

    // Whether a drawable is one a TEXT field produced. It is not quite "is it a
    // ZplText": the Zebra typeface draws its dash as a bar rather than a glyph, so
    // a field holding a hyphen comes back as text runs with bars between them and
    // is still a text field — its ^FT anchors a baseline, not a corner.
    private static bool ReadsAsText(ZplDrawable d)
        => d is ZplText || d is ZplBox { TextRule: true };

    // How far below the top of its turned box a ^FT graphic's anchor sits.
    //
    // For anything but a barcode that is the whole height: ^FT holds the bottom
    // edge. A barcode is the exception — ^FT holds the bottom of the BARS, and the
    // interpretation line hangs below the anchor, outside. So the drop is the bar
    // height, not the block height, and the two differ by that line. Standing the
    // barcode on its side changes the question again: the anchor then holds the end
    // of the bar RUN, which is the box height in that frame. The renderer decides
    // it the same way — these two have to agree or the barcode lands with the
    // interpretation line's worth of offset, on top of whatever is underneath.
    private static double AnchorDrop(Field? field, (double X, double Y, double W, double H) turned, int rot)
    {
        if (field?.Bars is not { } bars) return turned.H;
        int after = ((bars.Rotation + rot) % 360 + 360) % 360;
        if (after is 90 or 270) return bars.Width;
        return bars.BarHeight > 0 ? bars.BarHeight : turned.H;
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
        bool sawModuleWidth = false;
        bool needsModuleWidth = false;

        var byStart = fields.ToDictionary(f => f.Start);

        foreach (var t in tokens)
        {
            if (t.Start < block.Start || t.Start >= block.End) continue;
            var parts = Split(zpl, t);
            double At(int i) => i < parts.Count && Numeric(parts[i]) is { } n ? n.Value : 0;

            // A built-in font is a picture of an alphabet at one size, and the
            // printer can only double or treble it. Between those steps there is
            // nothing, so a conversion by three halves leaves the type where it was
            // or jumps it a whole step — which is worth saying rather than hiding.
            // A module that cannot be converted exactly is worth a word: the symbol
            // ends up a measurable amount wider or narrower than the one that was
            // there, and no arrangement of whole dots avoids it.
            void NoteModule(ZplToken token, double original)
            {
                if (original <= 0) return;
                double ideal = original * ratio;
                long landed = Whole(Math.Max(ModuleFloor(original), ideal));
                if (Math.Abs(landed - ideal) < 0.01) return;
                int percent = (int)Math.Round((landed / ideal - 1) * 100);
                // A real minus sign: the hyphen on the keyboard is a different
                // character and reads as one in a line of figures.
                var change = percent > 0 ? "+" + percent : "−" + Math.Abs(percent);
                notes.Add(new Note("module",
                    $"{Named(zpl, token)}{original:0.##} → {landed} ({change} %)",
                    LineOf(zpl, token.Start)));
            }

            void NoteFont(string font, double height)
            {
                if (!scaling || font.Length == 0 || !ZplFont.IsBitmap(font)) return;
                double asked = (height > 0 ? height : ZplFont.BaseCell(font).H) * ratio;
                var (landed, _) = ZplFont.Quantize(font, asked, asked);
                if (Math.Abs(landed - asked) > 1)
                    notes.Add(new Note("bitmapFont", Named(zpl, t), LineOf(zpl, t.Start)));
            }

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
                //
                // Turning the label is the exception. These offsets are added to
                // every coordinate before anything is drawn, so a coordinate is
                // written relative to them — and a quarter turn can land a field
                // nearer the edge than the offset itself, which would have to be
                // written as a NEGATIVE ^FO. ZPL has no negative coordinates: a
                // printer reads one as garbage and the field is lost. So a turn
                // folds the offsets into the coordinates and zeroes them here;
                // every field is then written where it really is.
                case "LH":
                    lhX = At(0); lhY = At(1);
                    if (rot != 0) { Write(edits, parts, 0, 0, 0); Write(edits, parts, 1, 0, 0); }
                    else if (scaling) { Write(edits, parts, 0, lhX * ratio, 0); Write(edits, parts, 1, lhY * ratio, 0); }
                    break;
                case "LS":
                    lsX = At(0);
                    if (rot != 0) Write(edits, parts, 0, 0, double.MinValue);
                    else if (scaling) Write(edits, parts, 0, lsX * ratio, double.MinValue);
                    break;
                case "LT":
                    ltY = At(0);
                    if (rot != 0) Write(edits, parts, 0, 0, double.MinValue);
                    else if (scaling) Write(edits, parts, 0, ltY * ratio, double.MinValue);
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
                            ny = t.Command == "FT" ? turned.Y + AnchorDrop(field, turned, rot) : turned.Y;
                        }
                    }

                    // Turned, the offsets above have been zeroed, so what is written
                    // is the position itself; upright, they still stand and the
                    // coordinate stays relative to them, exactly as it was written.
                    double offX = rot != 0 ? 0 : lhX + lsX;
                    double offY = rot != 0 ? 0 : lhY + ltY;
                    Write(edits, parts, 0, (nx - offX) * ratio, double.MinValue, fill: true);
                    Write(edits, parts, 1, (ny - offY) * ratio, double.MinValue, fill: true);
                    break;
                }

                // ── Which way a field reads ─────────────────────────────────
                case "FW":
                    sawFieldWidth = true;
                    if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: true);
                    break;

                // ── Type ────────────────────────────────────────────────────
                case "CF":
                    NoteFont(parts.Count > 0 ? parts[0].Text.Trim() : "", At(1));
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
                    sawModuleWidth = true;
                    if (scaling)
                    {
                        NoteModule(t, At(0));
                        Write(edits, parts, 0, At(0) * ratio, ModuleFloor(At(0)));
                        Write(edits, parts, 2, At(2) * ratio, 1);
                    }
                    break;

                case "GF":
                case "GFA":
                    if (!RewriteGraphic(zpl, t, fields, ratio, rot, edits))
                        notes.Add(new Note("image", Named(zpl, t), LineOf(zpl, t.Start)));
                    break;

                default:
                    if (t.Command == "MU")
                    {
                        // ^MUa: only I (inches) and M (millimetres) take the
                        // coordinates out of dots. Everything else is dots — U is
                        // what the manual names, but printers accept other letters
                        // for it (GLS writes ^MUD) and the renderer reads them all
                        // as dots. The two have to agree, or this refuses to move
                        // coordinates the renderer has already placed in dots and
                        // leaves the label turned in name only.
                        var unit = parts.Count > 0 ? parts[0].Text.Trim().ToUpperInvariant() : "";
                        inDots = unit.Length == 0 || (unit[0] != 'I' && unit[0] != 'M');
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
                        NoteFont(t.Command.Length > 1 ? t.Command[1].ToString() : "", At(1));
                        if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: false);
                        if (scaling) { Write(edits, parts, 1, At(1) * ratio, 1); Write(edits, parts, 2, At(2) * ratio, 0); }
                    }
                    else if (HeightAt.TryGetValue(t.Command, out int at))
                    {
                        // Every 1D symbology is built from the narrow bar ^BY
                        // sets. Without one it is the printer's own two dots, and
                        // two dots are a different width at another density - so
                        // the default has to be written down to be converted.
                        if (!sawModuleWidth && !Modules.Contains(t.Command)) needsModuleWidth = true;
                        if (rot != 0) TurnLetter(edits, parts, 0, rot, insert: false);
                        if (scaling)
                        {
                            if (Modules.Contains(t.Command)) NoteModule(t, At(at));
                            Write(edits, parts, at, At(at) * ratio,
                                  Modules.Contains(t.Command) ? ModuleFloor(At(at)) : 1);
                        }
                    }
                    break;
            }
        }

        // Nothing said which way the fields that never said so should read. One
        // ^FW at the top of the label says it once, for all of them.
        if (rot != 0 && !sawFieldWidth && block.OpenEnd > 0)
            OpenWith(zpl, block, "^FW" + Advance('N', rot), edits);

        // The printer's own narrow bar is two dots. Converted, those are two dots
        // of a different size - so a label that never said ^BY is handed the one
        // it was relying on, in the new dots.
        if (scaling && needsModuleWidth && block.OpenEnd > 0)
            OpenWith(zpl, block,
                "^BY" + Whole(Math.Max(ModuleFloor(DefaultModuleWidth), DefaultModuleWidth * ratio))
                    .ToString(CultureInfo.InvariantCulture),
                edits);
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

    /// <summary>The narrow bar a printer uses when the label never names one.</summary>
    private const double DefaultModuleWidth = 2;

    // Writes a command in at the top of the label, on its own line where ^XA had
    // one, so the document keeps the shape it was written in rather than growing
    // a command onto the end of a line.
    private static void OpenWith(string zpl, Block block, string command, List<ZplPatcher.Edit> edits)
    {
        int at = block.OpenEnd;
        var line = command;
        if (at + 1 < zpl.Length && zpl[at] == '\r' && zpl[at + 1] == '\n')
        { at += 2; line += "\r\n"; }
        else if (at < zpl.Length && zpl[at] == '\n')
        { at += 1; line += "\n"; }
        edits.Add(new ZplPatcher.Edit(at, at, line));
    }

    // The symbologies whose number at that index is a MODULE or a magnification —
    // a multiplier the whole symbol is built from — rather than a plain height.
    private static readonly HashSet<string> Modules = new(StringComparer.Ordinal)
    {
        "BX", "BQ", "BO", "B0",
    };

    // How small a module may become.
    //
    // Rounding a module to the nearest whole dot is the faithful choice, but it has
    // a floor that has nothing to do with faithfulness: below about two dots the
    // bars stop being readable, and a barcode nobody can scan is worse than one a
    // tenth off its size. Converting 8 to 6 dots/mm halves a ^BY2 to one dot — a
    // sixth of a millimetre — which no scanner is expected to manage.
    //
    // The floor is two dots, EXCEPT where the label was already finer than that:
    // a ^BY1 is a deliberate choice on a dense printer, and doubling it would be
    // us redesigning the label rather than converting it.
    private static double ModuleFloor(double original)
        => original <= 0 ? 1 : Math.Min(2, Math.Max(1, original));

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
            long missing = Whole(min == double.MinValue ? value : Math.Max(min, value));
            if (missing == 0) return;
            var tail = parts[^1];
            edits.Add(new ZplPatcher.Edit(tail.End, tail.End,
                new string(',', index - parts.Count + 1) + missing.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        var part = parts[index];
        double clamped = min == double.MinValue ? value : Math.Max(min, value);
        long rounded = Whole(clamped);
        var text = rounded.ToString(CultureInfo.InvariantCulture);

        if (Numeric(part) is { } n) { edits.Add(new ZplPatcher.Edit(n.Start, n.End, text)); return; }

        // An argument written as NOTHING — the empty slot in ^BCN,,Y,N — says the
        // same as one left off the end: use the default. That default is carried by
        // another command which is converted with everything else (a bar height
        // comes from ^BY), so writing a number here would override a default that
        // has already followed. And the number to hand is zero scaled up, which is
        // a barcode one dot tall — which is exactly what came out.
        if (fill && rounded != 0) edits.Add(new ZplPatcher.Edit(part.End, part.End, text));
    }

    // ZPL counts in whole dots, so every converted length has to land on one. The
    // nearest is the faithful choice; the question is what to do with a half.
    //
    // It is not a detail. Most of these numbers are lengths, where half a dot is
    // half a dot — but a few are MULTIPLIERS, and there the half is multiplied with
    // them. ^BY3 at 8 dots/mm is 4.5 at twelve, and a Code 128 is some two hundred
    // modules wide: the two answers are a barcode a tenth wider or a tenth narrower
    // than the one that was there, and neither is the one that was there.
    //
    // So the caller decides, and the window says which way it went. UP is the
    // default because a module width is quoted as a MINIMUM — going over it stays
    // within the specification, going under can fall out of it.
    // Set once at the top of Build and read all the way down. Marked per-thread so
    // two conversions could never read each other's answer.
    [ThreadStatic] private static bool _roundUp;

    private static long Whole(double value)
        => (long)(_roundUp ? Math.Floor(value + 0.5) : Math.Ceiling(value - 0.5));

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
