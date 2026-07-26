using System;
using System.Collections.Generic;

namespace Ultimate_ZPL_Viewer;

// QR Code model-2 encoder (ISO/IEC 18004), byte mode, versions 1–14, the four ECC
// levels and standard mask selection by penalty score. Used by ^BQ fields.
public static class QrEncoder
{
    // (ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data) per version (1-based) and level.
    // Levels indexed L=0, M=1, Q=2, H=3.
    private static readonly int[,][] Blocks = BuildBlocks();

    private static int[,][] BuildBlocks()
    {
        var t = new int[15, 4][];
        void S(int v, int l, int ec, int b1, int d1, int b2 = 0, int d2 = 0) => t[v, l] = new[] { ec, b1, d1, b2, d2 };
        S(1, 0, 7, 1, 19);   S(1, 1, 10, 1, 16);  S(1, 2, 13, 1, 13);  S(1, 3, 17, 1, 9);
        S(2, 0, 10, 1, 34);  S(2, 1, 16, 1, 28);  S(2, 2, 22, 1, 22);  S(2, 3, 28, 1, 16);
        S(3, 0, 15, 1, 55);  S(3, 1, 26, 1, 44);  S(3, 2, 18, 2, 17);  S(3, 3, 22, 2, 13);
        S(4, 0, 20, 1, 80);  S(4, 1, 18, 2, 32);  S(4, 2, 26, 2, 24);  S(4, 3, 16, 4, 9);
        S(5, 0, 26, 1, 108); S(5, 1, 24, 2, 43);  S(5, 2, 18, 2, 15, 2, 16); S(5, 3, 22, 2, 11, 2, 12);
        S(6, 0, 18, 2, 68);  S(6, 1, 16, 4, 27);  S(6, 2, 24, 4, 19);  S(6, 3, 28, 4, 15);
        S(7, 0, 20, 2, 78);  S(7, 1, 18, 4, 31);  S(7, 2, 18, 2, 14, 4, 15); S(7, 3, 26, 4, 13, 1, 14);
        S(8, 0, 24, 2, 97);  S(8, 1, 22, 2, 38, 2, 39); S(8, 2, 22, 4, 18, 2, 19); S(8, 3, 26, 4, 14, 2, 15);
        S(9, 0, 30, 2, 116); S(9, 1, 22, 3, 36, 2, 37); S(9, 2, 20, 4, 16, 4, 17); S(9, 3, 24, 4, 12, 4, 13);
        S(10, 0, 18, 2, 68, 2, 69); S(10, 1, 26, 4, 43, 1, 44); S(10, 2, 24, 6, 19, 2, 20); S(10, 3, 28, 6, 15, 2, 16);
        S(11, 0, 20, 4, 81); S(11, 1, 30, 1, 50, 4, 51); S(11, 2, 28, 4, 22, 4, 23); S(11, 3, 24, 3, 12, 8, 13);
        S(12, 0, 24, 2, 92, 2, 93); S(12, 1, 22, 6, 36, 2, 37); S(12, 2, 26, 4, 20, 6, 21); S(12, 3, 28, 7, 14, 4, 15);
        S(13, 0, 26, 4, 107); S(13, 1, 22, 8, 37, 1, 38); S(13, 2, 24, 8, 20, 4, 21); S(13, 3, 22, 12, 11, 4, 12);
        S(14, 0, 30, 3, 115, 1, 116); S(14, 1, 24, 4, 40, 5, 41); S(14, 2, 20, 11, 16, 5, 17); S(14, 3, 24, 11, 12, 5, 13);
        return t;
    }

    private static readonly int[][] AlignPos =
    {
        Array.Empty<int>(),                   // v1
        new[]{6,18}, new[]{6,22}, new[]{6,26}, new[]{6,30}, new[]{6,34},
        new[]{6,22,38}, new[]{6,24,42}, new[]{6,26,46}, new[]{6,28,50},
        new[]{6,30,54}, new[]{6,32,58}, new[]{6,34,62}, new[]{6,26,46,66},
    };

    // GF(256) with primitive polynomial 0x11D.
    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];
    static QrEncoder()
    {
        int v = 1;
        for (int i = 0; i < 255; i++) { Exp[i] = v; Log[v] = i; v <<= 1; if (v >= 256) v ^= 0x11D; }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }
    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];

    // Debug hook: returns the final interleaved codeword stream (data + EC).
    public static IReadOnlyList<int> DebugCodewords(byte[] data, char eccLevel)
    {
        DebugCapture = new List<int>();
        Encode(data, eccLevel);
        var result = DebugCapture;
        DebugCapture = null;
        return result!;
    }
    private static List<int>? DebugCapture;

    public static bool[,] Encode(byte[] data, char eccLevel)
    {
        int level = char.ToUpperInvariant(eccLevel) switch { 'L' => 0, 'M' => 1, 'H' => 3, _ => 2 };

        // Pick the smallest version whose data capacity fits byte mode.
        int version = -1, dataCw = 0;
        for (int vTry = 1; vTry <= 14; vTry++)
        {
            var b = Blocks[vTry, level];
            int cap = b[1] * b[2] + b[3] * b[4];
            int countBits = vTry <= 9 ? 8 : 16;
            int needed = (4 + countBits + data.Length * 8 + 7) / 8;
            if (needed <= cap) { version = vTry; dataCw = cap; break; }
        }
        if (version < 0) throw new InvalidOperationException("Données trop volumineuses pour un QR ≤ v14.");

        // ── Bit stream: mode 0100 + count + data + terminator + pad ──
        var bits = new List<bool>();
        void Put(int value, int count) { for (int k = count - 1; k >= 0; k--) bits.Add(((value >> k) & 1) != 0); }
        Put(0b0100, 4);
        Put(data.Length, version <= 9 ? 8 : 16);
        foreach (var by in data) Put(by, 8);
        int capacityBits = dataCw * 8;
        int term = Math.Min(4, capacityBits - bits.Count);
        Put(0, term);
        while (bits.Count % 8 != 0) bits.Add(false);
        var padBytes = new[] { 0xEC, 0x11 };
        int padIdx = 0;
        while (bits.Count < capacityBits) Put(padBytes[padIdx++ % 2], 8);

        var dataBytes = new int[dataCw];
        for (int i = 0; i < dataCw; i++)
            for (int k = 0; k < 8; k++)
                if (bits[i * 8 + k]) dataBytes[i] |= 1 << (7 - k);

        // ── Reed-Solomon per block + interleaving ──
        var spec = Blocks[version, level];
        int ecPer = spec[0];
        var blocks = new List<(int[] Data, int[] Ec)>();
        int pos = 0;
        for (int g = 0; g < 2; g++)
        {
            int nBlocks = spec[1 + g * 2], nData = spec[2 + g * 2];
            for (int b = 0; b < nBlocks; b++)
            {
                var d = new int[nData];
                Array.Copy(dataBytes, pos, d, 0, nData);
                pos += nData;
                blocks.Add((d, RsEncode(d, ecPer)));
            }
        }

        var final = new List<int>();
        int maxData = 0;
        foreach (var b in blocks) maxData = Math.Max(maxData, b.Data.Length);
        for (int i = 0; i < maxData; i++)
            foreach (var b in blocks)
                if (i < b.Data.Length) final.Add(b.Data[i]);
        for (int i = 0; i < ecPer; i++)
            foreach (var b in blocks)
                final.Add(b.Ec[i]);
        DebugCapture?.AddRange(final);

        // ── Matrix ──
        int size = 17 + version * 4;
        var modules = new bool[size, size];
        var reserved = new bool[size, size];

        void SetFunc(int r, int c, bool val) { modules[r, c] = val; reserved[r, c] = true; }

        void Finder(int r, int c)
        {
            for (int dr = -1; dr <= 7; dr++)
                for (int dc = -1; dc <= 7; dc++)
                {
                    int rr = r + dr, cc = c + dc;
                    if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;
                    bool dark = dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6 &&
                                (dr == 0 || dr == 6 || dc == 0 || dc == 6 || (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4));
                    SetFunc(rr, cc, dark);
                }
        }
        Finder(0, 0); Finder(0, size - 7); Finder(size - 7, 0);

        // Alignment patterns (skip those overlapping finders).
        var ap = AlignPos[version - 1];
        foreach (var ar in ap)
            foreach (var ac in ap)
            {
                if (reserved[ar, ac]) continue;
                for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                        SetFunc(ar + dr, ac + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }

        // Timing patterns.
        for (int i = 8; i < size - 8; i++)
        {
            if (!reserved[6, i]) SetFunc(6, i, i % 2 == 0);
            if (!reserved[i, 6]) SetFunc(i, 6, i % 2 == 0);
        }

        // Dark module + reserve format/version areas — exactly the 15+15 format
        // modules (over-reserving even one module shifts the whole data stream).
        SetFunc(size - 8, 8, true);
        for (int i = 0; i <= 8; i++)
            if (i != 6) { reserved[8, i] = true; reserved[i, 8] = true; }
        for (int i = 0; i <= 7; i++) reserved[8, size - 1 - i] = true;
        for (int i = 0; i <= 6; i++) reserved[size - 1 - i, 8] = true;
        reserved[8, 8] = true;
        if (version >= 7)
            for (int i = 0; i < 6; i++)
                for (int k = 0; k < 3; k++)
                {
                    reserved[size - 11 + k, i] = true;
                    reserved[i, size - 11 + k] = true;
                }

        // ── Data placement (zig-zag columns from the right) ──
        var dataBits = new List<bool>();
        foreach (var b in final) for (int k = 7; k >= 0; k--) dataBits.Add(((b >> k) & 1) != 0);
        int bitIdx = 0;
        for (int col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--; // skip the vertical timing column
            bool upward = ((size - 1 - col) / 2) % 2 == 0;
            for (int i = 0; i < size; i++)
            {
                int row = upward ? size - 1 - i : i;
                for (int dc = 0; dc < 2; dc++)
                {
                    int cc = col - dc;
                    if (reserved[row, cc]) continue;
                    modules[row, cc] = bitIdx < dataBits.Count && dataBits[bitIdx];
                    bitIdx++;
                }
            }
        }

        // ── Mask selection by penalty ──
        int bestMask = 0, bestPenalty = int.MaxValue;
        bool[,]? bestGrid = null;
        for (int mask = 0; mask < 8; mask++)
        {
            var g = ApplyMask(modules, reserved, mask, size);
            WriteFormat(g, size, level, mask);
            if (version >= 7) WriteVersion(g, size, version);
            int p = Penalty(g, size);
            if (p < bestPenalty) { bestPenalty = p; bestMask = mask; bestGrid = g; }
        }
        return bestGrid!;
    }

    private static int[] RsEncode(int[] data, int ec)
    {
        // Generator polynomial ∏ (x − α^i) for i = 0..ec−1, leading coefficient first.
        var gen = new int[] { 1 };
        for (int i = 0; i < ec; i++)
            gen = MulPoly(gen, new[] { 1, Exp[i] });

        var rem = new int[ec];
        foreach (var d in data)
        {
            int factor = d ^ rem[0];
            for (int j = 0; j < ec; j++)
            {
                int next = j < ec - 1 ? rem[j + 1] : 0;
                rem[j] = next ^ Mul(factor, gen[j + 1]);
            }
        }
        return rem;
    }

    private static int[] MulPoly(int[] a, int[] b)
    {
        var res = new int[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                res[i + j] ^= Mul(a[i], b[j]);
        return res;
    }

    private static bool[,] ApplyMask(bool[,] modules, bool[,] reserved, int mask, int size)
    {
        var g = (bool[,])modules.Clone();
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                if (reserved[r, c]) continue;
                bool flip = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => r * c % 2 + r * c % 3 == 0,
                    6 => (r * c % 2 + r * c % 3) % 2 == 0,
                    _ => ((r + c) % 2 + r * c % 3) % 2 == 0,
                };
                if (flip) g[r, c] = !g[r, c];
            }
        return g;
    }

    private static void WriteFormat(bool[,] g, int size, int level, int mask)
    {
        int[] levelBits = { 0b01, 0b00, 0b11, 0b10 }; // L, M, Q, H
        int format = (levelBits[level] << 3) | mask;
        int rem = format << 10;
        for (int i = 14; i >= 10; i--)
            if (((rem >> i) & 1) != 0) rem ^= 0b10100110111 << (i - 10);
        int bits = ((format << 10) | rem) ^ 0b101010000010010;

        // Standard placement, bit i = (bits >> i) & 1 (LSB first).
        for (int i = 0; i < 15; i++)
        {
            bool v = ((bits >> i) & 1) != 0;
            // Copy 1: down the left of the top-right finder / across under the top-left.
            if (i < 6) g[i, 8] = v;
            else if (i < 8) g[i + 1, 8] = v;   // skips the timing row
            else g[size - 15 + i, 8] = v;
            // Copy 2: right side of row 8 / top of column 8.
            if (i < 8) g[8, size - 1 - i] = v;
            else if (i < 9) g[8, 7] = v;       // skips the timing column
            else g[8, 14 - i] = v;
        }
        g[size - 8, 8] = true; // dark module stays dark
    }

    private static void WriteVersion(bool[,] g, int size, int version)
    {
        int rem = version << 12;
        for (int i = 17; i >= 12; i--)
            if (((rem >> i) & 1) != 0) rem ^= 0b1111100100101 << (i - 12);
        int bits = (version << 12) | rem;
        for (int i = 0; i < 18; i++)
        {
            bool bit = ((bits >> i) & 1) != 0;
            g[size - 11 + i % 3, i / 3] = bit;
            g[i / 3, size - 11 + i % 3] = bit;
        }
    }

    private static int Penalty(bool[,] g, int size)
    {
        int score = 0;
        // N1: runs of ≥5 same-colored modules.
        for (int r = 0; r < size; r++)
        {
            int run = 1;
            for (int c = 1; c < size; c++)
            {
                if (g[r, c] == g[r, c - 1]) { run++; if (c == size - 1 && run >= 5) score += run - 2; }
                else { if (run >= 5) score += run - 2; run = 1; }
            }
        }
        for (int c = 0; c < size; c++)
        {
            int run = 1;
            for (int r = 1; r < size; r++)
            {
                if (g[r, c] == g[r - 1, c]) { run++; if (r == size - 1 && run >= 5) score += run - 2; }
                else { if (run >= 5) score += run - 2; run = 1; }
            }
        }
        // N2: 2×2 blocks.
        for (int r = 0; r < size - 1; r++)
            for (int c = 0; c < size - 1; c++)
                if (g[r, c] == g[r, c + 1] && g[r, c] == g[r + 1, c] && g[r, c] == g[r + 1, c + 1])
                    score += 3;
        // N3: finder-like 1011101 with 4 light modules on either side.
        bool[] pat = { true, false, true, true, true, false, true };
        for (int r = 0; r < size; r++)
            for (int c = 0; c + 6 < size; c++)
            {
                bool m1 = true, m2 = true;
                for (int k = 0; k < 7; k++) { if (g[r, c + k] != pat[k]) m1 = false; if (g[c + k > size - 1 ? 0 : r, c + k] != pat[k]) { } }
                if (m1)
                {
                    bool leftLight = true, rightLight = true;
                    for (int k = 1; k <= 4; k++)
                    {
                        if (c - k < 0 || g[r, c - k]) leftLight = false;
                        if (c + 6 + k >= size || g[r, c + 6 + k]) rightLight = false;
                    }
                    if (leftLight || rightLight) score += 40;
                }
                m2 = true;
                if (r + 6 < size)
                {
                    for (int k = 0; k < 7; k++) if (g[r + k, c] != pat[k]) { m2 = false; break; }
                    if (m2)
                    {
                        bool upLight = true, downLight = true;
                        for (int k = 1; k <= 4; k++)
                        {
                            if (r - k < 0 || g[r - k, c]) upLight = false;
                            if (r + 6 + k >= size || g[r + 6 + k, c]) downLight = false;
                        }
                        if (upLight || downLight) score += 40;
                    }
                }
            }
        // N4: dark ratio deviation.
        int dark = 0;
        for (int r = 0; r < size; r++) for (int c = 0; c < size; c++) if (g[r, c]) dark++;
        int percent = dark * 100 / (size * size);
        score += Math.Abs(percent - 50) / 5 * 10;
        return score;
    }
}
