using System;
using System.Collections.Generic;

namespace Ultimate_ZPL_Viewer;

// Data Matrix ECC200 encoder (ISO/IEC 16022). Produces the module matrix for a byte
// payload: high-level ASCII+C40 encoding, symbol-size selection, Reed-Solomon error
// correction over GF(256), and the Annex-F module placement with the finder pattern
// and multi-region layout. Used by ^BX (Data Matrix) fields.
public static class DataMatrixEncoder
{
    // Square ECC200 symbols: totalSize (incl. finder), dataRegion side, regions/side,
    // data codewords, ecc codewords, RS block count.
    private readonly record struct Sym(int Size, int Region, int RegionsPerSide, int DataCW, int EccCW, int Blocks);

    private static readonly Sym[] Squares =
    {
        new(10,  8, 1,   3,   5, 1),
        new(12, 10, 1,   5,   7, 1),
        new(14, 12, 1,   8,  10, 1),
        new(16, 14, 1,  12,  12, 1),
        new(18, 16, 1,  18,  14, 1),
        new(20, 18, 1,  22,  18, 1),
        new(22, 20, 1,  30,  20, 1),
        new(24, 22, 1,  36,  24, 1),
        new(26, 24, 1,  44,  28, 1),
        new(32, 14, 2,  62,  36, 1),
        new(36, 16, 2,  86,  42, 1),
        new(40, 18, 2, 114,  48, 1),
        new(44, 20, 2, 144,  56, 1),
        new(48, 22, 2, 174,  68, 1),
        new(52, 24, 2, 204,  84, 2),
        new(64, 14, 4, 280, 112, 2),
        new(72, 16, 4, 368, 144, 4),
        new(80, 18, 4, 456, 192, 4),
        new(88, 20, 4, 576, 224, 4),
        new(96, 22, 4, 696, 272, 4),
        new(104,24, 4, 816, 336, 6),
        new(120,18, 6,1050, 408, 6),
        new(132,20, 6,1304, 496, 8),
        new(144,22, 6,1558, 620,10),
    };

    // Encodes the byte payload, returns Matrix[row, col] (true = black), or null if it
    // does not fit any symbol. forcedSize > 0 selects that exact square symbol size
    // (the ^BX columns/rows parameters).
    public static bool[,]? Encode(byte[] data, int forcedSize = 0)
    {
        var codewords = HighLevelEncode(data);
        // Pick the smallest symbol whose data capacity holds the encoded codewords.
        Sym sym = default; bool found = false;
        foreach (var s in Squares)
        {
            if (forcedSize > 0 ? s.Size == forcedSize : s.DataCW >= codewords.Count)
            {
                if (forcedSize > 0 && s.DataCW < codewords.Count) return null; // won't fit
                sym = s; found = true; break;
            }
        }
        if (!found) return null;

        // Pad: first pad = 129 (EOM), then the 253-state randomising algorithm.
        var full = new List<int>(codewords);
        if (full.Count < sym.DataCW)
        {
            full.Add(129);
            while (full.Count < sym.DataCW)
            {
                int pad = 129 + (((full.Count + 1) * 149) % 253) + 1;
                if (pad > 254) pad -= 254;
                full.Add(pad);
            }
        }

        var all = ReedSolomon(full, sym);
        return Place(all, sym);
    }

    // ── High-level encoding: ASCII base with C40 for runs of upper/digit/space ──────
    private static List<int> HighLevelEncode(byte[] data)
    {
        var cw = new List<int>();
        int i = 0, n = data.Length;
        while (i < n)
        {
            // Digit pair → single ASCII codeword (130 + value).
            if (i + 1 < n && IsDigit(data[i]) && IsDigit(data[i + 1]))
            {
                cw.Add((data[i] - '0') * 10 + (data[i + 1] - '0') + 130);
                i += 2;
                continue;
            }

            // Switch to C40 only for a long enough letter/space run. With whole-triples-
            // only C40 (latch+unlatch overhead + ASCII leftover), the break-even is 9
            // characters (3 triples); below that ASCII is as cheap or cheaper.
            int run = C40Run(data, i);
            if (run >= 9)
            {
                i = EncodeC40(data, i, cw);
                continue;
            }

            byte b = data[i];
            if (b < 128) cw.Add(b + 1);
            else { cw.Add(235); cw.Add((b - 128) + 1); } // upper-shift for extended ASCII
            i++;
        }
        return cw;
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';

    // C40 is only worth entering for letters/spaces: digits are cheaper as ASCII pairs
    // (0.5 cw/digit vs 0.67 in C40), so they are deliberately excluded from C40 runs.
    private static bool IsC40Basic(byte b) =>
        b == ' ' || (b >= 'A' && b <= 'Z');

    private static int C40Run(byte[] data, int i)
    {
        int r = 0;
        while (i + r < data.Length && IsC40Basic(data[i + r])) r++;
        return r;
    }

    // C40 value for a basic char (assumes IsC40Basic).
    private static int C40Value(byte b) =>
        b == ' ' ? 3 : b <= '9' ? b - '0' + 4 : b - 'A' + 14;

    // Encodes a C40 run starting at i. Emits the C40 latch (230), packs COMPLETE triples
    // (2 codewords each), unlatches (254), and leaves any 1-2 trailing basic chars for the
    // ASCII path. Only complete triples are encoded, which keeps the tricky end-of-data
    // padding out of play while staying fully decodable (slightly suboptimal at run ends).
    private static int EncodeC40(byte[] data, int i, List<int> cw)
    {
        int end = i;
        while (end < data.Length && IsC40Basic(data[end])) end++;
        int triples = (end - i) / 3;
        if (triples == 0) return i; // caller only enters with a run ≥ 3, but guard anyway

        cw.Add(230); // latch to C40
        int j = i;
        for (int t = 0; t < triples; t++, j += 3)
            Pack3(C40Value(data[j]), C40Value(data[j + 1]), C40Value(data[j + 2]), cw);
        cw.Add(254); // unlatch back to ASCII
        return j;    // 0-2 leftover basic chars continue in ASCII
    }

    private static void Pack3(int a, int b, int c, List<int> cw)
    {
        int v = 1600 * a + 40 * b + c + 1;
        cw.Add(v / 256);
        cw.Add(v % 256);
    }

    // ── Reed-Solomon (GF(256), primitive 0x12D) with symbol block interleaving ──────
    private static readonly int[] Log = new int[256];
    private static readonly int[] ALog = new int[256];
    static DataMatrixEncoder()
    {
        int p = 1;
        for (int i = 0; i < 255; i++)
        {
            ALog[i] = p;
            Log[p] = i;
            p <<= 1;
            if (p >= 256) p ^= 0x12D;
        }
        ALog[255] = ALog[0];
    }

    private static int[] ReedSolomon(List<int> data, Sym sym)
    {
        int totalData = sym.DataCW, totalEcc = sym.EccCW, blocks = sym.Blocks;
        int eccPerBlock = totalEcc / blocks;
        var gen = RsGenerator(eccPerBlock);

        // Split data into interleaved blocks (Data Matrix uses simple round-robin split).
        var dataBlocks = new List<int>[blocks];
        for (int b = 0; b < blocks; b++) dataBlocks[b] = new List<int>();
        for (int k = 0; k < totalData; k++) dataBlocks[k % blocks].Add(data[k]);

        var eccBlocks = new int[blocks][];
        for (int b = 0; b < blocks; b++) eccBlocks[b] = RsRemainder(dataBlocks[b], gen, eccPerBlock);

        // Output = all data codewords (original order) then interleaved ecc codewords.
        var outCw = new int[totalData + totalEcc];
        for (int k = 0; k < totalData; k++) outCw[k] = data[k];
        int idx = totalData;
        int maxEcc = eccPerBlock;
        for (int e = 0; e < maxEcc; e++)
            for (int b = 0; b < blocks; b++)
                outCw[idx++] = eccBlocks[b][e];
        return outCw;
    }

    // Generator polynomial for `ecc` check words, roots α^1..α^ecc (Data Matrix base 1).
    // Returned high-degree first: gen[0] = leading 1, gen[1..ecc] = lower coefficients.
    private static int[] RsGenerator(int ecc)
    {
        var g = new int[] { 1 };
        for (int i = 0; i < ecc; i++)
        {
            var ng = new int[g.Length + 1];
            for (int j = 0; j < g.Length; j++)
            {
                ng[j] ^= g[j];
                ng[j + 1] ^= GfMul(g[j], ALog[i + 1]);
            }
            g = ng;
        }
        return g;
    }

    private static int[] RsRemainder(List<int> data, int[] gen, int ecc)
    {
        var rem = new int[ecc];
        foreach (int d in data)
        {
            int factor = d ^ rem[0];
            for (int j = 0; j < ecc; j++)
            {
                int next = j < ecc - 1 ? rem[j + 1] : 0;
                rem[j] = next ^ GfMul(factor, gen[j + 1]); // gen[1..ecc]
            }
        }
        return rem;
    }

    private static int GfMul(int a, int b) => a == 0 || b == 0 ? 0 : ALog[(Log[a] + Log[b]) % 255];

    // ── ECC200 module placement (ISO 16022 Annex F) ─────────────────────────────────
    private static bool[,] Place(int[] codewords, Sym sym)
    {
        int regions = sym.RegionsPerSide;
        int dataRegion = sym.Region;                 // data-region side (without finder)
        int mappingSize = dataRegion * regions;      // full mapping matrix side (no finder)
        var bits = new int[mappingSize, mappingSize];
        for (int r = 0; r < mappingSize; r++) for (int c = 0; c < mappingSize; c++) bits[r, c] = -1;

        PlaceBits(bits, codewords, mappingSize);

        // Assemble the final symbol: each data region gets a finder L + timing, arranged
        // in a regions×regions grid, plus a 1-module quiet zone all around.
        int total = sym.Size;               // already includes the finder modules
        var m = new bool[total, total];
        int q = 0;                          // no interior quiet zone (Size = data+finder)
        for (int rr = 0; rr < regions; rr++)
        {
            for (int rc = 0; rc < regions; rc++)
            {
                int rowBase = q + rr * (dataRegion + 2);
                int colBase = q + rc * (dataRegion + 2);
                // Finder pattern: solid left column + solid bottom row; timing on the top
                // row (black at even columns) and right column (black at odd rows), so the
                // bottom-left / bottom-right corners stay black.
                for (int i = 0; i < dataRegion + 2; i++)
                {
                    m[rowBase + i, colBase] = true;                            // solid left
                    m[rowBase + dataRegion + 1, colBase + i] = true;           // solid bottom
                    if (i % 2 == 0) m[rowBase, colBase + i] = true;            // timing top
                    if (i % 2 == 1) m[rowBase + i, colBase + dataRegion + 1] = true; // timing right
                }
                // Data modules inside the region.
                for (int dr = 0; dr < dataRegion; dr++)
                    for (int dc = 0; dc < dataRegion; dc++)
                    {
                        int mr = rr * dataRegion + dr;
                        int mc = rc * dataRegion + dc;
                        if (bits[mr, mc] == 1)
                            m[rowBase + 1 + dr, colBase + 1 + dc] = true;
                    }
            }
        }
        return m;
    }

    private static void PlaceBits(int[,] bits, int[] cw, int size)
    {
        int chr = 0, row = 4, col = 0;
        int cwCount = cw.Length;

        void Module(int r, int c, int idx, int bit)
        {
            if (r < 0) { r += size; c += 4 - ((size + 4) % 8); }
            if (c < 0) { c += size; r += 4 - ((size + 4) % 8); }
            if (idx < cwCount)
                bits[r, c] = (cw[idx] >> (8 - bit)) & 1;
            else
                bits[r, c] = 0;
        }

        void Utah(int r, int c, int idx)
        {
            Module(r - 2, c - 2, idx, 1);
            Module(r - 2, c - 1, idx, 2);
            Module(r - 1, c - 2, idx, 3);
            Module(r - 1, c - 1, idx, 4);
            Module(r - 1, c,     idx, 5);
            Module(r,     c - 2, idx, 6);
            Module(r,     c - 1, idx, 7);
            Module(r,     c,     idx, 8);
        }

        void Corner1(int idx)
        {
            Module(size - 1, 0, idx, 1); Module(size - 1, 1, idx, 2); Module(size - 1, 2, idx, 3);
            Module(0, size - 2, idx, 4); Module(0, size - 1, idx, 5);
            Module(1, size - 1, idx, 6); Module(2, size - 1, idx, 7); Module(3, size - 1, idx, 8);
        }
        void Corner2(int idx)
        {
            Module(size - 3, 0, idx, 1); Module(size - 2, 0, idx, 2); Module(size - 1, 0, idx, 3);
            Module(0, size - 4, idx, 4); Module(0, size - 3, idx, 5); Module(0, size - 2, idx, 6);
            Module(0, size - 1, idx, 7); Module(1, size - 1, idx, 8);
        }
        void Corner3(int idx)
        {
            Module(size - 3, 0, idx, 1); Module(size - 2, 0, idx, 2); Module(size - 1, 0, idx, 3);
            Module(0, size - 2, idx, 4); Module(0, size - 1, idx, 5);
            Module(1, size - 1, idx, 6); Module(2, size - 1, idx, 7); Module(3, size - 1, idx, 8);
        }
        void Corner4(int idx)
        {
            Module(size - 1, 0, idx, 1); Module(size - 1, size - 1, idx, 2);
            Module(0, size - 3, idx, 3); Module(0, size - 2, idx, 4); Module(0, size - 1, idx, 5);
            Module(1, size - 3, idx, 6); Module(1, size - 2, idx, 7); Module(1, size - 1, idx, 8);
        }

        do
        {
            if (row == size && col == 0) { Corner1(chr++); }
            else if (row == size - 2 && col == 0 && (size % 4) != 0) { Corner2(chr++); }
            else if (row == size - 2 && col == 0 && (size % 8) == 4) { Corner3(chr++); }
            else if (row == size + 4 && col == 2 && (size % 8) == 0) { Corner4(chr++); }

            do
            {
                if (row < size && col >= 0 && bits[row, col] < 0) Utah(row, col, chr++);
                row -= 2; col += 2;
            } while (row >= 0 && col < size);
            row += 1; col += 3;

            do
            {
                if (row >= 0 && col < size && bits[row, col] < 0) Utah(row, col, chr++);
                row += 2; col -= 2;
            } while (row < size && col >= 0);
            row += 3; col += 1;
        } while (row < size || col < size);

        // Fill the bottom-right corner check pattern if still unset.
        if (bits[size - 1, size - 1] < 0)
        {
            bits[size - 1, size - 1] = 1;
            bits[size - 2, size - 2] = 1;
        }
    }
}
