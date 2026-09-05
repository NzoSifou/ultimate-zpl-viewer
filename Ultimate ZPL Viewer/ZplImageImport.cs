using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// ── Turning a picture into a ^GF field ──────────────────────────────────────
// A printer has one ink and no half-tones: every pixel is either burnt or left
// blank. So an imported image is not "shrunk and embedded" — it is REDRAWN, at
// the exact dot size it will occupy on the label, with each pixel decided one way
// or the other. That decision is the whole job, and it is why the import offers
// two ways of making it: a threshold, which keeps a logo crisp, and dithering,
// which trades crispness for the shades a photograph needs.
//
// The result is written as ^GFA — plain ASCII hex, which every Zebra reads —
// compressed with the standard ACS run-length scheme. Compression matters here:
// a logo is mostly blank, and uncompressed hex would put a hundred kilobytes of
// "0" into the editor for a picture worth two.
internal static class ZplImageImport
{
    /// <summary>A 1-bit image, rows padded to whole bytes; a set bit prints.</summary>
    public sealed record Mono(int Width, int Height, byte[] Bits);

    // A guard, not a preference: a 300 dpi label is ~2400 dots across, and the
    // hex for anything past this would be unusable in an editor long before the
    // printer refused it.
    public const int MaxSide = 4000;

    public static readonly string[] Extensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>Reads the file into memory, detached from the file on disk.</summary>
    public static Bitmap Load(string path)
    {
        // Bitmap keeps the file locked for as long as it lives when constructed
        // from a path; copying it frees the user's file immediately.
        using var original = new Bitmap(path);
        return new Bitmap(original);
    }

    /// <summary>
    /// Scales the picture to the dot size it will occupy and decides every pixel.
    /// Transparency becomes white — unprinted — because that is what "nothing" is
    /// on a label.
    /// </summary>
    public static Mono Convert(Bitmap source, int width, int height,
                               bool dither, double threshold, bool invert)
    {
        width = Math.Clamp(width, 1, MaxSide);
        height = Math.Clamp(height, 1, MaxSide);

        var gray = Sample(source, width, height);
        double cut = Math.Clamp(threshold, 0, 1) * 255.0;

        int bytesPerRow = (width + 7) / 8;
        var bits = new byte[bytesPerRow * height];

        if (dither) Diffuse(gray, width, height, cut);

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool black = gray[y * width + x] < cut;
                if (invert) black = !black;
                if (black) bits[y * bytesPerRow + x / 8] |= (byte)(0x80 >> (x & 7));
            }

        return new Mono(width, height, bits);
    }

    // ── Grey levels ─────────────────────────────────────────────────────────

    // Draws the source at the target size over white, then reads its luminance.
    // Bicubic on the way down is what keeps small type in a logo legible.
    private static double[] Sample(Bitmap source, int width, int height)
    {
        using var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var gray = new double[width * height];
        var data = scaled.LockBits(new Rectangle(0, 0, width, height),
                                   ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = new byte[width * 4];
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    byte b = row[x * 4], v = row[x * 4 + 1], r = row[x * 4 + 2];
                    gray[y * width + x] = 0.299 * r + 0.587 * v + 0.114 * b;
                }
            }
        }
        finally { scaled.UnlockBits(data); }
        return gray;
    }

    // Floyd-Steinberg: the error made rounding one pixel is handed to the ones
    // that come after it, so an area that is half grey ends up half black rather
    // than entirely one or the other.
    private static void Diffuse(double[] gray, int width, int height, double cut)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                double old = gray[i];
                double now = old < cut ? 0 : 255;
                gray[i] = now;
                double error = old - now;

                void Spread(int dx, int dy, double share)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= width || ny >= height) return;
                    gray[ny * width + nx] += error * share;
                }
                Spread(1, 0, 7 / 16.0);
                Spread(-1, 1, 3 / 16.0);
                Spread(0, 1, 5 / 16.0);
                Spread(1, 1, 1 / 16.0);
            }
    }

    // ── The ZPL ─────────────────────────────────────────────────────────────

    /// <summary>The complete ^GFA command for this bitmap.</summary>
    public static string ToGraphicField(Mono mono)
    {
        int bytesPerRow = (mono.Width + 7) / 8;
        int total = bytesPerRow * mono.Height;
        var body = new StringBuilder(total);
        string? previous = null;

        var row = new StringBuilder(bytesPerRow * 2);
        for (int y = 0; y < mono.Height; y++)
        {
            row.Clear();
            for (int i = 0; i < bytesPerRow; i++)
            {
                byte value = mono.Bits[y * bytesPerRow + i];
                row.Append(Nibble(value >> 4)).Append(Nibble(value & 0xF));
            }
            string hex = row.ToString();
            // ':' repeats the row above verbatim — the cheapest thing in the format,
            // and blank margins are made of nothing else.
            if (hex == previous) { body.Append(':'); continue; }
            previous = hex;
            Compress(body, hex);
        }

        string n(int v) => v.ToString(CultureInfo.InvariantCulture);
        return $"^GFA,{n(total)},{n(total)},{n(bytesPerRow)},{body}";
    }

    private static char Nibble(int value) => "0123456789ABCDEF"[value & 0xF];

    // ACS run-length: a letter (or a pair) says how many times the nibble that
    // follows repeats, and a ',' ends the row, filling the rest with blank.
    private static void Compress(StringBuilder into, string hex)
    {
        int end = hex.Length;
        while (end > 0 && hex[end - 1] == '0') end--;   // the tail the ',' stands for

        int i = 0;
        while (i < end)
        {
            int run = 1;
            while (i + run < end && hex[i + run] == hex[i]) run++;
            // Two of a kind cost the same either way; below three, spell it out.
            if (run < 3)
            {
                for (int k = 0; k < run; k++) into.Append(hex[i]);
            }
            else
            {
                int left = run;
                while (left > 0)
                {
                    int take = Math.Min(left, 400);
                    AppendCount(into, take);
                    into.Append(hex[i]);
                    left -= take;
                }
            }
            i += run;
        }

        if (end < hex.Length) into.Append(',');
    }

    // G..Y count 1..19, g..z count 20..400 in steps of twenty; the two combine.
    private static void AppendCount(StringBuilder into, int count)
    {
        if (count <= 1) return;                       // a lone nibble needs no count
        int twenties = count / 20, ones = count % 20;
        if (twenties > 0) into.Append((char)('g' + twenties - 1));
        if (ones > 0) into.Append((char)('G' + ones - 1));
    }
}
