using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;

namespace Ultimate_ZPL_Viewer;

public sealed record DpmmOption(double Dpmm)
{
    public int Dpi => (int)Math.Round(Dpmm * 25.4);
    public string Label => $"{Format(Dpmm)} dpmm ({Dpi} dpi)";

    private static string Format(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.0001
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

public sealed record LabelSize(double WidthDots, double HeightDots)
{
    public double WidthMm(double dpmm) => WidthDots / dpmm;
    public double HeightMm(double dpmm) => HeightDots / dpmm;
}

public abstract record ZplDrawable(double X, double Y)
{
    // The span, in the original ZPL text, of the field this element came from —
    // from its ^FO/^FT through its ^FS. Set once the field commits; -1 means the
    // element could not be traced back (nothing selectable). End is exclusive.
    public int SourceStart { get; set; } = -1;
    public int SourceEnd { get; set; } = -1;
}
// X,Y = ZPL field origin. Rotation in degrees (0/90/180/270). Baseline = true for
// ^FT (origin is the text baseline), false for ^FO (origin is the top-left).
public sealed record ZplText(double X, double Y, string Text, double Height, double Width, string Font, bool Bold, bool Reverse, int Rotation, bool Baseline) : ZplDrawable(X, Y);
public sealed record ZplBox(double X, double Y, double Width, double Height, double Thickness, bool Reverse, bool WhiteFill = false) : ZplDrawable(X, Y);
public sealed record ZplLine(double X, double Y, double Width, double Height, double Thickness) : ZplDrawable(X, Y);
// Circle (^GC) / ellipse (^GE) outline; Thickness >= min(W,H)/2 means filled.
public sealed record ZplEllipse(double X, double Y, double Width, double Height, double Thickness) : ZplDrawable(X, Y);
// ^GS symbol: Code is 'A'=®, 'B'=©, 'C'=™, 'D'=UL mark, 'E'=CSA mark.
public sealed record ZplSymbol(double X, double Y, double Height, double Width, char Code) : ZplDrawable(X, Y);

// A pre-rendered 1D barcode: black segments + human-readable labels, in a local
// box of Width×Height dots. X,Y anchor the ROTATED bounding box's top-left corner
// (the ^FO/^FT rule Labelary uses); Rotation is 0/90/180/270.
public sealed record BarSeg(double X, double Y, double W, double H);
// CenterWidth > 0 → the text is centered within [X, X+CenterWidth].
public sealed record BarLabel(double X, double Y, string Text, double FontHeight, double CenterWidth = 0);
public sealed record ZplBars(double X, double Y, double Width, double Height, IReadOnlyList<BarSeg> Segs, IReadOnlyList<BarLabel> Labels, int Rotation) : ZplDrawable(X, Y);
// A rectangular 2D module grid with independent module width/height (PDF417).
public sealed record ZplGrid(double X, double Y, double ModW, double ModH, bool[,] Matrix) : ZplDrawable(X, Y);
// Monochrome bitmap from ^GF/^GFA (1 bit per pixel, row-padded to whole bytes).
public sealed record ZplImage(double X, double Y, int PixelWidth, int PixelHeight, byte[] Bits) : ZplDrawable(X, Y);
// Placeholder box for 2D symbologies we don't render pixel-exactly yet
// (Data Matrix, QR) — drawn as a framed square so the layout still reads right.
public sealed record ZplMatrix(double X, double Y, double Size) : ZplDrawable(X, Y);
// A real Aztec code (^BO / ^B0): Matrix[row, col] = true for a black module,
// each module ModuleSize dots square.
public sealed record ZplAztec(double X, double Y, double ModuleSize, bool[,] Matrix) : ZplDrawable(X, Y);
// A real Data Matrix (^BX): Matrix[row, col] = true for a black module.
public sealed record ZplDataMatrix(double X, double Y, double ModuleSize, bool[,] Matrix) : ZplDrawable(X, Y);

public sealed class ZplRenderModel
{
    public double? DeclaredDpmm { get; init; }
    public LabelSize Size { get; init; } = new(812, 1218);
    public IReadOnlyList<ZplDrawable> Drawables { get; init; } = Array.Empty<ZplDrawable>();

    // ^POI: the whole label prints upside-down (180° rotation of the content).
    public bool InvertOrientation { get; init; }

    // ^PMY: the whole label prints mirrored (flipped left ↔ right).
    public bool MirrorImage { get; init; }

    // ^PW / ^LL values found in the ZPL (null when the command is absent).
    public double? DeclaredWidthDots { get; init; }
    public double? DeclaredHeightDots { get; init; }

    // Size computed from the elements' bounding box (fallback when ^PW/^LL are absent).
    public double ContentWidthDots { get; init; }
    public double ContentHeightDots { get; init; }
}

public static partial class ZplRenderer
{
    private const double DefaultLabelWidthInches = 4d;
    private const double DefaultLabelHeightInches = 6d;

    public static ZplRenderModel Parse(string zpl, double fallbackDpmm)
    {
        var src = zpl ?? string.Empty;
        var x = 0d;
        var y = 0d;
        // Power-on default: font A at its 9x5 cell, exactly what a printer (and the
        // reference renderer) uses for a ^FD that never selected a font. ^CF moves this
        // default; ^A only dresses the field it belongs to, so `font` falls back here at
        // every ^FS.
        var defaultFont = MakeFont("A", 9, 5);
        var font = defaultFont;
        var orientation = 0;        // current font rotation, degrees
        var typeset = false;        // ^FT (baseline origin) vs ^FO (top-left origin)
        var lhX = 0d;
        var lhY = 0d;               // ^LH label home offset
        var moduleWidth = 2d;
        var barcodeHeight = 100d;
        var pendingBarcode = false;
        var pending2D = false;
        var pendingAztec = false;
        var aztecMag = 1d;
        var pendingDataMatrix = false;
        var dmModuleSize = 3d;
        var dmForcedSize = 0;       // ^BX columns param → force a symbol size
        var pendingQr = false;
        var qrMag = 3d;
        var pendingPdf417 = false;
        var p417RowH = 8d;
        var p417Cols = 0;
        var p417Rows = 0;
        var p417Sec = -1;
        var pendingSym = "";        // 1D symbology awaiting ^FD: "39", "2of5", "E13", "UPA", "E8"
        var wideRatio = 3d;         // ^BY wide:narrow ratio (Code39 / I2of5)
        var symCheck = false;       // Code39 mod-43 / I2of5 mod-10 check digit
        var cbStart = 'A';          // ^BK Codabar start/stop characters
        var cbStop = 'A';
        var msiCheck = 'B';         // ^BM check digit scheme (A none, B/C/D)
        var hrtAbove = false;       // ^Bx parameter g: interpretation line above the symbol
        var pendingMaxiCode = false; // ^BD awaiting ^FD
        var pendingGs = false;      // ^GS symbol awaiting ^FD
        var gsH = 0d; var gsW = 0d;
        var swallowFd = false;      // ^RF / ^BD: the next ^FD is data, not content
        var barcodeRotation = 0;    // orientation char of the ^Bx command
        var bcMode = 'N';           // ^BC mode param (A = automatic subset optimization)
        var showBarcodeText = true;
        var reverse = false;
        var fieldHex = false;
        var hexIndicator = '_';
        // ^FB field block: width, max lines, extra line spacing, justification.
        var fbActive = false;
        var fbWidth = 0d;
        var fbLines = 1;
        var fbSpacing = 0d;
        var fbJust = 'L';
        var width = 0d;
        var height = 0d;
        var ltY = 0d;               // ^LT label top shift (can be negative)
        var lsX = 0d;               // ^LS label shift (horizontal)
        var poi = false;            // ^POI inverted orientation
        var mirror = false;         // ^PMY mirror image (whole label flipped left/right)
        var labelReverse = false;   // ^LRY reverse print: every field behaves like ^FR
        var fwOrient = 0;           // ^FW default field orientation (fields may override)
        var unitScale = 1d;         // ^MU: dots per unit of the coordinates that follow
        var fpVertical = false;     // ^FPV: characters stacked downwards instead of a line
        var fpGap = 0d;             // ^FP additional space between characters
        // ^CW font aliases (^CWZ,E:FONT.TTF): the letter names a downloaded scalable
        // font, so it must NOT be quantised like a built-in bitmap cell.
        var fontAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double? dpmm = null;
        var drawables = new List<ZplDrawable>();
        var blackBoxes = new List<ZplRect>(); // filled black ^GB boxes, for ^FR text color
        var storedGraphics = new Dictionary<string, (int W, int H, byte[] Bits)>(StringComparer.OrdinalIgnoreCase);
        var stop = false;

        // A field only prints once its ^FS arrives: elements accumulate in fieldBuf
        // and are committed on ^FS. A new ^FO/^FT (or end of label) before the ^FS
        // abandons the un-terminated field, exactly like real printers/Labelary.
        var fieldBuf = new List<ZplDrawable>();
        var fieldBlackBoxes = new List<ZplRect>();
        // Span of the field being built, so the elements it commits can point back
        // at the code that produced them (-1 = not started).
        var fieldStart = -1;
        var fieldEnd = -1;
        var growBuf = new List<(double X, double Y)>();
        var maxXCommitted = 0d; var maxYCommitted = 0d;

        void Grow(double gx, double gy) { growBuf.Add((gx, gy)); }
        // The end of a field span lands after the trailing whitespace, because the
        // last token's arguments run up to the next ^ or ~ - the line break included.
        int TrimmedEnd()
        {
            var end = Math.Min(fieldEnd, src.Length);
            while (end > fieldStart && char.IsWhiteSpace(src[end - 1])) end--;
            return end;
        }
        void CommitField()
        {
            var end = fieldStart >= 0 ? TrimmedEnd() : fieldEnd;
            foreach (var d in fieldBuf) { d.SourceStart = fieldStart; d.SourceEnd = end; }
            drawables.AddRange(fieldBuf);
            blackBoxes.AddRange(fieldBlackBoxes);
            foreach (var (gx, gy) in growBuf)
            {
                if (gx > maxXCommitted) maxXCommitted = gx;
                if (gy > maxYCommitted) maxYCommitted = gy;
            }
            AbandonField();
        }
        void AbandonField()
        {
            fieldBuf.Clear();
            fieldBlackBoxes.Clear();
            growBuf.Clear();
            fieldStart = -1;
            fieldEnd = -1;
        }

        // Emits a text field (with ^FB word-wrap, justification, bitmap-font quirks).
        void EmitTextField(string rawData)
        {
            var data = rawData;
            if (string.IsNullOrEmpty(data)) return;
            double fx = x + lhX + lsX, fy = y + lhY + ltY;

            // ^FO anchors the top of the character CELL, and the bitmap faces leave a
            // blank band above their capitals inside it, so the ink starts lower. A
            // ^FB block anchors its first line directly (measured: no band).
            if (!typeset && !fbActive) fy += font.TopGap;

            // The Zebra bitmap font B only has uppercase glyphs on real printers.
            if (font.Name.Equals("B", StringComparison.OrdinalIgnoreCase))
                data = data.ToUpperInvariant();

            // Effective glyph width. Bitmap fonts have a non-square base cell, so their
            // "natural" ZPL width is height×(baseW/baseH); the mono face we substitute
            // already has that aspect, so the render scale is width / natural — passed
            // to the drawables as an equivalent width against the height.
            var (cellH, cellW) = ZplFont.BaseCell(font.Name);
            double natural = font.Height * (cellW / cellH);
            double effWidth = natural > 0 && font.Width > 0 ? font.Width / (cellW / cellH) : font.Height;
            double condenseW = effWidth / Math.Max(1, font.Height);

            if (font.Name == "0")
            {
                // Leading spaces are alignment padding (e.g. "          D-B2C"): render
                // them at the Zebra nominal space width via an origin offset.
                if (!fbActive && orientation == 0)
                {
                    int lead = 0;
                    while (lead < data.Length && data[lead] == ' ') lead++;
                    if (lead > 0) { fx += lead * ZebraSpaceEmRatio * font.Height; data = data[lead..]; }
                }
            }

            // Font 0 and the P–V faces are the same Zebra typeface (CG Triumvirate Bold
            // Condensed), whose hyphen is a long, thick, vertically-centred dash; a font
            // glyph is too short and light.
            if (font.Name == "0" || ZplFont.IsTriumvirate(font.Name))
            {
                // For horizontal fields, draw each '-' as a solid bar between text
                // segments (matches Labelary's "F — 52200" and Chronopost's
                // "FR — CHR — 0437 — JAG1"). Rotated or block text keeps the
                // spaced-hyphen approximation.
                if (!fbActive && orientation == 0 && data.IndexOf('-') >= 0)
                {
                    double h = font.Height;
                    // Labelary metrics (h=62 ref): bar 36 wide (0.58h), 7 thick (0.11h),
                    // 13-14 dot gaps. The gap is set slightly under the measured 0.21h
                    // because our text advances run ~3 % wider than Labelary's: 0.15h
                    // keeps the LAST dash aligned (DPD "FR-DPD-1021-" must not enter the
                    // black "021" box at x564). Vertically the bar sits 0.31h above the
                    // baseline (^FT anchor) — i.e. 0.42h under the ^FO cap-line anchor
                    // (measured on both faces: font 0 at h30/h62, fonts Q and V).
                    double dGap = 0.15 * h, barW = 0.58 * h, barThick = Math.Max(2, 0.11 * h);
                    double barCenter = typeset ? fy - 0.31 * h : fy + 0.42 * h;
                    double barTop = barCenter - barThick / 2;
                    var segs = data.Split('-');
                    double cx = fx;
                    for (int i = 0; i < segs.Length; i++)
                    {
                        if (segs[i].Length > 0)
                        {
                            fieldBuf.Add(new ZplText(cx, fy, segs[i], h, effWidth, font.Family, font.Bold, reverse, orientation, typeset));
                            cx += MeasureTextWidth(segs[i], font) * condenseW;
                        }
                        if (i < segs.Length - 1)
                        {
                            cx += dGap;
                            fieldBuf.Add(new ZplBox(cx, barTop, barW, barThick, barThick, false, false));
                            cx += barW + dGap;
                        }
                    }
                    Grow(cx, fy + h);
                    return;
                }
                data = data.Replace("-", " - ", StringComparison.Ordinal);
            }

            if (fbActive && fbWidth > 0 && orientation == 0)
            {
                // ^FB: word-wrap into at most fbLines lines of fbWidth dots. With ^FT
                // the baseline of the FIRST line sits (maxLines-1) line-heights above
                // the anchor (the block is top-justified in its reserved band).
                double lineHeight = font.Height + fbSpacing;
                var lines = WrapFieldBlock(data, font, condenseW, fbWidth, fbLines);
                double startY = typeset ? fy - (fbLines - 1) * lineHeight : fy;
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];
                    double ly = startY + i * lineHeight;
                    double lw = MeasureTextWidth(line, font) * condenseW;
                    if (fbJust == 'J' && i < lines.Count - 1 && line.Contains(' '))
                    {
                        // Justified: spread the leftover width between the words.
                        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        double wordsW = words.Sum(wd => MeasureTextWidth(wd, font)) * condenseW;
                        double gap = words.Length > 1 ? (fbWidth - wordsW) / (words.Length - 1) : 0;
                        double wx = fx;
                        foreach (var word in words)
                        {
                            fieldBuf.Add(new ZplText(wx, ly, word, font.Height, effWidth, font.Family, font.Bold, false, 0, typeset));
                            wx += MeasureTextWidth(word, font) * condenseW + gap;
                        }
                    }
                    else
                    {
                        double lx = fbJust switch
                        {
                            'C' => fx + (fbWidth - lw) / 2,
                            'R' => fx + (fbWidth - lw),
                            _   => fx,
                        };
                        fieldBuf.Add(new ZplText(lx, ly, line, font.Height, effWidth, font.Family, font.Bold, false, 0, typeset));
                    }
                    Grow(fx + fbWidth, ly + font.Height);
                }
                return;
            }

            // ^FPV: the characters are printed one under the other instead of on a line.
            if (fpVertical && orientation == 0 && !fbActive)
            {
                double step = font.Height + fpGap;
                double cy = fy;
                foreach (var ch in data)
                {
                    if (ch != ' ')
                        fieldBuf.Add(new ZplText(fx, cy, ch.ToString(), font.Height, effWidth,
                            font.Family, font.Bold, reverse, 0, typeset));
                    cy += step;
                }
                Grow(fx + MeasureTextWidth("W", font) * condenseW, cy);
                return;
            }

            double txX = fx, txY = fy;
            if (fbActive && fbWidth > 0 && orientation != 0)
            {
                // Rotated field block (e.g. GLS FlexDeliveryService): the lines stack
                // perpendicular to the reading direction.
                double lineHeight = font.Height + fbSpacing;
                double lineShift = (fbLines - 1) * lineHeight;
                if (orientation == 90) txX = fx + lineShift;
                else if (orientation == 270) txX = fx - lineShift;
            }

            // ^FR text prints white only over a solid black box, otherwise black.
            bool textReverse = reverse;
            if (reverse && orientation == 0)
            {
                double sampleY = typeset ? txY - font.Height * 0.4 : txY + font.Height * 0.4;
                bool over(ZplRect r) => txX >= r.X && txX <= r.Right && sampleY >= r.Y && sampleY <= r.Bottom;
                textReverse = blackBoxes.Any(over) || fieldBlackBoxes.Any(over);
            }

            fieldBuf.Add(new ZplText(txX, txY, data, font.Height, effWidth, font.Family, font.Bold, textReverse, orientation, typeset));
            var tw = MeasureTextWidth(data, font) * condenseW;
            if (orientation is 90 or 270) Grow(txX + font.Height, txY + tw);
            else Grow(txX + tw, txY + font.Height);
        }

        // Emits a 1D barcode (bars + human-readable text) as a ZplBars drawable.
        void Emit1DBarcode(string sym, string data, double fx, double fy)
        {
            // Interpretation line height. Calibrated on the reference by WIDTH, which is
            // what matters for the layout: at ^BY3 an 8-character line is 137 dots wide
            // there and 139 here. The reference's own face is more condensed, so it also
            // stands ~5 dots taller — enlarging ours to match that would blow the width
            // out by half, so the width wins.
            double hrtH = Math.Max(20, moduleWidth * 10);
            List<BarSeg> segs = new();
            List<BarLabel> labels = new();
            double W = 0, H;

            void SegsFromRuns(IReadOnlyList<BarcodeRun> runs)
            {
                double cursor = 0;
                foreach (var run in runs)
                {
                    double rw = run.Width * moduleWidth;
                    if (run.Black) segs.Add(new BarSeg(cursor, 0, Math.Max(1, rw), barcodeHeight));
                    cursor += rw;
                }
                W = cursor;
            }

            string hrt = "";
            switch (sym)
            {
                case "128":
                    SegsFromRuns(BuildCode128Bars(data, bcMode == 'A'));
                    hrt = StripCode128Invocations(data);
                    break;
                case "39":
                case "logmars":
                {
                    var (runs, text) = BuildCode39(data, symCheck, wideRatio);
                    SegsFromRuns(runs);
                    // LOGMARS leaves the '*' delimiters out of the printed line.
                    hrt = sym == "logmars" ? text.Trim('*') : text;
                    break;
                }
                case "2of5":
                {
                    var (runs, text) = BuildI2of5(data, symCheck, wideRatio);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "E13":
                case "UPA":
                case "E8":
                case "UPE":
                case "addon":
                    BuildEanUpc(sym, data, moduleWidth, barcodeHeight, hrtH, showBarcodeText, segs, labels, out W);
                    break;
                case "93":
                {
                    var (runs, text) = Barcode1D.BuildCode93(data);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "codabar":
                {
                    var (runs, text) = Barcode1D.BuildCodabar(data, cbStart, cbStop, wideRatio);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "11":
                {
                    var (runs, text) = Barcode1D.BuildCode11(data, symCheck, wideRatio);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "msi":
                {
                    var (runs, text) = Barcode1D.BuildMsi(data, msiCheck, wideRatio);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "plessey":
                {
                    var (runs, text) = Barcode1D.BuildPlessey(data);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "ind25":
                case "std25":
                {
                    var (runs, text) = Barcode1D.Build2of5(data, sym == "ind25", wideRatio);
                    SegsFromRuns(runs);
                    hrt = text;
                    break;
                }
                case "postnet":
                case "planet":
                    segs.AddRange(Barcode1D.BuildPostnet(data, sym == "planet", moduleWidth, barcodeHeight, out W));
                    break;
            }

            bool centeredHrt = sym is "128" or "39" or "logmars" or "2of5" or "93" or "codabar"
                or "11" or "msi" or "plessey" or "ind25" or "std25" or "postnet" or "planet";
            if (sym is "postnet" or "planet") hrt = new string(data.Where(char.IsDigit).ToArray());
            if (showBarcodeText && centeredHrt && hrt.Length > 0)
            {
                // ^Bx parameter g puts the interpretation line ABOVE the symbol
                // (always the case for LOGMARS), otherwise it hangs underneath.
                double labelY = hrtAbove ? -hrtH : barcodeHeight - moduleWidth;
                labels.Add(new BarLabel(0, labelY, hrt, hrtH, W));
            }

            H = barcodeHeight + (showBarcodeText ? hrtH + 2 : 0);
            double bw = barcodeRotation is 90 or 270 ? H : W;
            double bh = barcodeRotation is 90 or 270 ? W : H;
            // ^FT anchors the bottom of the BARS at the baseline; the interpretation
            // line overflows BELOW the anchor (Labelary: ^FT35,1091 + h40 + HRT =>
            // bars 1051..1091, digits underneath). Using the full block height here
            // lifted the bars into the text above (MOR "Réf Client" overlap bug).
            double anchorH = barcodeRotation is 90 or 270 ? W : barcodeHeight;
            double topY = typeset ? fy - anchorH : fy;
            fieldBuf.Add(new ZplBars(fx, topY, W, H, segs, labels, barcodeRotation));
            // Some interpretation digits sit OUTSIDE the symbol (the UPC-A/UPC-E number
            // system and check digits): without them the auto-sized label ends at the
            // last bar and clips the trailing digit away.
            foreach (var l in labels)
            {
                double right = l.X + (l.CenterWidth > 0 ? l.CenterWidth : l.Text.Length * 0.6 * l.FontHeight);
                if (barcodeRotation == 0 && right > bw) bw = right;
            }
            Grow(fx + bw, topY + bh);
        }

        foreach (var token in ExpandStoredFormats(Tokenize(src)))
        {
            if (stop) break;
            var command = token.Command;
            var args = token.Args.Trim();

            // Any two-letter ^Ax command is a font selection (^AA…^AH, ^A0…).
            if (command.Length == 2 && command[0] == 'A')
            {
                (font, orientation) = ParseFieldFont(command, args, font, fwOrient, fontAliases, unitScale);
                continue;
            }

            switch (command)
            {
                case "JM":
                    dpmm = ParseDpmm(args);
                    break;
                case "PW":
                    width = Positive(ParseFirstNumber(args) * unitScale, width);
                    break;
                case "LL":
                    height = Positive(ParseFirstNumber(args) * unitScale, height);
                    break;
                case "LH":
                    var lh = ParseNumbers(args).ToArray();
                    if (lh.Length > 0) lhX = lh[0] * unitScale;
                    if (lh.Length > 1) lhY = lh[1] * unitScale;
                    break;
                case "FO":
                {
                    AbandonField(); // an unterminated previous field never prints
                    fieldStart = token.Start; fieldEnd = token.End;
                    var o = ParseNumbers(args).ToArray();
                    if (o.Length > 0) x = o[0] * unitScale;
                    if (o.Length > 1) y = o[1] * unitScale;
                    typeset = false;
                    break;
                }
                case "FT":
                {
                    AbandonField();
                    fieldStart = token.Start; fieldEnd = token.End;
                    var o = ParseNumbers(args).ToArray();
                    if (o.Length > 0) x = o[0] * unitScale;
                    if (o.Length > 1) y = o[1] * unitScale;
                    typeset = true;
                    break;
                }
                case "CF":
                    defaultFont = ParseDefaultFont(args, defaultFont, fontAliases, unitScale);
                    font = defaultFont;
                    break;
                case "A":
                    (font, orientation) = ParseFieldFont(command, args, font, fwOrient, fontAliases, unitScale);
                    break;
                case "FW":
                    // Default field orientation for the ^A / ^B / ^GB that follow; a
                    // field that states its own orientation still wins.
                    fwOrient = args.Length > 0 ? OrientationDegrees(args[0]) : 0;
                    orientation = fwOrient;
                    break;
                case "LR":
                    // ^LRY: every field prints in reverse — exactly the ^FR rule, but
                    // as the default rather than per field.
                    labelReverse = args.StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                    reverse = labelReverse;
                    break;
                case "PM":
                    mirror = args.StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                    break;
                case "MU":
                {
                    // ^MUa,b,c — a = U (dots), I (inches) or M (millimetres): the unit
                    // every coordinate that follows is expressed in.
                    var mu = args.Length > 0 ? char.ToUpperInvariant(args[0]) : 'U';
                    var dots = dpmm ?? fallbackDpmm;
                    unitScale = mu switch { 'I' => dots * 25.4, 'M' => dots, _ => 1d };
                    break;
                }
                case "FP":
                {
                    // ^FPd,g — d = H (a line, default) or V (characters stacked down);
                    // g = extra space between characters.
                    var fp = args.Split(',');
                    fpVertical = fp.Length > 0 && fp[0].Trim().StartsWith("V", StringComparison.OrdinalIgnoreCase);
                    fpGap = fp.Length > 1 && double.TryParse(fp[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fpg) ? fpg : 0;
                    break;
                }
                case "CW":
                {
                    // ^CWa,device:font.ttf — the letter now names a downloaded scalable
                    // font, so ^Aa must not be quantised to a built-in bitmap cell.
                    var cw = args.Split(',');
                    if (cw.Length > 0 && cw[0].Trim().Length > 0) fontAliases.Add(cw[0].Trim());
                    break;
                }
                case "SF":
                    // Serialization: the ^FD value already emitted is the first in the
                    // series, which is exactly what a preview shows.
                    break;
                case "LT":
                    ltY = ParseNumbers(args).FirstOrDefault();
                    break;
                case "LS":
                    lsX = ParseNumbers(args).FirstOrDefault();
                    break;
                case "PO":
                    // ^POI = print orientation inverted (whole label upside down).
                    poi = args.StartsWith("I", StringComparison.OrdinalIgnoreCase);
                    break;
                case "RF": // RFID write: the following ^FD is tag data, not visual content
                    swallowFd = true;
                    break;
                case "BD":
                    // MaxiCode. Its module placement is a normative table we do not have,
                    // so no scannable symbol can be produced; the reference itself only
                    // draws one when the mode parameter is valid. Swallow the data rather
                    // than print it as text.
                    swallowFd = true;
                    break;
                case "XZ":
                    // Multi-label streams: like Labelary, only the first label that
                    // produced content is previewed.
                    AbandonField();
                    if (drawables.Count > 0) stop = true;
                    break;
                case "DG":
                {
                    // ~DGd:name.ext,totalBytes,bytesPerRow,data — store for ^XG recall.
                    var dg = args.Split(new[] { ',' }, 4);
                    if (dg.Length == 4
                        && int.TryParse(dg[1].Trim(), out int dgTotal) && dgTotal > 0
                        && int.TryParse(dg[2].Trim(), out int dgRow) && dgRow > 0)
                    {
                        var bits = DecodeAcsHex(dg[3], dgTotal, dgRow);
                        storedGraphics[FormatName(dg[0])] = (dgRow * 8, dgTotal / dgRow, bits);
                    }
                    break;
                }
                case "DY":
                {
                    // ~DYd:f,b,x,t,w,data — download an object. Only the bitmap forms
                    // are visual: .GRF carries the same 1bpp rows as ~DG (hex, or the
                    // :Z64:/:B64: wrappers ^GFA also accepts).
                    var dy = args.Split(new[] { ',' }, 6);
                    if (dy.Length == 6
                        && int.TryParse(dy[3].Trim(), out int dyTotal) && dyTotal > 0
                        && int.TryParse(dy[4].Trim(), out int dyRow) && dyRow > 0)
                    {
                        var payload = dy[5];
                        var bits = payload.Contains(":Z64:", StringComparison.OrdinalIgnoreCase)
                                || payload.Contains(":B64:", StringComparison.OrdinalIgnoreCase)
                            ? DecodeBase64Graphic(payload, dyTotal)
                            : DecodeAcsHex(payload, dyTotal, dyRow);
                        if (bits is not null)
                            storedGraphics[FormatName(dy[0])] = (dyRow * 8, dyTotal / dyRow, bits);
                    }
                    break;
                }
                case "IM": // ^IMd:f.x — recall a stored image at the field origin
                case "IL": // ^ILd:f.x — same, as the label background
                {
                    if (storedGraphics.TryGetValue(FormatName(args), out var img))
                    {
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        double topY = typeset ? fy - img.H : fy;
                        // ^IL paints the background: it always starts at the origin.
                        if (command == "IL") { fx = lhX + lsX; topY = lhY + ltY; }
                        fieldBuf.Add(new ZplImage(fx, topY, img.W, img.H, img.Bits));
                        Grow(fx + img.W, topY + img.H);
                        CommitField(); // self-contained, like ^GF
                    }
                    break;
                }
                case "ID": // delete an object from storage — nothing to draw
                case "IS": // save the label format/image — nothing to draw
                    break;
                case "XG":
                {
                    // ^XGd:name.ext,mx,my — recall a stored graphic magnified mx×my.
                    var xg = args.Split(',');
                    if (xg.Length > 0 && storedGraphics.TryGetValue(FormatName(xg[0]), out var g))
                    {
                        int mx = xg.Length > 1 && int.TryParse(xg[1].Trim(), out var m1) ? Math.Max(1, m1) : 1;
                        int my = xg.Length > 2 && int.TryParse(xg[2].Trim(), out var m2) ? Math.Max(1, m2) : 1;
                        var (sw, sh, sbits) = ScaleBits(g.W, g.H, g.Bits, mx, my);
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        double topY = typeset ? fy - sh : fy;
                        fieldBuf.Add(new ZplImage(fx, topY, sw, sh, sbits));
                        Grow(fx + sw, topY + sh);
                    }
                    break;
                }
                case "BY":
                {
                    // Split on ',' by POSITION (not ParseNumbers) so an empty ratio field
                    // doesn't shift the height: ^BY3,,150 = width 3, default ratio, height 150.
                    var by = args.Split(',');
                    if (by.Length > 0 && double.TryParse(by[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var byw)) moduleWidth = Math.Max(1, byw * unitScale);
                    if (by.Length > 1 && double.TryParse(by[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var byr) && byr >= 2) wideRatio = Math.Min(3, byr);
                    if (by.Length > 2 && double.TryParse(by[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var byh)) barcodeHeight = Math.Max(20, byh * unitScale);
                    break;
                }
                case "BC":
                {
                    // ^BCo,h,f,g,e,m — o orientation, h height, f = print HRT, m = mode.
                    var bc = ParseNumbers(args).ToArray();
                    if (bc.Length > 0) barcodeHeight = Math.Max(20, bc[0]);
                    showBarcodeText = ParseBarcodeTextFlag(args);
                    barcodeRotation = BarcodeOrientation(args, fwOrient);
                    var bcParts = args.Split(',');
                    hrtAbove = bcParts.Length > 3 && string.Equals(bcParts[3].Trim(), "Y", StringComparison.OrdinalIgnoreCase);
                    bcMode = bcParts.Length > 5 && bcParts[5].Trim().Length > 0
                        ? char.ToUpperInvariant(bcParts[5].Trim()[0]) : 'N';
                    pendingBarcode = true;
                    break;
                }
                case "B3": // Code 39: ^B3o,e(mod43),h,f,g
                {
                    var b3 = args.Split(',');
                    barcodeRotation = BarcodeOrientation(args, fwOrient);
                    symCheck = b3.Length > 1 && b3[1].Trim().ToUpperInvariant() == "Y";
                    if (b3.Length > 2 && double.TryParse(b3[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b3h)) barcodeHeight = Math.Max(20, b3h);
                    showBarcodeText = b3.Length < 4 || !string.Equals(b3[3].Trim(), "N", StringComparison.OrdinalIgnoreCase);
                    hrtAbove = b3.Length > 4 && string.Equals(b3[4].Trim(), "Y", StringComparison.OrdinalIgnoreCase);
                    pendingSym = "39";
                    break;
                }
                case "B2": // Interleaved 2 of 5: ^B2o,h,f,g,e(mod10)
                {
                    var b2 = args.Split(',');
                    barcodeRotation = BarcodeOrientation(args, fwOrient);
                    if (b2.Length > 1 && double.TryParse(b2[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b2h)) barcodeHeight = Math.Max(20, b2h);
                    showBarcodeText = b2.Length < 3 || !string.Equals(b2[2].Trim(), "N", StringComparison.OrdinalIgnoreCase);
                    symCheck = b2.Length > 4 && b2[4].Trim().ToUpperInvariant() == "Y";
                    pendingSym = "2of5";
                    break;
                }
                case "BE": // EAN-13
                case "BU": // UPC-A
                case "B8": // EAN-8
                {
                    var be = args.Split(',');
                    barcodeRotation = BarcodeOrientation(args, fwOrient);
                    if (be.Length > 1 && double.TryParse(be[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var beh)) barcodeHeight = Math.Max(20, beh);
                    showBarcodeText = be.Length < 3 || !string.Equals(be[2].Trim(), "N", StringComparison.OrdinalIgnoreCase);
                    pendingSym = command == "BE" ? "E13" : command == "BU" ? "UPA" : "E8";
                    break;
                }
                case "BX": // Data Matrix (real encoder)
                {
                    // ^BXo,h,s,cols,rows — h = module size; cols forces the symbol size.
                    var bx = ParseNumbers(args).ToArray();
                    dmModuleSize = bx.Length > 0 ? Math.Max(1, bx[0]) : 3;
                    dmForcedSize = bx.Length > 2 ? (int)bx[2] : 0;
                    pendingDataMatrix = true;
                    break;
                }
                case "BQ": // QR code: ^BQa,b,c — c = magnification
                {
                    var bq = ParseNumbers(args).ToArray();
                    qrMag = bq.Length > 1 ? Math.Max(1, bq[1]) : 3;
                    pendingQr = true;
                    break;
                }
                case "B7": // PDF417: ^B7o,h,s,c,r,t — h row height, s security, c cols, r rows
                {
                    var b7 = ParseNumbers(args).ToArray();
                    p417RowH = b7.Length > 0 ? Math.Max(1, b7[0]) : 8;
                    p417Sec  = b7.Length > 1 ? (int)b7[1] : -1;
                    p417Cols = b7.Length > 2 ? (int)b7[2] : 0;
                    p417Rows = b7.Length > 3 ? (int)b7[3] : 0;
                    pendingPdf417 = true;
                    break;
                }
                case "BA": // Code 93: ^BAo,h,f,g,e
                case "B1": // Code 11: ^B1o,e(1 check digit),h,f,g
                case "BK": // Codabar: ^BKo,e,h,f,g,k(start),l(stop)
                case "BM": // MSI: ^BMo,e(check type),h,f,g,e2
                case "BP": // Plessey: ^BPo,e,h,f,g
                case "BI": // Industrial 2 of 5: ^BIo,h,f,g
                case "BJ": // Standard 2 of 5: ^BJo,h,f,g
                case "BL": // LOGMARS: ^BLo,h,g — Code 39 with a mandatory check digit
                case "BZ": // POSTNET: ^BZo,h,f,g
                case "B5": // PLANET: ^B5o,h,f,g
                {
                    var b = args.Split(',');
                    barcodeRotation = BarcodeOrientation(args, fwOrient);
                    string P(int i) => b.Length > i ? b[i].Trim() : "";
                    bool Flag(int i) => !string.Equals(P(i), "N", StringComparison.OrdinalIgnoreCase);
                    void Above(int i) => hrtAbove = string.Equals(P(i), "Y", StringComparison.OrdinalIgnoreCase);
                    void Height(int i)
                    {
                        if (double.TryParse(P(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                            barcodeHeight = Math.Max(20, v);
                    }
                    // The height and the "print the interpretation line" flag sit at a
                    // different index depending on how many switches come before them.
                    switch (command)
                    {
                        case "BA": Height(1); showBarcodeText = Flag(2); Above(3); pendingSym = "93"; break;
                        case "BI": Height(1); showBarcodeText = Flag(2); Above(3); pendingSym = "ind25"; break;
                        case "BJ": Height(1); showBarcodeText = Flag(2); Above(3); pendingSym = "std25"; break;
                        case "BZ": Height(1); showBarcodeText = Flag(2); Above(3); pendingSym = "postnet"; break;
                        case "B5": Height(1); showBarcodeText = Flag(2); Above(3); pendingSym = "planet"; break;
                        case "BL": Height(1); showBarcodeText = true; symCheck = true; Above(2); pendingSym = "logmars"; break;
                        case "B1":
                            symCheck = string.Equals(P(1), "Y", StringComparison.OrdinalIgnoreCase);
                            Height(2); showBarcodeText = Flag(3); Above(4); pendingSym = "11";
                            break;
                        case "BP":
                            Height(2); showBarcodeText = Flag(3); Above(4); pendingSym = "plessey";
                            break;
                        case "BK":
                            Height(2); showBarcodeText = Flag(3); Above(4);
                            cbStart = P(5).Length > 0 ? char.ToUpperInvariant(P(5)[0]) : 'A';
                            cbStop  = P(6).Length > 0 ? char.ToUpperInvariant(P(6)[0]) : 'A';
                            pendingSym = "codabar";
                            break;
                        case "BM":
                            msiCheck = P(1).Length > 0 ? char.ToUpperInvariant(P(1)[0]) : 'B';
                            Height(2); showBarcodeText = Flag(3); Above(4); pendingSym = "msi";
                            break;
                    }
                    break;
                }
                case "B9": // UPC-E: ^B9o,h,f,g,e
                case "BS": // UPC/EAN 2- or 5-digit supplement: ^BSo,h,f,g
                {
                    var b9 = args.Split(',');
                    barcodeRotation = BarcodeOrientation(args, fwOrient);
                    if (b9.Length > 1 && double.TryParse(b9[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b9h))
                        barcodeHeight = Math.Max(20, b9h);
                    showBarcodeText = b9.Length < 3 || !string.Equals(b9[2].Trim(), "N", StringComparison.OrdinalIgnoreCase);
                    pendingSym = command == "B9" ? "UPE" : "addon";
                    break;
                }
                case "BR": // GS1 DataBar — the reference renders nothing, so neither do
                           // we (printing the raw data would look like a bug).
                    swallowFd = true;
                    break;
                case "BF": // MicroPDF417 — a real symbol we do not encode yet: reserve
                           // the area with the 2D placeholder rather than dumping text.
                    pending2D = true;
                    break;
                case "B4": // Code 49
                case "BB": // CODABLOCK
                case "BT": // TLC39
                    // The reference renderer prints the data as plain text for these
                    // stacked symbologies rather than drawing a symbol — match it
                    // instead of showing a meaningless placeholder.
                    pendingSym = "astext";
                    break;
                case "BO": // Aztec (magnification is the first numeric arg)
                case "B0":
                    var azArgs = ParseNumbers(args).ToArray();
                    aztecMag = azArgs.Length > 0 ? Math.Max(1, azArgs[0]) : 1;
                    pendingAztec = true;
                    break;
                case "GS": // ^GSo,h,w + ^FD symbol code (A ® / B © / C ™ / D UL / E CSA)
                {
                    var gs = ParseNumbers(args).ToArray();
                    gsH = gs.Length > 0 ? gs[0] : font.Height;
                    gsW = gs.Length > 1 ? gs[1] : gsH;
                    pendingGs = true;
                    break;
                }
                case "GC": // circle: ^GCd,t,c
                {
                    var gc = ParseNumbers(args).ToArray();
                    if (gc.Length >= 1)
                    {
                        double d = Math.Max(3, gc[0]);
                        double t = gc.Length > 1 ? Math.Max(1, gc[1]) : 1;
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        double topY = typeset ? fy - d : fy;
                        fieldBuf.Add(new ZplEllipse(fx, topY, d, d, t));
                        Grow(fx + d, topY + d);
                    }
                    break;
                }
                case "GE": // ellipse: ^GEw,h,t,c
                {
                    var ge = ParseNumbers(args).ToArray();
                    if (ge.Length >= 2)
                    {
                        double gw = Math.Max(3, ge[0]), gh = Math.Max(3, ge[1]);
                        double t = ge.Length > 2 ? Math.Max(1, ge[2]) : 1;
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        double topY = typeset ? fy - gh : fy;
                        fieldBuf.Add(new ZplEllipse(fx, topY, gw, gh, t));
                        Grow(fx + gw, topY + gh);
                    }
                    break;
                }
                case "FB":
                {
                    // ^FBwidth,maxLines,lineSpacing,justification(L/C/R/J),hangingIndent
                    var fb = args.Split(',');
                    fbActive = true;
                    fbWidth = fb.Length > 0 && double.TryParse(fb[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fbw) ? fbw : 0;
                    fbLines = fb.Length > 1 && int.TryParse(fb[1].Trim(), out var fbl) ? Math.Max(1, fbl) : 1;
                    fbSpacing = fb.Length > 2 && double.TryParse(fb[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fbs) ? fbs : 0;
                    fbJust = fb.Length > 3 && fb[3].Trim().Length > 0 ? char.ToUpperInvariant(fb[3].Trim()[0]) : 'L';
                    break;
                }
                case "FH":
                    fieldHex = true;
                    hexIndicator = args.Length > 0 && !char.IsDigit(args[0]) ? args[0] : '_';
                    break;
                case "FR":
                    reverse = true;
                    break;
                case "GB":
                {
                    var b = ParseNumbers(args).Select(v => v * unitScale).ToArray();
                    if (b.Length >= 1)
                    {
                        double gw = b.Length > 0 ? b[0] : 0;
                        double gh = b.Length > 1 ? b[1] : 0;
                        double gt = b.Length > 2 ? Math.Max(1, b[2]) : 1;
                        // A 0/small dimension turns the box into a line/bar of the
                        // given thickness (^GB787,0,5 = a 787-wide, 5-thick line).
                        gw = Math.Max(gw, gt);
                        gh = Math.Max(gh, gt);
                        // 4th param = line color (B default / W). A white box knocks out
                        // (erases) like a reverse field — e.g. ^GB…,W behind ^FR text.
                        var gbParts = args.Split(',');
                        bool white = gbParts.Length > 3 && gbParts[3].Trim().ToUpperInvariant() == "W";
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        double topY = typeset ? fy - gh : fy; // ^FT anchors bottom-left
                        var boxRect = new ZplRect(fx, topY, gw, gh);
                        // ^FR reverses against the background: knock out to white only when
                        // the field is genuinely OVER a solid black area — tested by the box
                        // centre being inside a tracked black box. (A hairline crossing the
                        // field must not trigger it: e.g. the "MESS" outline box crosses a
                        // 1-dot separator line yet must still print black.)
                        double cX = fx + gw / 2, cY = topY + gh / 2;
                        bool overBlack = reverse && blackBoxes.Any(b => cX >= b.X && cX <= b.Right && cY >= b.Y && cY <= b.Bottom);
                        fieldBuf.Add(new ZplBox(fx, topY, gw, gh, gt, overBlack, white));
                        // Remember only SOLID 2-D black areas (min dimension ≥ 8 dots) as
                        // knockout backdrops — hairline rules/bars are not backdrops.
                        if (!overBlack && !white && gt >= Math.Min(gw, gh) / 2.0 && Math.Min(gw, gh) >= 8)
                            fieldBlackBoxes.Add(boxRect);
                        Grow(fx + gw, topY + gh);
                        reverse = labelReverse;
                    }
                    break;
                }
                case "GD":
                {
                    // ^GDw,h,t,c,o — o: L (default) = "\" top-left→bottom-right,
                    //                    R           = "/" bottom-left→top-right.
                    var l = ParseNumbers(args).ToArray();
                    if (l.Length >= 2)
                    {
                        var t = l.Length >= 3 ? Math.Max(1, l[2]) : 1;
                        var gdParts = args.Split(',');
                        bool rightLean = gdParts.Length > 4 && gdParts[4].Trim().ToUpperInvariant() is "R" or "/";
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        fieldBuf.Add(rightLean
                            ? new ZplLine(fx, fy + l[1], l[0], -l[1], t)
                            : new ZplLine(fx, fy, l[0], l[1], t));
                        Grow(fx + Math.Abs(l[0]), fy + Math.Abs(l[1]));
                    }
                    break;
                }
                case "GF":
                case "GFA":
                {
                    var img = DecodeGraphic(args);
                    if (img is { } g)
                    {
                        double fx = x + lhX + lsX, fy = y + lhY + ltY;
                        double topY = typeset ? fy - g.Height : fy;
                        fieldBuf.Add(new ZplImage(fx, topY, g.Width, g.Height, g.Bits));
                        Grow(fx + g.Width, topY + g.Height);
                        // ^GF is complete in itself: it prints even without a closing
                        // ^FS (DPD's logo/Predict graphics are followed directly by the
                        // next ^FT — Labelary renders them; the abandon-on-new-field
                        // rule only applies to ^FD text fields).
                        CommitField();
                    }
                    break;
                }
                case "SN": // serial number field: render the initial value as text
                    EmitTextField(args.Split(',')[0]);
                    reverse = labelReverse;
                    break;
                case "FD":
                case "FV": // field variable: same payload as ^FD, just cleared per label
                {
                    // Use the RAW args (not the trimmed `args`): ^FD data can carry
                    // significant leading/trailing spaces used for alignment, e.g.
                    // "          D-B2C" right-aligns the text inside its cell.
                    var data = token.Args;
                    if (fieldHex)
                    {
                        data = ApplyHex(data, hexIndicator, out bool hexChanged);
                        if (hexChanged) data = DecodeHexUtf8(data);
                        fieldHex = false;
                    }
                    data = data.Replace("\\&", "\n", StringComparison.Ordinal);
                    double fx = x + lhX + lsX, fy = y + lhY + ltY;

                    if (swallowFd)
                    {
                        swallowFd = false; // RFID / MaxiCode data: nothing to draw
                    }
                    else if (pendingMaxiCode)
                    {
                        // Fixed physical size: 1.11 x 1.054 inches whatever the data.
                        double dots = (dpmm ?? fallbackDpmm) * 25.4;   // dots per inch
                        double mw = 0.985 * dots, mh = 0.950 * dots;
                        double topY = typeset ? fy - mh : fy;
                        double cx = fx + mw / 2, cy = topY + mh / 2;
                        // The bullseye: three black rings, the outer one 0.28 in across.
                        for (int ring = 0; ring < 3; ring++)
                        {
                            double d = (0.28 - ring * 0.075) * dots;
                            double t = 0.02 * dots;
                            fieldBuf.Add(new ZplEllipse(cx - d / 2, cy - d / 2, d, d, t));
                        }
                        fieldBuf.Add(new ZplBox(fx, topY, mw, mh, 1, false, false));
                        Grow(fx + mw, topY + mh);
                        pendingMaxiCode = false;
                    }
                    else if (pendingGs)
                    {
                        char code = data.Trim().Length > 0 ? char.ToUpperInvariant(data.Trim()[0]) : 'A';
                        double topY = typeset ? fy - gsH : fy;
                        fieldBuf.Add(new ZplSymbol(fx, topY, gsH, gsW, code));
                        Grow(fx + gsW, topY + gsH);
                        pendingGs = false;
                    }
                    else if (pendingAztec)
                    {
                        var matrix = TryEncodeAztec(data);
                        if (matrix is not null)
                        {
                            int nm = matrix.GetLength(0);
                            double sz = nm * aztecMag;
                            double topY = typeset ? fy - sz : fy;
                            fieldBuf.Add(new ZplAztec(fx, topY, aztecMag, matrix));
                            Grow(fx + sz, topY + sz);
                        }
                        pendingAztec = false;
                    }
                    else if (pendingDataMatrix)
                    {
                        var matrix = TryEncodeDataMatrix(data, dmForcedSize);
                        if (matrix is not null)
                        {
                            int nm = matrix.GetLength(0);
                            double sz = nm * dmModuleSize;
                            double topY = typeset ? fy - sz : fy;
                            fieldBuf.Add(new ZplDataMatrix(fx, topY, dmModuleSize, matrix));
                            Grow(fx + sz, topY + sz);
                        }
                        pendingDataMatrix = false;
                    }
                    else if (pendingQr)
                    {
                        // ^FD payload: <ecc><input>,data (e.g. "QA,https://…").
                        var payload = data;
                        char ecc = 'Q';
                        int comma = payload.IndexOf(',');
                        if (comma >= 1 && comma <= 2)
                        {
                            ecc = char.ToUpperInvariant(payload[0]);
                            payload = payload[(comma + 1)..];
                        }
                        var matrix = TryEncodeQr(payload, ecc);
                        if (matrix is not null)
                        {
                            int nm = matrix.GetLength(0);
                            double sz = nm * qrMag;
                            double topY = typeset ? fy - sz : fy;
                            fieldBuf.Add(new ZplDataMatrix(fx, topY, qrMag, matrix));
                            Grow(fx + sz, topY + sz);
                        }
                        pendingQr = false;
                    }
                    else if (pendingPdf417)
                    {
                        var matrix = TryEncodePdf417(data, p417Cols, p417Rows, p417Sec);
                        if (matrix is not null)
                        {
                            int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
                            double w = cols * moduleWidth, h = rows * p417RowH;
                            double topY = typeset ? fy - h : fy;
                            fieldBuf.Add(new ZplGrid(fx, topY, moduleWidth, p417RowH, matrix));
                            Grow(fx + w, topY + h);
                        }
                        pendingPdf417 = false;
                    }
                    else if (pending2D)
                    {
                        double sz = Math.Max(48, moduleWidth * 26);
                        double topY = typeset ? fy - sz : fy;
                        fieldBuf.Add(new ZplMatrix(fx, topY, sz));
                        Grow(fx + sz, topY + sz);
                        pending2D = false;
                    }
                    else if (pendingSym.Length > 0)
                    {
                        if (!string.IsNullOrEmpty(data))
                        {
                            // ^B4 / ^BB / ^BT: the reference prints the data, not a symbol.
                            if (pendingSym == "astext") EmitTextField(data);
                            else Emit1DBarcode(pendingSym, data, fx, fy);
                        }
                        pendingSym = "";
                    }
                    else if (pendingBarcode)
                    {
                        if (!string.IsNullOrEmpty(data))
                            Emit1DBarcode("128", data, fx, fy);
                        pendingBarcode = false;
                    }
                    else if (!string.IsNullOrEmpty(data))
                    {
                        EmitTextField(data);
                    }
                    reverse = labelReverse;
                    break;
                }
                case "FS":
                    fieldEnd = token.End;
                    CommitField();
                    pendingBarcode = false;
                    pending2D = false;
                    pendingAztec = false;
                    pendingDataMatrix = false;
                    pendingQr = false;
                    pendingPdf417 = false;
                    pendingSym = "";
                    font = defaultFont;     // ^A dressed this field only
                    orientation = fwOrient;
                    hrtAbove = false;
                    fpVertical = false;
                    pendingMaxiCode = false;
                    pendingGs = false;
                    swallowFd = false;
                    reverse = labelReverse;
                    fieldHex = false;
                    fbActive = false;
                    break;
            }

            // A field that never opened with ^FO/^FT still needs somewhere to point,
            // and a field spans every token that fed it: widen as they go by.
            if (fieldBuf.Count > 0)
            {
                if (fieldStart < 0) fieldStart = token.Start;
                if (token.End > fieldEnd) fieldEnd = token.End;
            }
        }

        var effectiveDpmm = dpmm ?? fallbackDpmm;
        // No ^PW/^LL → size from the content bounding box, mirroring the label-home
        // offset as the far margin so the content is framed symmetrically.
        var maxX = maxXCommitted;
        var maxY = maxYCommitted;
        var contentWidth  = maxX > 0 ? maxX + lhX : effectiveDpmm * 25.4 * DefaultLabelWidthInches;
        var contentHeight = maxY > 0 ? maxY + lhY : effectiveDpmm * 25.4 * DefaultLabelHeightInches;

        return new ZplRenderModel
        {
            DeclaredDpmm = dpmm,
            DeclaredWidthDots  = width  > 0 ? width  : null,
            DeclaredHeightDots = height > 0 ? height : null,
            ContentWidthDots  = contentWidth,
            ContentHeightDots = contentHeight,
            Size = new LabelSize(width > 0 ? width : contentWidth, height > 0 ? height : contentHeight),
            Drawables = drawables,
            InvertOrientation = poi,
            MirrorImage = mirror,
        };
    }

    // Orientation char of a ^Bx command's first parameter (N/R/I/B → degrees).
    // Omitted → the ^FW default orientation applies.
    private static int BarcodeOrientation(string args, int fallback = 0)
    {
        var first = args.Split(',')[0].Trim();
        return first.Length > 0 && "NRIB".IndexOf(char.ToUpperInvariant(first[0])) >= 0
            ? OrientationDegrees(first[0]) : fallback;
    }

    // Nearest-neighbour scaling of a 1bpp row-padded bitmap (for ^XG magnification).
    private static (int W, int H, byte[] Bits) ScaleBits(int w, int h, byte[] bits, int mx, int my)
    {
        if (mx == 1 && my == 1) return (w, h, bits);
        int nw = w * mx, nh = h * my;
        int srcRow = (w + 7) / 8, dstRow = (nw + 7) / 8;
        var dst = new byte[dstRow * nh];
        for (int yDst = 0; yDst < nh; yDst++)
        {
            int ySrc = yDst / my;
            for (int xDst = 0; xDst < nw; xDst++)
            {
                int xSrc = xDst / mx;
                int si = ySrc * srcRow + xSrc / 8;
                if (si < bits.Length && ((bits[si] >> (7 - (xSrc & 7))) & 1) == 1)
                    dst[yDst * dstRow + xDst / 8] |= (byte)(1 << (7 - (xDst & 7)));
            }
        }
        return (nw, nh, dst);
    }

    // Word-wraps ^FB data into at most maxLines lines of `width` dots ('\n' = forced
    // break); overflow lines are dropped, like a real printer's field block.
    private static List<string> WrapFieldBlock(string data, ZplFont font, double condense, double width, int maxLines)
    {
        var lines = new List<string>();
        foreach (var para in data.Split('\n'))
        {
            if (lines.Count >= maxLines) break;
            var words = para.Split(' ');
            var current = "";
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && MeasureTextWidth(candidate, font) * condense > width)
                {
                    lines.Add(current);
                    if (lines.Count >= maxLines) return lines;
                    current = word;
                }
                else current = candidate;
            }
            lines.Add(current);
        }
        return lines;
    }

    private static int OrientationDegrees(char c) => char.ToUpperInvariant(c) switch
    {
        'R' => 90, 'I' => 180, 'B' => 270, _ => 0,
    };

    // Encodes the field data (byte-per-char) into an Aztec module matrix, or null
    // if it cannot be encoded (falls back to no symbol rather than crashing).
    private static bool[,]? TryEncodeAztec(string data)
    {
        try
        {
            var bytes = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) bytes[i] = (byte)(data[i] & 0xFF);
            return AztecEncoder.Encode(bytes);
        }
        catch { return null; }
    }

    // Encodes the field data (byte-per-char) into a Data Matrix module matrix, or null.
    private static bool[,]? TryEncodeDataMatrix(string data, int forcedSize = 0)
    {
        try
        {
            var bytes = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) bytes[i] = (byte)(data[i] & 0xFF);
            return DataMatrixEncoder.Encode(bytes, forcedSize);
        }
        catch { return null; }
    }

    private static bool[,]? TryEncodeQr(string data, char ecc)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            return QrEncoder.Encode(bytes, ecc);
        }
        catch { return null; }
    }

    private static bool[,]? TryEncodePdf417(string data, int cols, int rows, int security)
    {
        try
        {
            return Pdf417Encoder.Encode(data, cols, rows, security);
        }
        catch { return null; }
    }

    // Converts ^FH hex escapes (indicator + two hex digits) into the raw bytes,
    // e.g. "_1E" → char 0x1E. Used for control chars in structured barcode data.
    private static string ApplyHex(string data, char indicator) => ApplyHex(data, indicator, out _);

    private static string ApplyHex(string data, char indicator, out bool changed)
    {
        changed = false;
        var sb = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == indicator && i + 2 < data.Length
                && Uri.IsHexDigit(data[i + 1]) && Uri.IsHexDigit(data[i + 2]))
            {
                sb.Append((char)Convert.ToInt32(data.Substring(i + 1, 2), 16));
                i += 2;
                changed = true;
            }
            else sb.Append(data[i]);
        }
        return sb.ToString();
    }

    // With ^CI28 the ^FH hex escapes are raw UTF-8 bytes (e.g. _C3_A9 = é). Only
    // applied when ApplyHex actually converted escapes, and only when the byte
    // sequence is STRICTLY valid UTF-8 — otherwise the data already contained real
    // Unicode text (e.g. "N° Expédition") and must not be reinterpreted.
    private static string DecodeHexUtf8(string data)
    {
        if (!data.Any(c => c >= 0x80 && c <= 0xFF)) return data;
        try
        {
            var bytes = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] > 0xFF) return data; // genuine Unicode chars → not a byte string
                bytes[i] = (byte)data[i];
            }
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch { return data; }
    }

    // Decodes an ASCII-hex ^GF/^GFA graphic (1 bit per pixel, rows byte-padded).
    // Supports the ZPL ACS compression scheme (run-length letters + ,/: row codes).
    private static (int Width, int Height, byte[] Bits)? DecodeGraphic(string args)
    {
        var parts = args.Split(new[] { ',' }, 5);
        if (parts.Length < 5) return null;
        if (parts[0].Trim().ToUpperInvariant() is not ("A" or "")) return null; // ASCII-hex only
        if (!int.TryParse(parts[2].Trim(), out int total) || total <= 0) return null;
        if (!int.TryParse(parts[3].Trim(), out int bytesPerRow) || bytesPerRow <= 0) return null;

        var data = parts[4].TrimStart();
        // :Z64:/:B64: — base64-encoded data (Z64 = additionally zlib-deflated), with a
        // trailing ":CRC". Used by e.g. the Colissimo logo. Anything else is ASCII-hex
        // (optionally ACS-compressed).
        byte[]? bytes =
            data.StartsWith(":Z64:", StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(":B64:", StringComparison.OrdinalIgnoreCase)
                ? DecodeBase64Graphic(data, total)
                : DecodeAcsHex(parts[4], total, bytesPerRow);
        if (bytes is null) return null;

        int rows = total / bytesPerRow;
        if (rows <= 0) return null;
        return (bytesPerRow * 8, rows, bytes);
    }

    // Decodes a ZPL :Z64:/:B64: graphic body. The base64 payload sits between the
    // ":Z64:" (or ":B64:") tag and the final ":CRC"; Z64 is additionally zlib-deflated.
    // Returns exactly `total` bytes (padded / truncated).
    private static byte[]? DecodeBase64Graphic(string data, int total)
    {
        bool deflated = data.StartsWith(":Z64:", StringComparison.OrdinalIgnoreCase);
        string body = data.Substring(5);
        int crc = body.LastIndexOf(':');            // strip the trailing ":CRC"
        if (crc >= 0) body = body.Substring(0, crc);
        var sb = new StringBuilder(body.Length);
        foreach (char c in body) if (!char.IsWhiteSpace(c)) sb.Append(c); // may be line-wrapped
        try
        {
            var raw = Convert.FromBase64String(sb.ToString());
            if (deflated)
            {
                using var input = new MemoryStream(raw);
                using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var outp = new MemoryStream(total);
                z.CopyTo(outp);
                raw = outp.ToArray();
            }
            if (raw.Length == total) return raw;
            var fixedBytes = new byte[total];
            Array.Copy(raw, fixedBytes, Math.Min(raw.Length, total));
            return fixedBytes;
        }
        catch { return null; }
    }

    // ZPL "Alternative Compression Scheme" (ACS) for ^GFA hex data:
    //  - 0-9 A-F           : literal hex nibble
    //  - G..Y              : repeat count 1..19  } summed together, applied to the
    //  - g..z              : repeat count 20..400 (×20)  next single hex nibble
    //  - reaching bytesPerRow×2 nibbles : the row auto-wraps to the next row
    //  - ','               : end the row early, filling the remainder with 0x00
    //  - ':'               : repeat the previous row verbatim
    // (Consecutive full rows in the data have no ',' between them — they rely on the
    // auto-wrap. A leading ',' means a fully-blank first row.)
    private static byte[] DecodeAcsHex(string raw, int total, int bytesPerRow)
    {
        int nibblesPerRow = bytesPerRow * 2;
        int totalRows = total / bytesPerRow;
        var rows = new List<int[]>(totalRows);
        var row = new List<int>(nibblesPerRow);
        int[]? prev = null;
        int count = 0;

        void FlushRow()
        {
            var arr = new int[nibblesPerRow];
            for (int k = 0; k < nibblesPerRow && k < row.Count; k++) arr[k] = row[k];
            rows.Add(arr);
            prev = arr;
            row.Clear();
        }

        foreach (char ch in raw)
        {
            if (ch <= ' ') continue;
            if (Uri.IsHexDigit(ch))
            {
                int v = Convert.ToInt32(ch.ToString(), 16);
                int rep = count > 0 ? count : 1;
                count = 0;
                for (int k = 0; k < rep; k++)
                {
                    row.Add(v);
                    if (row.Count == nibblesPerRow) FlushRow(); // auto-wrap
                }
            }
            else if (ch >= 'G' && ch <= 'Y') count += ch - 'G' + 1;
            else if (ch >= 'g' && ch <= 'z') count += (ch - 'g' + 1) * 20;
            else if (ch == ',') { count = 0; FlushRow(); }        // end row early, pad with 0
            else if (ch == ':') { count = 0; if (row.Count > 0) FlushRow(); if (prev is not null) rows.Add(prev); }
            // any other character is ignored
        }
        if (row.Count > 0) FlushRow();

        var outBytes = new byte[total];
        for (int r = 0; r < totalRows && r < rows.Count; r++)
        {
            var nr = rows[r];
            for (int b = 0; b < bytesPerRow; b++)
                outBytes[r * bytesPerRow + b] = (byte)((nr[b * 2] << 4) | nr[b * 2 + 1]);
        }
        return outBytes;
    }

    /// <summary>
    /// Renders the model onto the canvas. When <paramref name="hitMap"/> is given it
    /// is filled with every element created and the drawable it came from, which is
    /// what lets a click on the preview find its way back to the ZPL that made it.
    /// Collected here rather than recomputed from the model: the geometry of a field
    /// (condensed glyphs, baseline anchors, rotation) lives in the drawing helpers,
    /// and a second copy of it would drift away from this one.
    /// </summary>
    public static void Draw(Canvas canvas, ZplRenderModel model, double dpmm, double rotationDegrees,
                            IDictionary<UIElement, ZplDrawable>? hitMap = null)
    {
        hitMap?.Clear();
        canvas.Children.Clear();

        double w = model.Size.WidthDots;
        double h = model.Size.HeightDots;
        var angle = ((rotationDegrees % 360) + 360) % 360;

        // Compute the axis-aligned bounding box of the rotated W×H rectangle.
        double bw, bh;
        if (angle < 0.5 || Math.Abs(angle - 180) < 0.5)
        {
            bw = w; bh = h;
        }
        else if (Math.Abs(angle - 90) < 0.5 || Math.Abs(angle - 270) < 0.5)
        {
            bw = h; bh = w;
        }
        else
        {
            var rad = angle * Math.PI / 180;
            var cos = Math.Abs(Math.Cos(rad));
            var sin = Math.Abs(Math.Sin(rad));
            bw = w * cos + h * sin;
            bh = w * sin + h * cos;
        }

        canvas.Width  = bw;
        canvas.Height = bh;
        canvas.Background = null; // white filled by the Rectangle child below (avoids layout overflow on rotations)
        canvas.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        canvas.RenderTransform = BuildRotation(angle, w, h, bw, bh);
        // Clip to the document rectangle (local space, before the rotation), so
        // anything overflowing the label (e.g. text wider than ^PW) is cut off
        // instead of spilling outside — both on screen and in the PNG snapshot.
        canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, w, h) };

        canvas.Children.Add(new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Colors.Gray),
            StrokeThickness = 1
        });

        // ^POI: the content prints upside-down. Rotate an inner container (a child
        // transform is captured by RenderTargetBitmap; the root canvas' own
        // RenderTransform would be lost in PNG snapshots).
        Canvas target = canvas;
        if (model.InvertOrientation || model.MirrorImage)
        {
            // ^PMY mirrors left/right; ^POI turns the label upside-down. Both together
            // is a vertical flip, which the two scales below produce naturally.
            var flip = new TransformGroup();
            if (model.MirrorImage)
                flip.Children.Add(new ScaleTransform { ScaleX = -1, ScaleY = 1, CenterX = w / 2, CenterY = h / 2 });
            if (model.InvertOrientation)
                flip.Children.Add(new RotateTransform { Angle = 180, CenterX = w / 2, CenterY = h / 2 });
            target = new Canvas { Width = w, Height = h, RenderTransform = flip };
            Canvas.SetLeft(target, 0);
            Canvas.SetTop(target, 0);
            canvas.Children.Add(target);
        }

        var blackFilledRects = new List<ZplRect>();
        foreach (var drawable in model.Drawables)
        {
            int childrenBefore = target.Children.Count;
            switch (drawable)
            {
                case ZplText text:
                    DrawText(target, text);
                    break;
                case ZplBox box:
                    DrawBox(target, box, blackFilledRects);
                    break;
                case ZplLine line:
                    DrawLine(target, line);
                    break;
                case ZplBars bars:
                    DrawBars(target, bars);
                    break;
                case ZplEllipse el:
                    DrawEllipse(target, el);
                    break;
                case ZplSymbol sym:
                    DrawSymbol(target, sym);
                    break;
                case ZplImage image:
                    DrawImage(target, image);
                    break;
                case ZplMatrix matrix:
                    DrawMatrix(target, matrix);
                    break;
                case ZplAztec aztec:
                    DrawAztec(target, aztec);
                    break;
                case ZplDataMatrix dm:
                    DrawModuleGrid(target, dm.X, dm.Y, dm.ModuleSize, dm.ModuleSize, dm.Matrix);
                    break;
                case ZplGrid grid:
                    DrawModuleGrid(target, grid.X, grid.Y, grid.ModW, grid.ModH, grid.Matrix);
                    break;
            }

            // Everything this drawable just put on the canvas belongs to it. A field
            // can produce several (a barcode is bars plus its interpretation line),
            // and they are reunited later by their shared source span.
            if (hitMap is not null)
                for (int i = childrenBefore; i < target.Children.Count; i++)
                    hitMap[target.Children[i]] = drawable;
        }
    }

    // Builds the RenderTransform that rotates content by angle degrees while keeping
    // all pixels in positive coordinate space (no overflow outside the canvas bounds).
    private static Transform BuildRotation(double angle, double w, double h, double bw, double bh)
    {
        if (angle < 0.5) return new TranslateTransform(); // identity

        var tg = new TransformGroup();
        if (Math.Abs(angle - 90) < 0.5)
        {
            // 90° CW: (x,y)→(-y,x)  → spans x=[-H,0], translate X=H
            tg.Children.Add(new RotateTransform { Angle = 90 });
            tg.Children.Add(new TranslateTransform { X = h });
        }
        else if (Math.Abs(angle - 180) < 0.5)
        {
            // 180°:   (x,y)→(-x,-y) → spans x=[-W,0],y=[-H,0], translate (W,H)
            tg.Children.Add(new RotateTransform { Angle = 180 });
            tg.Children.Add(new TranslateTransform { X = w, Y = h });
        }
        else if (Math.Abs(angle - 270) < 0.5)
        {
            // 270° CW: (x,y)→(y,-x) → spans y=[-W,0], translate Y=W
            tg.Children.Add(new RotateTransform { Angle = 270 });
            tg.Children.Add(new TranslateTransform { Y = w });
        }
        else
        {
            // Arbitrary angle: rotate around original center, then shift into the bounding box.
            tg.Children.Add(new RotateTransform { Angle = angle, CenterX = w / 2, CenterY = h / 2 });
            tg.Children.Add(new TranslateTransform { X = (bw - w) / 2, Y = (bh - h) / 2 });
        }
        return tg;
    }

    /// <summary>
    /// Returns the FontSize multiplier needed so the visible ink height of capital
    /// letters matches the ZPL-specified dot height.
    /// TrueType em sizes include internal line-spacing that reduces the ink height
    /// below the em; this factor compensates so glyph height == ZPL height.
    /// Derived empirically: at FontSize H, Bitstream Vera Sans Mono cap height ≈ H×13/15.
    /// </summary>
    private static double GlyphScaleFactor(string fontFamily) =>
        fontFamily.StartsWith("Bitstream Vera", StringComparison.OrdinalIgnoreCase)
            ? 15.0 / 13.0
            : 1.0;

    // Baseline distance from the top of the line box, as a fraction of the ZPL
    // font height (the origin of a ^FT field is the text baseline).
    private const double BaselineFraction = 0.72;

    // Nominal space advance of the Zebra "0" font as a fraction of the em (font
    // height), measured from Labelary (~0.30 em). Used to render leading-space
    // alignment padding at the reference width instead of our narrower space glyph.
    private const double ZebraSpaceEmRatio = 0.30;

    private static void DrawText(Canvas canvas, ZplText text)
    {
        var fontSize = Math.Max(8, text.Height);
        // Scale FontSize so the ink height equals the ZPL dot height.
        var renderFontSize = fontSize * GlyphScaleFactor(text.Font);
        double anchorY = text.Baseline ? fontSize * BaselineFraction : 0;
        // ^A0 width parameter: w<h condenses glyphs horizontally (glyph space, pre-rotation).
        double condense = text.Height > 0 && text.Width > 0 ? text.Width / text.Height : 1.0;

        // DirectWrite's BOLD Bitstream Vera Sans Mono does not survive a squeezed cell:
        // Zebra's font B at ^ABN,30,15 is 3x tall but only 2x wide, and the compressed
        // "2" — the one digit with no vertical stem for the hinter to snap onto — came
        // out as a bar that moved with the preview zoom. The regular face compresses
        // cleanly, so draw that and rebuild the weight with the sub-pixel passes below
        // (measured: they put the ink back where the bold face had it). Only the XAML
        // preview is affected — the vector PDF/PNG export keeps the real bold face.
        bool restoreWeight = text.Bold && condense < 0.9
            && text.Font.StartsWith("Bitstream Vera", StringComparison.OrdinalIgnoreCase);
        var weight = text.Bold && !restoreWeight ? FontWeights.Bold : FontWeights.Normal;

        // For rotated ^FO fields the (x,y) is the top-left of the ROTATED bounding
        // box (Labelary's rule), so the translation depends on the rendered size.
        double textW = 0;
        if (!text.Baseline && text.Rotation != 0)
        {
            var probe = new TextBlock
            {
                Text = text.Text,
                FontFamily = new FontFamily(text.Font),
                FontSize = renderFontSize,
                FontWeight = weight,
                TextWrapping = TextWrapping.NoWrap,
            };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            textW = probe.DesiredSize.Width * condense;
        }

        TextBlock MakeBlock(double extraX)
        {
            var block = new TextBlock
            {
                Text = text.Text,
                Foreground = new SolidColorBrush(text.Reverse ? Colors.White : Colors.Black),
                FontFamily = new FontFamily(text.Font),
                FontSize = renderFontSize,
                FontWeight = weight,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = fontSize,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Padding = new Thickness(0),
            };
            var transforms = new TransformGroup();
            if (Math.Abs(condense - 1.0) > 0.001)
                transforms.Children.Add(new ScaleTransform { ScaleX = condense, ScaleY = 1.0, CenterX = 0, CenterY = anchorY });

            double tx, ty;
            if (text.Baseline || text.Rotation == 0)
            {
                // ^FT: rotate around the baseline anchor, anchor lands at (X, Y).
                if (text.Rotation != 0)
                    transforms.Children.Add(new RotateTransform { Angle = text.Rotation, CenterX = 0, CenterY = anchorY });
                tx = text.X; ty = text.Y - anchorY;
            }
            else
            {
                // ^FO + rotation: rotate around the block's top-left, then place the
                // rotated bounding box's top-left corner at (X, Y).
                transforms.Children.Add(new RotateTransform { Angle = text.Rotation, CenterX = 0, CenterY = 0 });
                (tx, ty) = text.Rotation switch
                {
                    90  => (text.X + fontSize, text.Y),
                    180 => (text.X + textW, text.Y + fontSize),
                    270 => (text.X, text.Y + textW),
                    _   => (text.X, text.Y),
                };
            }
            transforms.Children.Add(new TranslateTransform { X = tx + extraX, Y = ty });
            block.RenderTransform = transforms;
            Canvas.SetLeft(block, 0);
            Canvas.SetTop(block, 0);
            return block;
        }

        canvas.Children.Add(MakeBlock(0));
        // DirectWrite renders Swiss 721 Condensed "Bold" lighter than Zebra/Labelary.
        // Faux-embolden non-condensed bold text with a sub-pixel second pass so it matches
        // (e.g. "S F", the Contact/Note block). Condensed text (w<h) already narrows its
        // strokes and matches without help — and a second pass would over-ink it — so it
        // is left alone.
        // The 0.5-dot offset is right for medium sizes but proportionally huge on
        // small text (h≤~24: DPD's "Destinataire", Contact/Tél/Ref labels, agency
        // block…) which came out visibly heavier than Labelary — quarter it there.
        if (text.Bold && condense > 0.95 && text.Height >= 28)
            canvas.Children.Add(MakeBlock(0.5));
        // Squeezed Vera Mono lost its real bold face above: put the weight back with
        // overlapping passes across the width the bold stems would have covered.
        if (restoreWeight)
        {
            double spread = 0.05 * renderFontSize * condense; // bold-vs-regular stem gain
            for (double dx = 0.5; dx <= spread + 0.01; dx += 0.5)
                canvas.Children.Add(MakeBlock(dx));
        }
    }

    // Renders a pre-built 1D barcode (bars + labels) with the ^FO rotation rule:
    // the rotated bounding box's top-left corner sits at (X, Y).
    private static void DrawBars(Canvas canvas, ZplBars bars)
    {
        var inner = new Canvas { Width = bars.Width, Height = bars.Height };
        var black = new SolidColorBrush(Colors.Black);
        foreach (var s in bars.Segs)
        {
            var rect = new Rectangle { Width = Math.Max(1, s.W), Height = Math.Max(1, s.H), Fill = black };
            Canvas.SetLeft(rect, s.X);
            Canvas.SetTop(rect, s.Y);
            inner.Children.Add(rect);
        }
        foreach (var l in bars.Labels)
        {
            var tb = new TextBlock
            {
                Text = l.Text,
                Foreground = black,
                FontFamily = new FontFamily("Bitstream Vera Sans Mono"),
                FontSize = l.FontHeight,
                TextWrapping = TextWrapping.NoWrap,
            };
            if (l.CenterWidth > 0)
            {
                tb.Width = l.CenterWidth;
                tb.TextAlignment = TextAlignment.Center;
            }
            Canvas.SetLeft(tb, l.X);
            Canvas.SetTop(tb, l.Y);
            inner.Children.Add(tb);
        }

        var tg = new TransformGroup();
        double tx = bars.X, ty = bars.Y;
        if (bars.Rotation != 0)
        {
            tg.Children.Add(new RotateTransform { Angle = bars.Rotation });
            (tx, ty) = bars.Rotation switch
            {
                90  => (bars.X + bars.Height, bars.Y),
                180 => (bars.X + bars.Width, bars.Y + bars.Height),
                270 => (bars.X, bars.Y + bars.Width),
                _   => (bars.X, bars.Y),
            };
        }
        tg.Children.Add(new TranslateTransform { X = tx, Y = ty });
        inner.RenderTransform = tg;
        Canvas.SetLeft(inner, 0);
        Canvas.SetTop(inner, 0);
        canvas.Children.Add(inner);
    }

    private static void DrawEllipse(Canvas canvas, ZplEllipse el)
    {
        bool filled = el.Thickness >= Math.Min(el.Width, el.Height) / 2;
        var shape = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = el.Width,
            Height = el.Height,
            Stroke = filled ? null : new SolidColorBrush(Colors.Black),
            StrokeThickness = filled ? 0 : el.Thickness,
            Fill = filled ? new SolidColorBrush(Colors.Black) : null,
        };
        Canvas.SetLeft(shape, el.X);
        Canvas.SetTop(shape, el.Y);
        canvas.Children.Add(shape);
    }

    // ^GS symbols: A ®, B ©, C ™ rendered as glyphs; D (UL) and E (CSA) approximated
    // as a circle mark with the letters inside.
    private static void DrawSymbol(Canvas canvas, ZplSymbol sym)
    {
        var black = new SolidColorBrush(Colors.Black);
        if (sym.Code is 'A' or 'B' or 'C')
        {
            var tb = new TextBlock
            {
                Text = sym.Code switch { 'A' => "®", 'B' => "©", _ => "™" },
                Foreground = black,
                FontFamily = new FontFamily("Arial"),
                FontSize = sym.Height,
                TextWrapping = TextWrapping.NoWrap,
            };
            Canvas.SetLeft(tb, sym.X);
            Canvas.SetTop(tb, sym.Y);
            canvas.Children.Add(tb);
            return;
        }

        // D = UL mark, E = CSA mark: circle outline + letters.
        double d = Math.Min(sym.Width, sym.Height);
        var circle = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = d, Height = d,
            Stroke = black,
            StrokeThickness = Math.Max(2, d * 0.06),
        };
        Canvas.SetLeft(circle, sym.X);
        Canvas.SetTop(circle, sym.Y);
        canvas.Children.Add(circle);
        var label = new TextBlock
        {
            Text = sym.Code == 'D' ? "UL" : "CSA",
            Foreground = black,
            FontFamily = new FontFamily("Arial"),
            FontWeight = FontWeights.Bold,
            FontSize = d * (sym.Code == 'D' ? 0.42 : 0.30),
            Width = d,
            TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(label, sym.X);
        Canvas.SetTop(label, sym.Y + d * 0.30);
        canvas.Children.Add(label);
    }

    private static void DrawImage(Canvas canvas, ZplImage img)
    {
        int w = img.PixelWidth, h = img.PixelHeight;
        if (w <= 0 || h <= 0) return;
        var wb = new WriteableBitmap(w, h);
        var buf = new byte[w * h * 4];
        int bytesPerRow = (w + 7) / 8;
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int idx = yy * bytesPerRow + xx / 8;
                bool black = idx < img.Bits.Length && ((img.Bits[idx] >> (7 - (xx & 7))) & 1) == 1;
                if (black)
                {
                    int p = (yy * w + xx) * 4;
                    buf[p] = 0; buf[p + 1] = 0; buf[p + 2] = 0; buf[p + 3] = 255; // opaque black (BGRA premul)
                }
            }
        using (var s = wb.PixelBuffer.AsStream()) s.Write(buf, 0, buf.Length);

        var image = new Image { Source = wb, Width = w, Height = h, Stretch = Stretch.Fill };
        Canvas.SetLeft(image, img.X);
        Canvas.SetTop(image, img.Y);
        canvas.Children.Add(image);
    }

    private static void DrawAztec(Canvas canvas, ZplAztec az) =>
        DrawModuleGrid(canvas, az.X, az.Y, az.ModuleSize, az.ModuleSize, az.Matrix);

    // Draws a 2D module matrix (Aztec / Data Matrix / QR / PDF417) as black rects,
    // merging horizontal runs of set modules into a single rectangle.
    private static void DrawModuleGrid(Canvas canvas, double x, double y, double mw, double mh, bool[,] matrix)
    {
        int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
        var brush = new SolidColorBrush(Colors.Black);
        for (int r = 0; r < rows; r++)
        {
            int c = 0;
            while (c < cols)
            {
                if (!matrix[r, c]) { c++; continue; }
                int c2 = c;
                while (c2 < cols && matrix[r, c2]) c2++;   // merge a horizontal run
                var rect = new Rectangle { Width = (c2 - c) * mw, Height = mh, Fill = brush };
                Canvas.SetLeft(rect, x + c * mw);
                Canvas.SetTop(rect, y + r * mh);
                canvas.Children.Add(rect);
                c = c2;
            }
        }
    }

    // Placeholder for 2D symbologies (Data Matrix / QR): a framed square with a
    // coarse pattern so the label layout still reads correctly.
    private static void DrawMatrix(Canvas canvas, ZplMatrix m)
    {
        double s = m.Size;
        var frame = new Rectangle
        {
            Width = s, Height = s,
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Colors.Black),
            StrokeThickness = Math.Max(1, s * 0.04),
        };
        Canvas.SetLeft(frame, m.X);
        Canvas.SetTop(frame, m.Y);
        canvas.Children.Add(frame);

        const int n = 10;
        double cell = s / n;
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                if (((r * 7 + c * 13) & 2) == 0)
                {
                    var q = new Rectangle { Width = cell, Height = cell, Fill = new SolidColorBrush(Colors.Black) };
                    Canvas.SetLeft(q, m.X + c * cell);
                    Canvas.SetTop(q, m.Y + r * cell);
                    canvas.Children.Add(q);
                }
    }

    private static void DrawBox(Canvas canvas, ZplBox box, List<ZplRect> blackFilledRects)
    {
        var blackBrush = new SolidColorBrush(Colors.Black);
        var whiteBrush = new SolidColorBrush(Colors.White);
        var brush = box.Reverse ? whiteBrush : blackBrush;
        var width = Math.Max(1, box.Width);
        var height = Math.Max(1, box.Height);
        var thickness = Math.Max(1, Math.Min(box.Thickness, Math.Min(width, height)));

        // ^GB…,W = an explicit white box (paint white / erase). On the white surface it
        // is invisible; over black it knocks out. Not the same as ^FR XOR.
        if (box.WhiteFill)
        {
            if (IsFilledBox(box)) AddFilledRect(canvas, box.X, box.Y, width, height, whiteBrush);
            else
            {
                AddFilledRect(canvas, box.X, box.Y, width, thickness, whiteBrush);
                AddFilledRect(canvas, box.X, box.Y + height - thickness, width, thickness, whiteBrush);
                AddFilledRect(canvas, box.X, box.Y, thickness, height, whiteBrush);
                AddFilledRect(canvas, box.X + width - thickness, box.Y, thickness, height, whiteBrush);
            }
            return;
        }

        if (IsFilledBox(box))
        {
            var rect = new ZplRect(box.X, box.Y, width, height);
            if (box.Reverse)
            {
                AddFilledRect(canvas, rect.X, rect.Y, rect.Width, rect.Height, whiteBrush);
                foreach (var visible in Subtract(rect, blackFilledRects))
                {
                    AddFilledRect(canvas, visible.X, visible.Y, visible.Width, visible.Height, blackBrush);
                }
            }
            else
            {
                AddFilledRect(canvas, rect.X, rect.Y, rect.Width, rect.Height, blackBrush);
                blackFilledRects.Add(rect);
            }

            return;
        }

        AddFilledRect(canvas, box.X, box.Y, width, thickness, brush);
        AddFilledRect(canvas, box.X, box.Y + height - thickness, width, thickness, brush);
        AddFilledRect(canvas, box.X, box.Y, thickness, height, brush);
        AddFilledRect(canvas, box.X + width - thickness, box.Y, thickness, height, brush);
    }

    private static void DrawLine(Canvas canvas, ZplLine line)
    {
        var shape = new Line
        {
            X1 = 0,
            Y1 = 0,
            X2 = line.Width,
            Y2 = line.Height,
            Stroke = new SolidColorBrush(Colors.Black),
            StrokeThickness = Math.Max(1, line.Thickness)
        };

        Canvas.SetLeft(shape, line.X);
        Canvas.SetTop(shape, line.Y);
        canvas.Children.Add(shape);
    }

    private static void AddFilledRect(Canvas canvas, double x, double y, double width, double height, Brush brush)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Fill = brush
        };

        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }

    private static bool RectsOverlap(ZplRect a, ZplRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static IEnumerable<ZplRect> Subtract(ZplRect source, IReadOnlyList<ZplRect> blockers)
    {
        var remaining = new List<ZplRect> { source };
        foreach (var blocker in blockers)
        {
            var next = new List<ZplRect>();
            foreach (var rect in remaining)
            {
                next.AddRange(SubtractOne(rect, blocker));
            }

            remaining = next;
        }

        return remaining.Where(rect => rect.Width > 0 && rect.Height > 0);
    }

    private static IEnumerable<ZplRect> SubtractOne(ZplRect source, ZplRect blocker)
    {
        var left = Math.Max(source.X, blocker.X);
        var top = Math.Max(source.Y, blocker.Y);
        var right = Math.Min(source.Right, blocker.Right);
        var bottom = Math.Min(source.Bottom, blocker.Bottom);

        if (right <= left || bottom <= top)
        {
            yield return source;
            yield break;
        }

        if (top > source.Y)
        {
            yield return new ZplRect(source.X, source.Y, source.Width, top - source.Y);
        }

        if (bottom < source.Bottom)
        {
            yield return new ZplRect(source.X, bottom, source.Width, source.Bottom - bottom);
        }

        if (left > source.X)
        {
            yield return new ZplRect(source.X, top, left - source.X, bottom - top);
        }

        if (right < source.Right)
        {
            yield return new ZplRect(right, top, source.Right - right, bottom - top);
        }
    }

    private static double? ParseDpmm(string args)
    {
        var token = args.Trim().TrimStart(',');
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct) && direct > 0)
        {
            return direct;
        }

        return token.ToUpperInvariant() switch
        {
            "A" => 6,
            "B" => 8,
            "C" => 12,
            "D" => 24,
            _ => null
        };
    }

    private static IEnumerable<double> ParseNumbers(string args)
    {
        foreach (var part in args.Split(',', StringSplitOptions.None))
        {
            var match = Regex.Match(part.Trim(), @"-?\d+(?:\.\d+)?");
            if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return value;
            }
        }
    }

    private static double ParseFirstNumber(string args)
    {
        return ParseNumbers(args).FirstOrDefault();
    }

    private static double Positive(double value, double fallback)
    {
        return value > 0 ? value : fallback;
    }

    private static double EstimateTextWidth(string text, double height)
    {
        return text.Length * height * 0.45;
    }

    /// <summary>
    /// Measures the real rendered width of a text run using the same font that will
    /// actually be used to draw it, instead of guessing from a per-character factor.
    /// This is what lets the auto-computed label size land close to the real one
    /// (e.g. the 4x6in / 812x1218 dots default for Labelary's sample label) instead
    /// of drifting whenever the font (and therefore its metrics) changes.
    /// </summary>
    private static double MeasureTextWidth(string text, ZplFont font) =>
        MeasureTextWidthByFamily(text, font.Family, font.Height, font.Bold);

    private static double MeasureTextWidthByFamily(string text, string family, double height, bool bold)
    {
        try
        {
            var baseFontSize = Math.Max(8, height);
            var probe = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(family),
                FontSize = baseFontSize * GlyphScaleFactor(family),
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.NoWrap
            };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (probe.DesiredSize.Width > 0)
            {
                return probe.DesiredSize.Width;
            }
        }
        catch
        {
            // Parse() might be invoked off the UI thread by some hosts (e.g. a
            // background debounce timer). Real text measurement needs the UI
            // thread, so fall back to a calibrated estimate rather than throwing.
        }

        return EstimateTextWidth(text, height);
    }

    // Default width when ^A/^CF omits it (or gives the invalid 0): the height scaled
    // by the font's natural cell aspect (1:1 for the scalable font 0, e.g. 10:18 for D).
    private static double DefaultFontWidth(string name, double height)
    {
        var (cellH, cellW) = ZplFont.BaseCell(name);
        return height * (cellW / cellH);
    }

    // Builds the font actually used to draw: bitmap faces snap to an integer
    // magnification of their cell, then the cell is converted to a render size.
    // A ^CW alias names a downloaded scalable font, so it skips the quantisation.
    private static ZplFont MakeFont(string name, double height, double width, ICollection<string>? aliases = null)
    {
        if (aliases is not null && aliases.Contains(name))
            return new ZplFont(name, Math.Max(8, height), Math.Max(4, width));
        var (cellH, cellW) = ZplFont.Quantize(name, height, width);
        var em = ZplFont.EmRatio(name);
        return new ZplFont(name, Math.Max(8, cellH * em), Math.Max(4, cellW * em * ZplFont.WidthRatio(name)));
    }

    private static ZplFont ParseDefaultFont(string args, ZplFont current,
        ICollection<string>? aliases = null, double unitScale = 1)
    {
        var parts = args.Split(',', StringSplitOptions.None);
        var name = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0].Trim() : current.Name;
        var height = parts.Length > 1 && TryParseNumber(parts[1], out var parsedHeight) ? parsedHeight * unitScale : current.Height;
        var width = parts.Length > 2 && TryParseNumber(parts[2], out var parsedWidth) && parsedWidth > 0
            ? parsedWidth * unitScale : DefaultFontWidth(name, height);
        return MakeFont(name, height, width, aliases);
    }

    private static (ZplFont Font, int Orientation) ParseFieldFont(string command, string args, ZplFont current,
        int fallbackOrientation = 0, ICollection<string>? aliases = null, double unitScale = 1)
    {
        var parts = args.Split(',', StringSplitOptions.None);
        var name = command.Length > 1 ? command[1].ToString(CultureInfo.InvariantCulture) : current.Name;
        // No orientation of its own → the ^FW default applies.
        int orient = fallbackOrientation;
        var first = parts.Length > 0 ? parts[0].Trim() : "";
        if (first.Length > 0 && "NRIB".IndexOf(char.ToUpperInvariant(first[0])) >= 0)
            orient = OrientationDegrees(first[0]);
        var numbers = ParseNumbers(args).ToArray();
        var height = numbers.Length > 0 ? numbers[0] * unitScale : current.Height;
        // ^A width must be 1..32000; 0 (or omitted) means "use the default" = the
        // height scaled by the font's natural aspect (NOT a literal zero width).
        var width = numbers.Length > 1 && numbers[1] > 0 ? numbers[1] * unitScale : DefaultFontWidth(name, height);
        return (MakeFont(name, height, width, aliases), orient);
    }

    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsFilledBox(ZplBox box)
    {
        // A border of half the smaller dimension (or more) covers the whole box.
        return box.Thickness >= Math.Min(box.Width, box.Height) / 2.0;
    }

    private static bool ParseBarcodeTextFlag(string args)
    {
        var parts = args.Split(',', StringSplitOptions.None);
        return parts.Length < 3 || !string.Equals(parts[2].Trim(), "N", StringComparison.OrdinalIgnoreCase);
    }

    private static double EstimateCode128Width(string data, double moduleWidth)
    {
        // Exact width from the encoded symbols: 11 modules each, +2 for the stop bar.
        int codes = EncodeCode128(data).Count;
        return ((codes - 1) * 11 + 13) * moduleWidth;
    }

    private static IReadOnlyList<BarcodeRun> BuildCode128Bars(string data, bool autoMode = true)
    {
        var codes = EncodeCode128(data, autoMode);
        var runs = new List<BarcodeRun>();

        foreach (var code in codes)
        {
            var pattern = Code128Patterns[code];
            for (var i = 0; i < pattern.Length; i++)
            {
                runs.Add(new BarcodeRun(i % 2 == 0, pattern[i] - '0'));
            }
        }

        return runs;
    }

    // ── Code 39 ──────────────────────────────────────────────────────────────
    // Patterns: 9 elements (5 bars/4 spaces alternating), 'w' wide / 'n' narrow.
    private static readonly Dictionary<char, string> Code39Patterns = new()
    {
        ['0'] = "nnnwwnwnn", ['1'] = "wnnwnnnnw", ['2'] = "nnwwnnnnw", ['3'] = "wnwwnnnnn",
        ['4'] = "nnnwwnnnw", ['5'] = "wnnwwnnnn", ['6'] = "nnwwwnnnn", ['7'] = "nnnwnnwnw",
        ['8'] = "wnnwnnwnn", ['9'] = "nnwwnnwnn", ['A'] = "wnnnnwnnw", ['B'] = "nnwnnwnnw",
        ['C'] = "wnwnnwnnn", ['D'] = "nnnnwwnnw", ['E'] = "wnnnwwnnn", ['F'] = "nnwnwwnnn",
        ['G'] = "nnnnnwwnw", ['H'] = "wnnnnwwnn", ['I'] = "nnwnnwwnn", ['J'] = "nnnnwwwnn",
        ['K'] = "wnnnnnnww", ['L'] = "nnwnnnnww", ['M'] = "wnwnnnnwn", ['N'] = "nnnnwnnww",
        ['O'] = "wnnnwnnwn", ['P'] = "nnwnwnnwn", ['Q'] = "nnnnnnwww", ['R'] = "wnnnnnwwn",
        ['S'] = "nnwnnnwwn", ['T'] = "nnnnwnwwn", ['U'] = "wwnnnnnnw", ['V'] = "nwwnnnnnw",
        ['W'] = "wwwnnnnnn", ['X'] = "nwnnwnnnw", ['Y'] = "wwnnwnnnn", ['Z'] = "nwwnwnnnn",
        ['-'] = "nwnnnnwnw", ['.'] = "wwnnnnwnn", [' '] = "nwwnnnwnn", ['*'] = "nwnnwnwnn",
        ['$'] = "nwnwnwnnn", ['/'] = "nwnwnnnwn", ['+'] = "nwnnnwnwn", ['%'] = "nnnwnwnwn",
    };

    private const string Code39Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    private static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildCode39(string data, bool mod43, double ratio)
    {
        var payload = new string(data.ToUpperInvariant().Where(Code39Patterns.ContainsKey).Where(c => c != '*').ToArray());
        if (mod43)
        {
            int sum = payload.Sum(c => Code39Charset.IndexOf(c));
            payload += Code39Charset[sum % 43];
        }
        var full = "*" + payload + "*";
        var runs = new List<BarcodeRun>();
        int wide = Math.Max(2, (int)Math.Round(ratio));
        for (int k = 0; k < full.Length; k++)
        {
            var pat = Code39Patterns[full[k]];
            for (int i = 0; i < pat.Length; i++)
                runs.Add(new BarcodeRun(i % 2 == 0, pat[i] == 'w' ? wide : 1));
            if (k < full.Length - 1) runs.Add(new BarcodeRun(false, 1)); // inter-char gap
        }
        return (runs, full);
    }

    // ── Interleaved 2 of 5 ───────────────────────────────────────────────────
    private static readonly string[] I2of5Patterns =
    {
        "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw",
        "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn",
    };

    private static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildI2of5(string data, bool mod10, double ratio)
    {
        var digits = new string(data.Where(char.IsDigit).ToArray());
        if (mod10)
        {
            int sum = 0;
            for (int i = 0; i < digits.Length; i++)
                sum += (digits[digits.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
            digits += (char)('0' + (10 - sum % 10) % 10);
        }
        if (digits.Length % 2 == 1) digits = "0" + digits;

        var runs = new List<BarcodeRun>();
        int wide = Math.Max(2, (int)Math.Round(ratio));
        int V(char c) => c == 'w' ? wide : 1;
        // start: nnnn (bar,space,bar,space)
        runs.Add(new BarcodeRun(true, 1)); runs.Add(new BarcodeRun(false, 1));
        runs.Add(new BarcodeRun(true, 1)); runs.Add(new BarcodeRun(false, 1));
        for (int i = 0; i < digits.Length; i += 2)
        {
            var barPat = I2of5Patterns[digits[i] - '0'];
            var spacePat = I2of5Patterns[digits[i + 1] - '0'];
            for (int k = 0; k < 5; k++)
            {
                runs.Add(new BarcodeRun(true, V(barPat[k])));
                runs.Add(new BarcodeRun(false, V(spacePat[k])));
            }
        }
        // stop: wide bar, narrow space, narrow bar
        runs.Add(new BarcodeRun(true, wide)); runs.Add(new BarcodeRun(false, 1)); runs.Add(new BarcodeRun(true, 1));
        return (runs, digits);
    }

    // ── EAN-13 / UPC-A / EAN-8 ───────────────────────────────────────────────
    private static readonly string[] EanL = { "0001101","0011001","0010011","0111101","0100011","0110001","0101111","0111011","0110111","0001011" };
    private static readonly string[] EanG = { "0100111","0110011","0011011","0100001","0011101","0111001","0000101","0010001","0001001","0010111" };
    private static readonly string[] EanR = { "1110010","1100110","1101100","1000010","1011100","1001110","1010000","1000100","1001000","1110100" };
    // First-digit parity for the EAN-13 left half (L = EanL, G = EanG).
    private static readonly string[] EanParity = { "LLLLLL","LLGLGG","LLGGLG","LLGGGL","LGLLGG","LGGLLG","LGGGLL","LGLGLG","LGLGGL","LGGLGL" };

    private static int EanCheckDigit(string digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            sum += d * (i % 2 == 0 ? 3 : 1);
        }
        return (10 - sum % 10) % 10;
    }

    // Builds the segments/labels of an EAN-13, UPC-A or EAN-8 barcode: guard bars
    // extend below the data bars by half the text height, and the digits sit in the
    // guard gaps (leading digit outside the symbol for EAN-13/UPC-A).
    // UPC-E parity of the six data digits, indexed by the number system (0 or 1)
    // and the check digit. 'E' uses the even (G) table, 'O' the odd (L) one.
    private static readonly string[] UpcEParity =
    {
        "EEEOOO", "EEOEOO", "EEOOEO", "EEOOOE", "EOEEOO",
        "EOOEEO", "EOOOEE", "EOEOEO", "EOEOOE", "EOOEOE",
    };

    /// <summary>
    /// UPC-E: six data digits between a 101 guard and a 010101 terminator, with the
    /// number system on the left and the check digit on the right of the symbol.
    /// </summary>
    private static void BuildUpcE(string data, double module, double barH, double hrtH,
        bool showText, List<BarSeg> segs, List<BarLabel> labels, out double totalW)
    {
        var digits = new string(data.Where(char.IsDigit).ToArray());
        digits = digits.Length >= 6 ? digits[^6..] : digits.PadLeft(6, '0');
        int system = data.TrimStart().StartsWith("1") && data.Length > 6 ? 1 : 0;

        // The check digit comes from the UPC-A the short form expands to.
        var expanded = ExpandUpcE(system, digits);
        int check = EanCheckDigit(expanded);
        var parity = UpcEParity[check];
        if (system == 1) parity = new string(parity.Select(p => p == 'E' ? 'O' : 'E').ToArray());

        var modules = new System.Text.StringBuilder();
        var guard = new List<(int Start, int Len)>();
        void AddGuard(string bits) { guard.Add((modules.Length, bits.Length)); modules.Append(bits); }

        AddGuard("101");
        for (int i = 0; i < 6; i++)
            modules.Append(parity[i] == 'O' ? EanL[digits[i] - '0'] : EanG[digits[i] - '0']);
        AddGuard("010101");

        // Unlike EAN-13, the symbol itself starts at the field origin: the number
        // system digit prints OUTSIDE it on the left and the check digit on the right
        // (measured on the reference: bars 40…193 for a ^FO40 field, digits either side).
        double guardExtra = showText ? hrtH * 0.5 : 0;
        double sideW = hrtH * 1.1;
        bool IsGuard(int idx) => guard.Any(g => idx >= g.Start && idx < g.Start + g.Len);

        var mstr = modules.ToString();
        for (int i = 0; i < mstr.Length;)
        {
            if (mstr[i] == '0') { i++; continue; }
            int j = i;
            bool tall = false;
            while (j < mstr.Length && mstr[j] == '1' && IsGuard(j) == IsGuard(i)) { tall |= IsGuard(j); j++; }
            segs.Add(new BarSeg(i * module, 0, (j - i) * module, barH + (tall ? guardExtra : 0)));
            i = j;
        }
        totalW = mstr.Length * module;

        if (showText)
        {
            double ty = barH - module;
            labels.Add(new BarLabel(-sideW, ty, system.ToString(CultureInfo.InvariantCulture), hrtH, 0));
            labels.Add(new BarLabel(3 * module, ty, digits, hrtH, 42 * module));
            labels.Add(new BarLabel(totalW + module, ty,
                check.ToString(CultureInfo.InvariantCulture), hrtH, 0));
        }
    }

    // UPC-E → the 11-digit UPC-A body it stands for (needed for the check digit).
    private static string ExpandUpcE(int system, string d)
    {
        var s = system.ToString(CultureInfo.InvariantCulture);
        return d[5] switch
        {
            '0' or '1' or '2' => s + d[..2] + d[5] + "0000" + d[2..5],
            '3'               => s + d[..3] + "00000" + d[3..5],
            '4'               => s + d[..4] + "00000" + d[4],
            _                 => s + d[..5] + "0000" + d[5],
        };
    }

    /// <summary>
    /// The 2- or 5-digit UPC/EAN supplement (^BS): a "1011" start, then the digits
    /// separated by "01", the parity chosen by a checksum of the digits themselves.
    /// </summary>
    private static void BuildEanAddOn(string data, double module, double barH, double hrtH,
        bool showText, List<BarSeg> segs, List<BarLabel> labels, out double totalW)
    {
        var digits = new string(data.Where(char.IsDigit).ToArray());
        if (digits.Length >= 5) digits = digits[..5];
        else if (digits.Length >= 2) digits = digits[..2];
        else digits = digits.PadLeft(2, '0');

        string parity;
        if (digits.Length == 2)
        {
            parity = (int.Parse(digits, CultureInfo.InvariantCulture) % 4) switch
            {
                0 => "LL", 1 => "LG", 2 => "GL", _ => "GG",
            };
        }
        else
        {
            int sum = 0;
            for (int i = 0; i < 5; i++) sum += (digits[i] - '0') * (i % 2 == 0 ? 3 : 9);
            parity = new[] { "GGLLL", "GLGLL", "GLLGL", "GLLLG", "LGGLL",
                             "LLGGL", "LLLGG", "LGLGL", "LGLLG", "LLGLG" }[sum % 10];
        }

        var modules = new System.Text.StringBuilder("1011");
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0) modules.Append("01");
            modules.Append(parity[i] == 'L' ? EanL[digits[i] - '0'] : EanG[digits[i] - '0']);
        }

        double top = 0;
        var mstr = modules.ToString();
        for (int i = 0; i < mstr.Length;)
        {
            if (mstr[i] == '0') { i++; continue; }
            int j = i;
            while (j < mstr.Length && mstr[j] == '1') j++;
            segs.Add(new BarSeg(i * module, top, (j - i) * module, barH));
            i = j;
        }
        totalW = mstr.Length * module;
        if (showText) labels.Add(new BarLabel(0, barH - module, digits, hrtH, totalW));
    }

    private static void BuildEanUpc(string sym, string data, double module, double barH, double hrtH,
        bool showText, List<BarSeg> segs, List<BarLabel> labels, out double totalW)
    {
        if (sym == "UPE") { BuildUpcE(data, module, barH, hrtH, showText, segs, labels, out totalW); return; }
        if (sym == "addon") { BuildEanAddOn(data, module, barH, hrtH, showText, segs, labels, out totalW); return; }

        var digits = new string(data.Where(char.IsDigit).ToArray());
        int bodyLen = sym == "E8" ? 7 : sym == "UPA" ? 11 : 12;
        digits = digits.Length >= bodyLen ? digits[..bodyLen] : digits.PadLeft(bodyLen, '0');
        digits += (char)('0' + EanCheckDigit(digits));
        if (sym == "UPA") digits = "0" + digits; // UPC-A = EAN-13 with leading 0

        // Build the 95-module (or 67 for EAN-8) pattern with guard positions marked.
        var modules = new System.Text.StringBuilder();
        var guard = new List<(int Start, int Len)>();
        void AddGuard(string bits) { guard.Add((modules.Length, bits.Length)); modules.Append(bits); }

        string left, right;
        if (sym == "E8")
        {
            left = digits[..4]; right = digits[4..8];
            AddGuard("101");
            foreach (var c in left) modules.Append(EanL[c - '0']);
            AddGuard("01010");
            foreach (var c in right) modules.Append(EanR[c - '0']);
            AddGuard("101");
        }
        else
        {
            int first = digits[0] - '0';
            left = digits[1..7]; right = digits[7..13];
            AddGuard("101");
            var parity = EanParity[first];
            for (int i = 0; i < 6; i++)
                modules.Append(parity[i] == 'L' ? EanL[left[i] - '0'] : EanG[left[i] - '0']);
            AddGuard("01010");
            foreach (var c in right) modules.Append(EanR[c - '0']);
            AddGuard("101");
        }

        // UPC-A: the first and last DATA digit patterns also extend down like guards.
        if (sym == "UPA")
        {
            guard.Add((3, 7));
            guard.Add((modules.Length - 10, 7));
        }

        double guardExtra = showText ? hrtH * 0.5 : 0;
        // The leading digit of an EAN-13/UPC-A prints to the LEFT of the symbol, outside
        // the field: the bars themselves still start on the field origin.
        double leadW = 0;
        double sideW = sym == "E8" ? 0 : hrtH * 1.1;
        bool IsGuardModule(int idx) => guard.Any(g => idx >= g.Start && idx < g.Start + g.Len);

        var mstr = modules.ToString();
        for (int i = 0; i < mstr.Length;)
        {
            if (mstr[i] == '0') { i++; continue; }
            int j = i;
            bool tall = false;
            while (j < mstr.Length && mstr[j] == '1' && IsGuardModule(j) == IsGuardModule(i)) { tall |= IsGuardModule(j); j++; }
            segs.Add(new BarSeg(leadW + i * module, 0, (j - i) * module, barH + (tall ? guardExtra : 0)));
            i = j;
        }
        totalW = leadW + mstr.Length * module;

        if (showText)
        {
            double ty = barH - module;
            if (sym == "E8")
            {
                labels.Add(new BarLabel(3 * module, ty, digits[..4], hrtH, 28 * module));
                labels.Add(new BarLabel(35 * module, ty, digits[4..8], hrtH, 28 * module));
            }
            else
            {
                if (sym == "UPA")
                {
                    labels.Add(new BarLabel(-sideW, ty, digits[1..2], hrtH, 0));
                    labels.Add(new BarLabel(10 * module, ty, digits[2..7], hrtH, 35 * module));
                    labels.Add(new BarLabel(50 * module, ty, digits[7..12], hrtH, 35 * module));
                    labels.Add(new BarLabel(95 * module + module, ty, digits[12..13], hrtH, 0));
                }
                else
                {
                    labels.Add(new BarLabel(-sideW, ty, digits[..1], hrtH, 0));
                    labels.Add(new BarLabel(leadW + 3 * module, ty, digits[1..7], hrtH, 42 * module));
                    labels.Add(new BarLabel(leadW + 50 * module, ty, digits[7..13], hrtH, 42 * module));
                }
            }
        }
    }

    // Strips ZPL Code 128 invocation codes (>9 subset A, >: subset B, >; subset C,
    // >8 FNC1…) from the data so the human-readable line shows the payload only.
    private static string StripCode128Invocations(string data)
    {
        if (data.IndexOf('>') < 0) return data;
        var sb = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] == '>' && i + 1 < data.Length) { i++; continue; }
            sb.Append(data[i]);
        }
        return sb.ToString();
    }

    private static bool HasCode128Invocation(string data)
    {
        int j = data.IndexOf('>');
        // Start codes >9/>:/>; and mid-stream switches >5(→C) >6(→B) >7(→A) >8(FNC1).
        return j >= 0 && j + 1 < data.Length && "56789:;".IndexOf(data[j + 1]) >= 0;
    }

    // `autoMode` = ^BC mode A (automatic subset optimization). Without it, Labelary
    // encodes everything in subset B (no digit-pair packing), so we do the same.
    private static IReadOnlyList<int> EncodeCode128(string data, bool autoMode = true) =>
        HasCode128Invocation(data) ? EncodeCode128Explicit(data)
        : autoMode ? EncodeCode128Auto(data)
        : EncodeCode128SubsetB(data);

    private static IReadOnlyList<int> EncodeCode128SubsetB(string data)
    {
        var codes = new List<int> { 104 };
        foreach (var ch in data) codes.Add(Math.Clamp(ch - 32, 0, 95));
        var checksum = codes[0];
        for (int i = 1; i < codes.Count; i++) checksum += codes[i] * i;
        codes.Add(checksum % 103);
        codes.Add(106);
        return codes;
    }

    // Honors explicit ZPL subset invocations (>9/>:/>;). Unlike the auto path this does
    // NOT re-optimize subsets: >: forces B, >; forces C. In subset C only digit pairs are
    // valid — non-numeric data (e.g. ">;SANDBOX MODE") stops the payload, yielding an
    // (intentionally) invalid start+checksum+stop barcode, exactly like Labelary.
    private static IReadOnlyList<int> EncodeCode128Explicit(string data)
    {
        static int StartCode(char s) => s == 'A' ? 103 : s == 'C' ? 105 : 104;
        static int SwitchCode(char s) => s == 'A' ? 101 : s == 'C' ? 99 : 100;

        var codes = new List<int>();
        char subset = 'B';
        bool started = false;
        void Ensure(char s)
        {
            if (!started) { codes.Add(StartCode(s)); subset = s; started = true; }
            else if (s != subset) { codes.Add(SwitchCode(s)); subset = s; }
        }

        int i = 0;
        while (i < data.Length)
        {
            if (data[i] == '>' && i + 1 < data.Length)
            {
                char c = data[i + 1];
                // Start codes (>9/>:/>;) and mid-stream subset switches (>5/>6/>7)
                // are the same operation here — Ensure() emits a start or a switch.
                if (c == '9' || c == '7') Ensure('A');
                else if (c == ':' || c == '6') Ensure('B');
                else if (c == ';' || c == '5') Ensure('C');
                else if (c == '8') { if (!started) Ensure('C'); codes.Add(102); } // FNC1 (GS1-128)
                // other >x codes are skipped
                i += 2;
                continue;
            }
            if (!started) Ensure('B');
            if (subset == 'C')
            {
                if (i + 1 < data.Length && char.IsDigit(data[i]) && char.IsDigit(data[i + 1]))
                { codes.Add((data[i] - '0') * 10 + (data[i + 1] - '0')); i += 2; }
                else break; // invalid data in subset C → emit nothing more
            }
            else
            {
                codes.Add(Math.Clamp(data[i] - 32, 0, 95));
                i++;
            }
        }
        if (!started) codes.Add(StartCode('B'));

        var checksum = codes[0];
        for (int k = 1; k < codes.Count; k++) checksum += codes[k] * k;
        codes.Add(checksum % 103);
        codes.Add(106);
        return codes;
    }

    // Code 128 with automatic subset B/C switching: runs of digits are packed two
    // per symbol (subset C), which roughly halves the width of numeric barcodes and
    // matches what printers/Labelary produce (start 104=B/105=C, 99=→C, 100=→B).
    private static IReadOnlyList<int> EncodeCode128Auto(string data)
    {
        static int DigitRun(string s, int i)
        {
            int n = 0;
            while (i + n < s.Length && char.IsDigit(s[i + n])) n++;
            return n;
        }

        var codes = new List<int>();
        int pos = 0;
        // Start in C if the data opens with an even, long-enough digit run.
        int lead = DigitRun(data, 0);
        bool modeC = lead >= 4 || (lead == data.Length && lead >= 2 && lead % 2 == 0);
        codes.Add(modeC ? 105 : 104);

        while (pos < data.Length)
        {
            if (modeC)
            {
                if (pos + 1 < data.Length && char.IsDigit(data[pos]) && char.IsDigit(data[pos + 1]))
                {
                    codes.Add((data[pos] - '0') * 10 + (data[pos + 1] - '0'));
                    pos += 2;
                }
                else { codes.Add(100); modeC = false; } // switch to B
            }
            else
            {
                int run = DigitRun(data, pos);
                bool toEnd = pos + run == data.Length;
                if (run >= 4 || (toEnd && run >= 2))
                {
                    // Switching to C only pays off on an even count. If the run is
                    // odd, emit ONE digit in B first so the C portion is even — this
                    // avoids a trailing switch-back-to-B for the leftover digit and
                    // matches what printers/Labelary produce (a symbol two modules
                    // narrower per avoided switch).
                    if (run % 2 == 1)
                    {
                        codes.Add(Math.Clamp(data[pos] - 32, 0, 95));
                        pos++;
                        continue;
                    }
                    codes.Add(99); modeC = true; // switch to C
                    continue;
                }
                codes.Add(Math.Clamp(data[pos] - 32, 0, 95));
                pos++;
            }
        }

        var checksum = codes[0];
        for (var i = 1; i < codes.Count; i++) checksum += codes[i] * i;
        codes.Add(checksum % 103);
        codes.Add(106);
        return codes;
    }

    // Handles the ZPL stored-format / field-recall mechanism (^DF/^XF/^FN). Templates
    // declared with ^DFname are captured; a later ^XFname is expanded inline with each
    // template ^FN placeholder replaced by its field data (collected from ^FNn^FDdata
    // pairs). Labels that use none of these commands pass through unchanged.
    private static IReadOnlyList<ZplToken> ExpandStoredFormats(IReadOnlyList<ZplToken> tokens)
    {
        // 1) Pull out ^DF blocks (stored, not rendered directly).
        var formats = new Dictionary<string, List<ZplToken>>(StringComparer.OrdinalIgnoreCase);
        var main = new List<ZplToken>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Command == "DF")
            {
                var name = FormatName(tokens[i].Args);
                var buf = new List<ZplToken>();
                i++;
                while (i < tokens.Count && tokens[i].Command != "XZ") { buf.Add(tokens[i]); i++; }
                formats[name] = buf; // i is at ^XZ; the for-loop's i++ skips it
                continue;
            }
            main.Add(tokens[i]);
        }

        bool usesFormat = formats.Count > 0 || main.Any(t => t.Command is "XF" or "FN");
        if (!usesFormat) return main;

        // 2) Collect field data from ^FNn ^FD… (or ^FV…) pairs.
        var fieldData = new Dictionary<int, string>();
        for (int i = 0; i + 1 < main.Count; i++)
            if (main[i].Command == "FN" && int.TryParse(main[i].Args.Trim(), out int fn)
                && main[i + 1].Command is "FD" or "FV")
                fieldData[fn] = main[i + 1].Args;

        // 3) Emit: expand ^XF (with substitution), drop the ^FN/^FD fill pairs, keep the rest.
        var output = new List<ZplToken>();
        for (int i = 0; i < main.Count; i++)
        {
            var t = main[i];
            if (t.Command == "XF")
            {
                if (formats.TryGetValue(FormatName(t.Args), out var fmt))
                    foreach (var ft in fmt)
                        output.Add(ft.Command == "FN" && int.TryParse(ft.Args.Trim(), out int n)
                            ? new ZplToken("FD", fieldData.TryGetValue(n, out var d) ? d : "", ft.Start, ft.End)
                            : ft);
                continue;
            }
            if (t.Command == "FN")
            {
                if (i + 1 < main.Count && main[i + 1].Command is "FD" or "FV")
                {
                    i++;        // ^FNn^FDdata: a fill pair, its data was collected above
                    continue;
                }
                // A bare ^FNn inside the label body (no ^DF/^XF at all — e.g. the Geodis
                // labels, whose barcodes are ^BC…^FN1^FS with the data supplied by a
                // later ^FN1^FD… pair): substitute the data in place.
                if (int.TryParse(t.Args.Trim(), out int placeholder))
                    output.Add(new ZplToken("FD", fieldData.TryGetValue(placeholder, out var pd) ? pd : "", t.Start, t.End));
                continue;
            }
            output.Add(t);
        }
        return output;
    }

    // Extracts the bare format name from ^DF/^XF args (strips a device prefix like "R:"
    // and any extension like ".ZPL").
    private static string FormatName(string args)
    {
        var s = args.Trim();
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[(colon + 1)..];
        int dot = s.IndexOf('.');
        if (dot >= 0) s = s[..dot];
        return s.Trim();
    }

    private static IReadOnlyList<ZplToken> Tokenize(string zpl)
    {
        var tokens = new List<ZplToken>();
        for (var i = 0; i < zpl.Length; i++)
        {
            if (zpl[i] is not ('^' or '~') || i + 1 >= zpl.Length)
            {
                continue;
            }

            var start = i;
            i++;
            var commandStart = i;
            if (i + 1 < zpl.Length && char.IsLetterOrDigit(zpl[i]) && char.IsLetterOrDigit(zpl[i + 1]))
            {
                i += 2;
            }
            else
            {
                i++;
            }

            var command = zpl[commandStart..i].ToUpperInvariant();
            var argsStart = i;
            while (i < zpl.Length && zpl[i] is not ('^' or '~'))
            {
                i++;
            }

            var args = zpl[argsStart..i];
            tokens.Add(new ZplToken(command, args, start, i));
            i--;
        }

        return tokens;
    }
}

public sealed record ZplFont(string Name, double Height, double Width)
{
    // Font "0" (^CF0 / ^A0) → family "Swiss 721 Condensed" + Bold weight.
    //   Using the family name without the "Bold" suffix lets DirectWrite select the
    //   bold variant within the condensed family via FontWeight, which is more reliable
    //   than passing "Swiss 721 Condensed Bold" as the family name (which DirectWrite
    //   may not recognise as a valid family and silently falls back to the non-condensed).
    // Bitmap fonts A–H → Bitstream Vera Sans Mono (fixed-pitch approximation of the
    //   Zebra bitmap faces); B is the bold face. Font B only has uppercase glyphs on
    //   real printers — the parser upper-cases its data.
    // Others → Helvetica (resolved to Arial by the Windows font substitution table).
    public string Family => Name.ToUpperInvariant() switch
    {
        // Font 0 and the P–V faces are all Zebra's CG Triumvirate Bold Condensed;
        // Swiss 721 Condensed is our substitute for it.
        "0" or "P" or "Q" or "R" or "S" or "T" or "U" or "V" => "Swiss 721 Condensed",
        "A" or "B" or "C" or "D" or "E" or "F" or "G" or "H" => "Bitstream Vera Sans Mono",
        _   => "Helvetica",
    };

    public bool Bold => Name.ToUpperInvariant() switch
    {
        "0" => true,
        "B" => true,
        "A" or "C" or "D" or "E" or "F" or "G" or "H" => false,
        _   => true,
    };

    // Base cell size (height, width) of the Zebra bitmap fonts, used when ^A gives
    // no explicit size and no ^CF applies, and for the default width:height ratio.
    public static (double H, double W) BaseCell(string name) => name.ToUpperInvariant() switch
    {
        "A" => (9, 5),
        "B" => (11, 7),
        "C" or "D" => (18, 10),
        "E" => (28, 15),
        "F" => (26, 13),
        "G" => (60, 40),
        "H" => (21, 13),
        "GS" => (24, 24),
        "P" => (20, 18),
        "Q" => (28, 24),
        "R" => (35, 31),
        "S" => (40, 35),
        "T" => (48, 42),
        "U" => (59, 53),
        "V" => (80, 71),
        _   => (15, 15), // scalable font 0: proportional (ratio 1)
    };

    // Every font except "0" is a BITMAP face: it can only be drawn at INTEGER
    // magnifications of its base cell. Asking for a size that is not a multiple
    // snaps to the nearest one, and anything under the base cell still prints at
    // the base cell (verified against Labelary: ^ABN,30,15 → 3×11 by 2×7,
    // ^AQN,10,10 → the plain 28×24 cell, ^ADN,70,70 → 4×18 by 7×10).
    public static bool IsBitmap(string name) => name.ToUpperInvariant() is
        "A" or "B" or "C" or "D" or "E" or "F" or "G" or "H" or "GS"
        or "P" or "Q" or "R" or "S" or "T" or "U" or "V";

    public static (double H, double W) Quantize(string name, double height, double width)
    {
        if (!IsBitmap(name)) return (height, width);
        var (cellH, cellW) = BaseCell(name);
        double h = cellH * Math.Max(1, Math.Round(height / cellH, MidpointRounding.AwayFromZero));
        double w = cellW * Math.Max(1, Math.Round(width / cellW, MidpointRounding.AwayFromZero));
        return (h, w);
    }

    // Each Zebra bitmap face fills its cell differently (font B and H are all-caps
    // faces whose ink IS the whole cell; A/D/F/G leave ~20 % of it blank; the P–V
    // face only ~37 %), and our substitute faces have their own cap-height ratio.
    // These two per-font corrections were measured against Labelary with a row of
    // capital 'H' at the base cell size (see the probe in the render notes):
    //   Em  — the cell height is scaled by this to get the render height that
    //         reproduces Labelary's ink height;
    //   Top  — blank band above the ink inside the cell, as a fraction of the cell.
    //          ^FO anchors the CELL top, so the ink starts that far below it;
    //   Wide — horizontal correction applied on top of Em, so the character advance
    //          matches too (our substitute faces are not exactly as wide as Zebra's).
    private static (double Em, double Top, double Wide) InkMetrics(string name) => name.ToUpperInvariant() switch
    {
        "A" => (0.92, 0.13, 1.035),
        "B" => (1.18, 0.13, 1.000),
        "C" or "D" => (0.92, 0.13, 1.035),
        "E" => (0.84, 0.13, 1.200),
        "F" => (0.95, 0.13, 0.945),
        "G" => (0.94, 0.13, 1.210),
        "H" => (1.18, 0.13, 1.095),
        "P" or "Q" or "R" or "S" or "T" or "U" or "V" => (0.833, 0.165, 1.010),
        _   => (1.0, 0.0, 1.0),  // scalable font 0: already matches
    };

    /// <summary>True for the P–V faces, which share font 0's typeface.</summary>
    public static bool IsTriumvirate(string name) =>
        name.ToUpperInvariant() is "P" or "Q" or "R" or "S" or "T" or "U" or "V";

    public static double EmRatio(string name) => InkMetrics(name).Em;

    public static double WidthRatio(string name) => InkMetrics(name).Wide;

    /// <summary>Blank band above the capital ink inside the cell, in dots.</summary>
    public double TopGap
    {
        get
        {
            var (em, top, _) = InkMetrics(Name);
            return Height / em * top;
        }
    }
}

// ── Vector PDF export ────────────────────────────────────────────────────────
// Converts the render model into a real vector PDF (paths for boxes/lines/bars,
// embedded TrueType fonts for text) instead of embedding a raster image, so
// zooming the PDF never loses quality. The app's own fonts are embedded (the
// user has them installed), so text matches the preview; if a font can't be
// embedded, a standard PDF base font is used as a fallback. Coordinates are
// emitted in ZPL dots with a top-left origin; the page CTM flips Y and scales
// dots → PostScript points using the density, so the label prints at true size.
public static partial class ZplRenderer
{
    // One PDF font resource (embedded TrueType, or a standard base font fallback).
    private sealed class FontEntry
    {
        public string ResName = "F0";
        public double CapRatio = 0.7;
        public bool Bold;
        public PdfFontEmbedder.EmbeddedFont? Embedded;
        public string BaseFont = "Helvetica";
        public int FontObj, DescObj, FileObj;
    }

    // marginDots adds an empty (white) border of that many dots on all four sides
    // of the exported page — used by the CLI's --margin option.
    public static byte[] ToPdf(ZplRenderModel model, double dpmm, double rotationDegrees, double marginDots = 0)
    {
        double w = model.Size.WidthDots;
        double h = model.Size.HeightDots;
        if (model.InvertOrientation) rotationDegrees += 180; // ^POI
        double angle = ((rotationDegrees % 360) + 360) % 360;

        // Axis-aligned bounding box of the rotated W×H rectangle (same as Draw).
        double bw, bh;
        if (angle < 0.5 || Math.Abs(angle - 180) < 0.5) { bw = w; bh = h; }
        else if (Math.Abs(angle - 90) < 0.5 || Math.Abs(angle - 270) < 0.5) { bw = h; bh = w; }
        else
        {
            var r = angle * Math.PI / 180;
            bw = w * Math.Abs(Math.Cos(r)) + h * Math.Abs(Math.Sin(r));
            bh = w * Math.Abs(Math.Sin(r)) + h * Math.Abs(Math.Cos(r));
        }

        var (ra, rb, rc, rd, re, rf) = PdfRotationMatrix(angle, w, h, bw, bh);
        if (model.MirrorImage)
        {
            // ^PMY: flip label space about the vertical centre line (x → w − x) BEFORE
            // the page rotation, folded into the page matrix so every drawable — text
            // included, which a real printer also mirrors — follows without extra work.
            (re, rf) = (re + ra * w, rf + rb * w);
            (ra, rb) = (-ra, -rb);
        }

        double dp = dpmm > 0 ? dpmm : 8;
        double s = 72.0 / (dp * 25.4);        // dots → points
        double m = Math.Max(0, marginDots);   // margin in dots, all four sides
        double pageW = (bw + 2 * m) * s, pageH = (bh + 2 * m) * s;

        // Resolve fonts lazily as the content references them; each distinct
        // (face, bold) becomes one resource, embedded when possible.
        var fontMap = new Dictionary<(string, bool), FontEntry>();
        var fontOrder = new List<FontEntry>();
        FontEntry Resolve(string face, bool bold)
        {
            face ??= "";
            var key = (face, bold);
            if (fontMap.TryGetValue(key, out var fe)) return fe;
            fe = new FontEntry { ResName = "F" + fontOrder.Count, Bold = bold };
            var emb = PdfFontEmbedder.TryLoad(face, bold);
            if (emb is not null)
            {
                fe.Embedded = emb;
                fe.CapRatio = emb.CapRatio > 0.05 ? emb.CapRatio : 0.7;
            }
            else
            {
                (fe.BaseFont, fe.CapRatio) = StandardFallback(face, bold);
            }
            fontMap[key] = fe;
            fontOrder.Add(fe);
            return fe;
        }

        // ^GF/^GFA bitmaps become image XObjects; collect them as they are drawn.
        var images = new List<ZplImage>();
        string RegisterImage(ZplImage img) { var n = "Im" + images.Count; images.Add(img); return n; }

        var body = new StringBuilder();
        body.Append("q\n");
        // Scale + Y-flip, offset by the margin so the label sits inside the border.
        body.Append($"{N(s)} 0 0 {N(-s)} {N(m * s)} {N(pageH - m * s)} cm\n");
        body.Append($"{N(ra)} {N(rb)} {N(rc)} {N(rd)} {N(re)} {N(rf)} cm\n");         // rotation (dots)
        body.Append($"1 g 0 0 {N(w)} {N(h)} re f\n");                                 // white label surface
        body.Append($"0.6 G 1 w 0 0 {N(w)} {N(h)} re S\n");                           // light gray edge (matches preview)

        // Clip the elements to the document rectangle so overflowing content
        // (e.g. text wider than the label) is cut off instead of spilling out.
        body.Append("q\n");
        body.Append($"0 0 {N(w)} {N(h)} re W n\n");

        var blackRects = new List<ZplRect>();
        foreach (var drawable in model.Drawables)
        {
            switch (drawable)
            {
                case ZplText t:    PdfText(body, t, Resolve); break;
                case ZplBox b:     PdfBox(body, b, blackRects); break;
                case ZplLine l:    PdfLine(body, l); break;
                case ZplBars bb:   PdfBars(body, bb, Resolve); break;
                case ZplEllipse el: PdfEllipse(body, el); break;
                case ZplSymbol sy: PdfSymbol(body, sy, Resolve); break;
                case ZplImage im:  PdfImage(body, im, RegisterImage); break;
                case ZplMatrix mx: PdfMatrix(body, mx); break;
                case ZplAztec az:  PdfModuleGrid(body, az.X, az.Y, az.ModuleSize, az.ModuleSize, az.Matrix); break;
                case ZplDataMatrix dm: PdfModuleGrid(body, dm.X, dm.Y, dm.ModuleSize, dm.ModuleSize, dm.Matrix); break;
                case ZplGrid gr:   PdfModuleGrid(body, gr.X, gr.Y, gr.ModW, gr.ModH, gr.Matrix); break;
            }
        }
        body.Append("Q\n"); // end clip
        body.Append("Q\n"); // end page transform

        var content = Encoding.ASCII.GetBytes(body.ToString());

        // Object numbers: 1 Catalog, 2 Pages, 3 Page, 4 Content, fonts, then images.
        int next = 5;
        foreach (var fe in fontOrder)
        {
            fe.FontObj = next++;
            if (fe.Embedded is not null) { fe.DescObj = next++; fe.FileObj = next++; }
        }
        var imageObjs = new int[images.Count];
        for (int i = 0; i < images.Count; i++) imageObjs[i] = next++;
        int size = next; // /Size = last object number + 1

        var fontDict = new StringBuilder("<< ");
        foreach (var fe in fontOrder) fontDict.Append($"/{fe.ResName} {fe.FontObj} 0 R ");
        fontDict.Append(">>");

        var xobjDict = new StringBuilder();
        if (images.Count > 0)
        {
            xobjDict.Append("/XObject << ");
            for (int i = 0; i < images.Count; i++) xobjDict.Append($"/Im{i} {imageObjs[i]} 0 R ");
            xobjDict.Append(">> ");
        }

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0 };
        void Ascii(string v) { var by = Encoding.ASCII.GetBytes(v); ms.Write(by, 0, by.Length); }
        void Obj(int n, string dict) { offsets.Add(ms.Position); Ascii($"{n} 0 obj\n{dict}\nendobj\n"); }
        void StreamObj(int n, string dict, byte[] data)
        {
            offsets.Add(ms.Position);
            Ascii($"{n} 0 obj\n{dict}\nstream\n");
            ms.Write(data, 0, data.Length);
            Ascii("\nendstream\nendobj\n");
        }

        Ascii("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pageW)} {N(pageH)}] " +
               $"/Resources << /Font {fontDict} {xobjDict}>> /Contents 4 0 R >>");
        StreamObj(4, $"<< /Length {content.Length} >>", content);

        foreach (var fe in fontOrder)
        {
            if (fe.Embedded is PdfFontEmbedder.EmbeddedFont ef)
            {
                var widths = string.Join(" ", ef.Widths);
                // CFF outlines → simple Type1 font + FontFile3 /Type1C.
                // TrueType outlines → TrueType font + FontFile2 (with /Length1).
                string subtype = ef.IsCff ? "Type1" : "TrueType";
                string fontFileRef = ef.IsCff ? "FontFile3" : "FontFile2";
                Obj(fe.FontObj,
                    $"<< /Type /Font /Subtype /{subtype} /BaseFont /{ef.PostScriptName} " +
                    $"/FirstChar 32 /LastChar 255 /Widths [{widths}] " +
                    $"/FontDescriptor {fe.DescObj} 0 R /Encoding /WinAnsiEncoding >>");
                Obj(fe.DescObj,
                    $"<< /Type /FontDescriptor /FontName /{ef.PostScriptName} /Flags {ef.Flags} " +
                    $"/FontBBox [{ef.FontBBox[0]} {ef.FontBBox[1]} {ef.FontBBox[2]} {ef.FontBBox[3]}] " +
                    $"/ItalicAngle {ef.ItalicAngle} /Ascent {ef.Ascent} /Descent {ef.Descent} " +
                    $"/CapHeight {ef.CapHeight} /StemV {(fe.Bold ? 140 : 80)} /{fontFileRef} {fe.FileObj} 0 R >>");
                string streamDict = ef.IsCff
                    ? $"<< /Subtype /Type1C /Length {ef.Program.Length} >>"
                    : $"<< /Length {ef.Program.Length} /Length1 {ef.Program.Length} >>";
                StreamObj(fe.FileObj, streamDict, ef.Program);
            }
            else
            {
                Obj(fe.FontObj, $"<< /Type /Font /Subtype /Type1 /BaseFont /{fe.BaseFont} /Encoding /WinAnsiEncoding >>");
            }
        }

        for (int i = 0; i < images.Count; i++)
        {
            var im = images[i];
            // 1-bit image mask: paints the current fill (black) where the bit is 1.
            StreamObj(imageObjs[i],
                $"<< /Type /XObject /Subtype /Image /Width {im.PixelWidth} /Height {im.PixelHeight} " +
                $"/ImageMask true /Decode [1 0] /BitsPerComponent 1 /Length {im.Bits.Length} >>",
                im.Bits);
        }

        long xref = ms.Position;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (var off in offsets.Skip(1)) Ascii($"{off:0000000000} 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    private static (string BaseFont, double CapRatio) StandardFallback(string face, bool bold)
    {
        if (face.StartsWith("Bitstream Vera", StringComparison.OrdinalIgnoreCase))
            return (bold ? "Courier-Bold" : "Courier", 0.562);
        if (face.StartsWith("Swiss 721", StringComparison.OrdinalIgnoreCase))
            return ("Helvetica-Bold", 0.717);
        return (bold ? "Helvetica-Bold" : "Helvetica", 0.717);
    }

    // Content→bounding-box affine matrix, mirroring BuildRotation (y-down space).
    private static (double, double, double, double, double, double) PdfRotationMatrix(
        double angle, double w, double h, double bw, double bh)
    {
        if (angle < 0.5) return (1, 0, 0, 1, 0, 0);
        double rad = angle * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        double cx = w / 2, cy = h / 2, tx = (bw - w) / 2, ty = (bh - h) / 2;
        double e = -cx * cos + cy * sin + cx + tx;
        double f = -cx * sin - cy * cos + cy + ty;
        return (cos, sin, -sin, cos, e, f);
    }

    private static void PdfBox(StringBuilder sb, ZplBox box, List<ZplRect> blackRects)
    {
        double w = Math.Max(1, box.Width), h = Math.Max(1, box.Height);
        double t = Math.Max(1, Math.Min(box.Thickness, Math.Min(w, h)));

        if (box.WhiteFill)
        {
            if (IsFilledBox(box)) sb.Append($"1 g {N(box.X)} {N(box.Y)} {N(w)} {N(h)} re f\n");
            else
            {
                sb.Append($"1 g {N(box.X)} {N(box.Y)} {N(w)} {N(t)} re f\n");
                sb.Append($"1 g {N(box.X)} {N(box.Y + h - t)} {N(w)} {N(t)} re f\n");
                sb.Append($"1 g {N(box.X)} {N(box.Y)} {N(t)} {N(h)} re f\n");
                sb.Append($"1 g {N(box.X + w - t)} {N(box.Y)} {N(t)} {N(h)} re f\n");
            }
            return;
        }

        if (IsFilledBox(box))
        {
            var rect = new ZplRect(box.X, box.Y, w, h);
            if (box.Reverse)
            {
                sb.Append($"1 g {N(box.X)} {N(box.Y)} {N(w)} {N(h)} re f\n");
                foreach (var v in Subtract(rect, blackRects))
                    sb.Append($"0 g {N(v.X)} {N(v.Y)} {N(v.Width)} {N(v.Height)} re f\n");
            }
            else
            {
                sb.Append($"0 g {N(box.X)} {N(box.Y)} {N(w)} {N(h)} re f\n");
                blackRects.Add(rect);
            }
            return;
        }

        string fill = box.Reverse ? "1 g" : "0 g";
        sb.Append($"{fill} {N(box.X)} {N(box.Y)} {N(w)} {N(t)} re f\n");
        sb.Append($"{fill} {N(box.X)} {N(box.Y + h - t)} {N(w)} {N(t)} re f\n");
        sb.Append($"{fill} {N(box.X)} {N(box.Y)} {N(t)} {N(h)} re f\n");
        sb.Append($"{fill} {N(box.X + w - t)} {N(box.Y)} {N(t)} {N(h)} re f\n");
    }

    private static void PdfLine(StringBuilder sb, ZplLine line)
    {
        double t = Math.Max(1, line.Thickness);
        sb.Append($"0 G {N(t)} w 0 J\n");
        sb.Append($"{N(line.X)} {N(line.Y)} m {N(line.X + line.Width)} {N(line.Y + line.Height)} l S\n");
    }

    private static void PdfText(StringBuilder sb, ZplText text, Func<string, bool, FontEntry> resolve)
    {
        double zh = Math.Max(8, text.Height);
        var fe = resolve(text.Font, text.Bold);
        double fs = zh * GlyphScaleFactor(text.Font); // match the preview render size
        string color = text.Reverse ? "1 g" : "0 g";

        double r = text.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        // ^A0 width parameter → horizontal glyph scale (w<h = condensed); applied to
        // the glyph-x column (a,b) of the text matrix.
        double condense = text.Height > 0 && text.Width > 0 ? text.Width / text.Height : 1.0;

        // Baseline start point. ^FT (or unrotated ^FO): baseline anchor at (X, Y[+BF·h]).
        // Rotated ^FO: the ROTATED bounding box's top-left sits at (X, Y) — mirror the
        // preview's translation, then map the local baseline point through the rotation.
        double baseX, baseY;
        if (text.Baseline || text.Rotation == 0)
        {
            baseX = text.X;
            baseY = text.Baseline ? text.Y : text.Y + zh * BaselineFraction;
        }
        else
        {
            double bf = zh * BaselineFraction;
            double tw = MeasureTextWidthByFamily(text.Text, text.Font, zh, text.Bold) * condense;
            (baseX, baseY) = text.Rotation switch
            {
                90  => (text.X + zh - bf, text.Y),
                180 => (text.X + tw, text.Y + zh - bf),
                270 => (text.X + bf, text.Y + tw),
                _   => (text.X, text.Y + bf),
            };
        }

        // Tm rotates the text r° clockwise around the baseline origin and flips the
        // glyph Y so it stays upright under the page's own Y-flip.
        var lines = text.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            double ox = baseX + (-sin) * zh * i; // next line: perpendicular to reading dir
            double oy = baseY + cos * zh * i;
            sb.Append("BT\n");
            sb.Append(color).Append('\n');
            sb.Append($"/{fe.ResName} {N(fs)} Tf\n");
            sb.Append($"{N(cos * condense)} {N(sin * condense)} {N(sin)} {N(-cos)} {N(ox)} {N(oy)} Tm\n");
            sb.Append($"({PdfString(lines[i])}) Tj\nET\n");
        }
    }

    private static void PdfBars(StringBuilder sb, ZplBars bars, Func<string, bool, FontEntry> resolve)
    {
        double W = bars.Width, H = bars.Height;
        (double X, double Y, double W, double H) Map(BarSeg s) => bars.Rotation switch
        {
            90  => (bars.X + H - s.Y - s.H, bars.Y + s.X, s.H, s.W),
            180 => (bars.X + W - s.X - s.W, bars.Y + H - s.Y - s.H, s.W, s.H),
            270 => (bars.X + s.Y, bars.Y + W - s.X - s.W, s.H, s.W),
            _   => (bars.X + s.X, bars.Y + s.Y, s.W, s.H),
        };

        sb.Append("0 g\n");
        foreach (var s in bars.Segs)
        {
            var m = Map(s);
            sb.Append($"{N(m.X)} {N(m.Y)} {N(Math.Max(1, m.W))} {N(Math.Max(1, m.H))} re f\n");
        }

        var fe = resolve("Bitstream Vera Sans Mono", false);
        double rad = bars.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        foreach (var l in bars.Labels)
        {
            double fsz = l.FontHeight;
            double textW = l.Text.Length * 0.6 * fsz;       // monospace advance ≈ 0.6 em
            double lx = l.CenterWidth > 0 ? l.X + (l.CenterWidth - textW) / 2 : l.X;
            double lby = l.Y + fsz * fe.CapRatio;           // local baseline
            // Map the local baseline point through the rotation + bbox placement.
            (double ox, double oy) = bars.Rotation switch
            {
                90  => (bars.X + H - lby, bars.Y + lx),
                180 => (bars.X + W - lx, bars.Y + H - lby),
                270 => (bars.X + lby, bars.Y + W - lx),
                _   => (bars.X + lx, bars.Y + lby),
            };
            sb.Append("BT\n0 g\n");
            sb.Append($"/{fe.ResName} {N(fsz)} Tf\n");
            sb.Append($"{N(cos)} {N(sin)} {N(sin)} {N(-cos)} {N(ox)} {N(oy)} Tm\n");
            sb.Append($"({PdfString(l.Text)}) Tj\nET\n");
        }
    }

    // Ellipse via 4 Bézier arcs (kappa approximation); filled when the border
    // thickness reaches half the smaller radius.
    private static void PdfEllipse(StringBuilder sb, ZplEllipse el)
    {
        void Arcs(double x, double y, double w, double h)
        {
            const double k = 0.5523;
            double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
            sb.Append($"{N(cx + rx)} {N(cy)} m\n");
            sb.Append($"{N(cx + rx)} {N(cy + k * ry)} {N(cx + k * rx)} {N(cy + ry)} {N(cx)} {N(cy + ry)} c\n");
            sb.Append($"{N(cx - k * rx)} {N(cy + ry)} {N(cx - rx)} {N(cy + k * ry)} {N(cx - rx)} {N(cy)} c\n");
            sb.Append($"{N(cx - rx)} {N(cy - k * ry)} {N(cx - k * rx)} {N(cy - ry)} {N(cx)} {N(cy - ry)} c\n");
            sb.Append($"{N(cx + k * rx)} {N(cy - ry)} {N(cx + rx)} {N(cy - k * ry)} {N(cx + rx)} {N(cy)} c\n");
        }

        bool filled = el.Thickness >= Math.Min(el.Width, el.Height) / 2;
        if (filled)
        {
            sb.Append("0 g\n");
            Arcs(el.X, el.Y, el.Width, el.Height);
            sb.Append("f\n");
        }
        else
        {
            // Stroke along the centerline of the border band.
            sb.Append($"0 G {N(el.Thickness)} w\n");
            Arcs(el.X + el.Thickness / 2, el.Y + el.Thickness / 2,
                 el.Width - el.Thickness, el.Height - el.Thickness);
            sb.Append("S\n");
        }
    }

    private static void PdfSymbol(StringBuilder sb, ZplSymbol sym, Func<string, bool, FontEntry> resolve)
    {
        if (sym.Code is 'A' or 'B' or 'C')
        {
            var fe = resolve("Helvetica", false);
            string glyph = sym.Code switch { 'A' => "®", 'B' => "©", _ => "™" };
            sb.Append("BT\n0 g\n");
            sb.Append($"/{fe.ResName} {N(sym.Height)} Tf\n");
            sb.Append($"1 0 0 -1 {N(sym.X)} {N(sym.Y + sym.Height * 0.75)} Tm\n");
            sb.Append($"({PdfString(glyph)}) Tj\nET\n");
            return;
        }
        double d = Math.Min(sym.Width, sym.Height);
        PdfEllipse(sb, new ZplEllipse(sym.X, sym.Y, d, d, Math.Max(2, d * 0.06)));
        var feb = resolve("Helvetica", true);
        string txt = sym.Code == 'D' ? "UL" : "CSA";
        double fs = d * (sym.Code == 'D' ? 0.42 : 0.30);
        double tw = txt.Length * fs * 0.62;
        sb.Append("BT\n0 g\n");
        sb.Append($"/{feb.ResName} {N(fs)} Tf\n");
        sb.Append($"1 0 0 -1 {N(sym.X + (d - tw) / 2)} {N(sym.Y + d * 0.30 + fs * 0.72)} Tm\n");
        sb.Append($"({PdfString(txt)}) Tj\nET\n");
    }

    private static void PdfImage(StringBuilder sb, ZplImage img, Func<ZplImage, string> register)
    {
        var name = register(img);
        // Map the unit image square to [X, X+W]×[topY, topY+H] with row 0 at the top
        // (the -H flips the image the right way up in the y-down content space).
        sb.Append("q\n0 g\n");
        sb.Append($"{N(img.PixelWidth)} 0 0 {N(-img.PixelHeight)} {N(img.X)} {N(img.Y + img.PixelHeight)} cm\n");
        sb.Append($"/{name} Do\nQ\n");
    }

    private static void PdfModuleGrid(StringBuilder sb, double x, double y, double mw, double mh, bool[,] matrix)
    {
        int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
        sb.Append("0 g\n");
        for (int r = 0; r < rows; r++)
        {
            int c = 0;
            while (c < cols)
            {
                if (!matrix[r, c]) { c++; continue; }
                int c2 = c;
                while (c2 < cols && matrix[r, c2]) c2++;
                sb.Append($"{N(x + c * mw)} {N(y + r * mh)} {N((c2 - c) * mw)} {N(mh)} re f\n");
                c = c2;
            }
        }
    }

    private static void PdfMatrix(StringBuilder sb, ZplMatrix m)
    {
        double s = m.Size;
        sb.Append($"1 g {N(m.X)} {N(m.Y)} {N(s)} {N(s)} re f\n");
        double t = Math.Max(1, s * 0.04);
        sb.Append($"0 G {N(t)} w {N(m.X)} {N(m.Y)} {N(s)} {N(s)} re S\n");
        const int n = 10;
        double cell = s / n;
        sb.Append("0 g\n");
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                if (((r * 7 + c * 13) & 2) == 0)
                    sb.Append($"{N(m.X + c * cell)} {N(m.Y + r * cell)} {N(cell)} {N(cell)} re f\n");
    }

    private static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // PDF literal-string escaping; non-ASCII → WinAnsi octal escape, unmappable → '?'.
    private static string PdfString(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch == '\\') sb.Append("\\\\");
            else if (ch == '(') sb.Append("\\(");
            else if (ch == ')') sb.Append("\\)");
            else if (ch < 32 || ch > 126)
            {
                int code = ch;
                if (code < 256) sb.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
                else sb.Append('?');
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}

// Start/End bracket the token in the ORIGINAL ZPL text, so an element on the
// preview can be traced back to the code that produced it (End is exclusive).
public sealed record ZplToken(string Command, string Args, int Start = 0, int End = 0);
public sealed record BarcodeRun(bool Black, int Width);
public sealed record ZplRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public static partial class ZplRenderer
{
    private static readonly string[] Code128Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    };
}