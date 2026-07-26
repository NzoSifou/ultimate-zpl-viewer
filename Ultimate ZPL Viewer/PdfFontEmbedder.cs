using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// Extracts an installed TrueType font (the exact file the app renders with) so it
// can be embedded in the exported PDF — the glyphs then match the on-screen
// preview instead of being approximated by the standard PDF fonts. Uses GDI
// GetFontData for the raw bytes + GetCharWidth32 for advances, and parses the
// TrueType tables for the font descriptor. Returns null when the requested font
// is not a plain TrueType-outline file (the caller then falls back to a base font).
public static class PdfFontEmbedder
{
    public sealed class EmbeddedFont
    {
        // The font program to embed: the whole TrueType file (IsCff == false,
        // → FontFile2) or the bare CFF table extracted from an OpenType/CFF font
        // (IsCff == true, → FontFile3 /Type1C).
        public byte[] Program = Array.Empty<byte>();
        public bool IsCff;
        public int[] Widths = Array.Empty<int>(); // advances for chars 32..255, in 1000-unit em
        public double CapRatio = 0.7;              // cap height / em, for sizing text
        public int Ascent, Descent, CapHeight, ItalicAngle;
        public int[] FontBBox = { 0, 0, 1000, 1000 };
        public int Flags = 32;
        public string PostScriptName = "EmbeddedFont";
    }

    private static readonly Dictionary<(string, bool), EmbeddedFont?> Cache = new();

    public static EmbeddedFont? TryLoad(string faceName, bool bold)
    {
        var key = (faceName, bold);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        EmbeddedFont? result = null;
        try { result = Load(faceName, bold); } catch { result = null; }
        Cache[key] = result;
        return result;
    }

    private static EmbeddedFont? Load(string faceName, bool bold)
    {
        const int em = 1000;
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;

        IntPtr hFont = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hFont = CreateFontW(-em, 0, 0, 0, bold ? 700 : 400, 0, 0, 0,
                0 /*ANSI_CHARSET*/, 0, 0, 0, 0, faceName);
            if (hFont == IntPtr.Zero) return null;
            old = SelectObject(hdc, hFont);

            uint size = GetFontData(hdc, 0, 0, null, 0);
            if (size == 0 || size == 0xFFFFFFFF) return null;
            var ttf = new byte[size];
            if (GetFontData(hdc, 0, 0, ttf, size) != size) return null;

            uint sfnt = U32(ttf, 0);
            // 0x00010000 / 'true' = TrueType outlines (embed whole file as FontFile2).
            // 'OTTO' = OpenType with CFF outlines (embed the CFF table as Type1C).
            // TTC ('ttcf') and anything else are unsupported.
            bool isCff;
            byte[] program;
            if (sfnt == 0x00010000 || sfnt == 0x74727565)
            {
                isCff = false;
                program = ttf;
            }
            else if (sfnt == 0x4F54544F) // 'OTTO'
            {
                if (!FindTable(ttf, "CFF ", out int cffOff, out int cffLen)) return null;
                if (cffLen <= 0 || cffOff + cffLen > ttf.Length) cffLen = ttf.Length - cffOff;
                program = new byte[cffLen];
                Array.Copy(ttf, cffOff, program, 0, cffLen);
                isCff = true;
            }
            else return null;

            if (!FindTable(ttf, "head", out int head, out _)) return null;
            int unitsPerEm = U16(ttf, head + 18);
            if (unitsPerEm <= 0) return null;
            double scale = 1000.0 / unitsPerEm;

            int xMin = S16(ttf, head + 36), yMin = S16(ttf, head + 38);
            int xMax = S16(ttf, head + 40), yMax = S16(ttf, head + 42);

            int ascent = 800, descent = -200;
            if (FindTable(ttf, "hhea", out int hhea, out _))
            {
                ascent = S16(ttf, hhea + 4);
                descent = S16(ttf, hhea + 6);
            }

            int capHeight = 0;
            if (FindTable(ttf, "OS/2", out int os2, out int os2Len))
            {
                int ver = U16(ttf, os2);
                if (ver >= 2 && os2Len >= 90) capHeight = S16(ttf, os2 + 88);
            }

            int italicAngle = 0;
            bool fixedPitch = false;
            if (FindTable(ttf, "post", out int post, out _))
            {
                italicAngle = (int)Math.Round((int)U32(ttf, post + 4) / 65536.0);
                fixedPitch = U32(ttf, post + 12) != 0;
            }

            var widths = new int[224]; // 32..255
            GetCharWidth32W(hdc, 32, 255, widths);

            int Sc(int v) => (int)Math.Round(v * scale);
            int capScaled = capHeight > 0 ? Sc(capHeight) : 700;

            return new EmbeddedFont
            {
                Program = program,
                IsCff = isCff,
                Widths = widths,
                CapRatio = capScaled / 1000.0,
                Ascent = Sc(ascent),
                Descent = Sc(descent),
                CapHeight = capScaled,
                ItalicAngle = italicAngle,
                FontBBox = new[] { Sc(xMin), Sc(yMin), Sc(xMax), Sc(yMax) },
                Flags = 32 | (fixedPitch ? 1 : 0),
                PostScriptName = SanitizeName(faceName) + (bold ? "-Bold" : ""),
            };
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(hdc, old);
            if (hFont != IntPtr.Zero) DeleteObject(hFont);
            DeleteDC(hdc);
        }
    }

    private static string SanitizeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (ch > 32 && ch < 127 && ch != '/' && ch != '(' && ch != ')' && ch != '<' &&
                ch != '>' && ch != '[' && ch != ']' && ch != '{' && ch != '}' && ch != '%' && ch != '#')
                sb.Append(ch);
        return sb.Length > 0 ? sb.ToString() : "Font";
    }

    private static bool FindTable(byte[] f, string tag, out int offset, out int length)
    {
        int num = U16(f, 4);
        for (int i = 0; i < num; i++)
        {
            int rec = 12 + i * 16;
            if (rec + 16 > f.Length) break;
            if (Encoding.ASCII.GetString(f, rec, 4) == tag)
            {
                offset = (int)U32(f, rec + 8);
                length = (int)U32(f, rec + 12);
                return offset >= 0 && offset < f.Length;
            }
        }
        offset = length = 0;
        return false;
    }

    private static ushort U16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
    private static short S16(byte[] b, int o) => (short)U16(b, o);
    private static uint U32(byte[] b, int o) =>
        ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement,
        int nOrientation, int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut,
        uint fdwCharSet, uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality,
        uint fdwPitchAndFamily, string lpszFace);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetFontData(IntPtr hdc, uint dwTable, uint dwOffset, byte[]? lpvBuffer, uint cbData);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetCharWidth32W(IntPtr hdc, uint first, uint last, int[] buffer);
}
