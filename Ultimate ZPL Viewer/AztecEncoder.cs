using System;
using System.Collections.Generic;

namespace Ultimate_ZPL_Viewer;

// Aztec Code encoder (ISO/IEC 24778), ported from the ZXing algorithm. Produces
// the module matrix for a byte payload: high-level text/binary encoding, symbol
// sizing with bit-stuffing, Reed-Solomon error correction, the mode message, and
// the bull's-eye + spiral data layout. Used by ^BO / ^B0 (Aztec) fields.
public static class AztecEncoder
{
    private const int Upper = 0, Lower = 1, Digit = 2, Mixed = 3, Punct = 4;
    private static readonly int[] ModeBits = { 5, 5, 4, 5, 5 };

    // charToCode[mode][byte] = symbol value, or -1 if the byte is not in that mode.
    private static readonly int[][] CharToCode = BuildCharTables();

    // Direct latch code emitted in the FROM mode to reach the TO mode (-1 = none).
    private static readonly int[,] DirectLatch =
    {
        //         U    L    D    M    P
        /*U*/ {   -1,  28,  30,  29,  -1 },
        /*L*/ {   -1,  -1,  30,  29,  -1 },
        /*D*/ {   14,  -1,  -1,  -1,  -1 },
        /*M*/ {   29,  28,  -1,  -1,  30 },
        /*P*/ {   31,  -1,  -1,  -1,  -1 },
    };

    // Shortest latch sequences (list of (mode, code)) between every mode pair,
    // and their total bit cost — computed once from DirectLatch.
    private static readonly (List<(int Mode, int Code)> Seq, int Bits)[,] LatchPath = BuildLatchPaths();

    // Zebra/Labelary target a higher Aztec error-correction level than the ZXing
    // 23% default: for the DPD payload, 23% yields 61 modules but the real printer
    // (and Labelary) produce 67. 28% lands on the same 67-module symbol, so the
    // rendered code matches the reference size while staying more robust.
    public static bool[,] Encode(byte[] data) => Encode(data, 28);

    public static bool[,] Encode(byte[] data, int minEccPercent)
    {
        var bits = HighLevelEncode(data);

        int eccBits = bits.Count * minEccPercent / 100 + 11;
        int bitCount = bits.Count;

        bool compact = false;
        int layers = 0, wordSize = 0;
        List<bool> stuffed = null!;
        for (int i = 0; ; i++)
        {
            if (i > 32) throw new InvalidOperationException("Données trop volumineuses pour un code Aztec.");
            compact = i <= 3;
            layers = compact ? i + 1 : i;
            if (layers == 0) continue;
            int totalBitsInLayer = TotalBitsInLayer(layers, compact);
            if (bitCount > totalBitsInLayer) continue;
            if (wordSize != WordSize(layers))
            {
                wordSize = WordSize(layers);
                stuffed = StuffBits(bits, wordSize);
            }
            int usableBits = totalBitsInLayer - totalBitsInLayer % wordSize;
            if (compact && stuffed.Count > wordSize * 64) continue;
            if (stuffed.Count + eccBits <= usableBits) break;
        }

        // Pass the FULL layer bit count so the leading pad covers the remainder and
        // messageBits spans every module position in the spiral.
        int totalBitsInLayer2 = TotalBitsInLayer(layers, compact);
        int messageSizeInWords = stuffed.Count / wordSize;
        var messageBits = GenerateCheckWords(stuffed, totalBitsInLayer2, wordSize);
        var modeMessage = GenerateModeMessage(compact, layers, messageSizeInWords);

        // ── Lay out the matrix ────────────────────────────────────────────────
        int baseMatrixSize = (compact ? 11 : 14) + layers * 4;
        var alignmentMap = new int[baseMatrixSize];
        int matrixSize;
        if (compact)
        {
            matrixSize = baseMatrixSize;
            for (int i = 0; i < alignmentMap.Length; i++) alignmentMap[i] = i;
        }
        else
        {
            matrixSize = baseMatrixSize + 1 + 2 * ((baseMatrixSize / 2 - 1) / 15);
            int origCenter = baseMatrixSize / 2;
            int center2 = matrixSize / 2;
            for (int i = 0; i < origCenter; i++)
            {
                int newOffset = i + i / 15;
                alignmentMap[origCenter - i - 1] = center2 - newOffset - 1;
                alignmentMap[origCenter + i] = center2 + newOffset + 1;
            }
        }

        var matrix = new bool[matrixSize, matrixSize];
        void Set(int x, int y) { matrix[y, x] = true; }

        // Data spiral: 2-module dominoes around the core, four sides per layer.
        int rowOffset = 0;
        for (int i = 0; i < layers; i++)
        {
            int rowSize = (layers - i) * 4 + (compact ? 9 : 12);
            for (int j = 0; j < rowSize; j++)
            {
                int columnOffset = j * 2;
                for (int k = 0; k < 2; k++)
                {
                    if (messageBits[rowOffset + columnOffset + k]) Set(alignmentMap[i * 2 + k], alignmentMap[i * 2 + j]);
                    if (messageBits[rowOffset + rowSize * 2 + columnOffset + k]) Set(alignmentMap[i * 2 + j], alignmentMap[baseMatrixSize - 1 - i * 2 - k]);
                    if (messageBits[rowOffset + rowSize * 4 + columnOffset + k]) Set(alignmentMap[baseMatrixSize - 1 - i * 2 - k], alignmentMap[baseMatrixSize - 1 - i * 2 - j]);
                    if (messageBits[rowOffset + rowSize * 6 + columnOffset + k]) Set(alignmentMap[baseMatrixSize - 1 - i * 2 - j], alignmentMap[i * 2 + k]);
                }
            }
            rowOffset += rowSize * 8;
        }

        DrawModeMessage(matrix, compact, matrixSize, modeMessage);

        // Bull's-eye finder + orientation marks.
        int center = matrixSize / 2;
        int ringMax = compact ? 5 : 7;
        for (int i = 0; i < ringMax; i += 2)
            for (int j = center - i; j <= center + i; j++)
            {
                Set(j, center - i);
                Set(j, center + i);
                Set(center - i, j);
                Set(center + i, j);
            }
        Set(center - ringMax, center - ringMax);
        Set(center - ringMax + 1, center - ringMax);
        Set(center - ringMax, center - ringMax + 1);
        Set(center + ringMax, center - ringMax);
        Set(center + ringMax, center - ringMax + 1);
        Set(center + ringMax, center + ringMax - 1);

        // Reference grid (full symbols only): fixed alternating lines every 16 modules.
        if (!compact)
        {
            for (int i = 0; i < matrixSize; i++)
                if (i % 16 == center % 16)
                    for (int j = (i % 2); j < matrixSize; j += 2) { matrix[i, j] = true; matrix[j, i] = true; }
        }

        return matrix;
    }

    private static void DrawModeMessage(bool[,] matrix, bool compact, int matrixSize, List<bool> modeMessage)
    {
        int center = matrixSize / 2;
        void Set(int x, int y) { matrix[y, x] = true; }
        if (compact)
        {
            for (int i = 0; i < 7; i++)
            {
                if (modeMessage[i]) Set(center - 3 + i, center - 5);
                if (modeMessage[i + 7]) Set(center + 5, center - 3 + i);
                if (modeMessage[20 - i]) Set(center - 3 + i, center + 5);
                if (modeMessage[27 - i]) Set(center - 5, center - 3 + i);
            }
        }
        else
        {
            for (int i = 0; i < 10; i++)
            {
                int m = i < 5 ? i : i + 1; // skip the reference-grid module in the middle
                if (modeMessage[i]) Set(center - 5 + m, center - 7);
                if (modeMessage[i + 10]) Set(center + 7, center - 5 + m);
                if (modeMessage[29 - i]) Set(center - 5 + m, center + 7);
                if (modeMessage[39 - i]) Set(center - 7, center - 5 + m);
            }
        }
    }

    // ── High-level (text) encoding — greedy, valid; digits/letters latch, one-off
    //    punctuation/upper use a shift, ≥128 bytes use binary shift. ─────────────
    private static List<bool> HighLevelEncode(byte[] data)
    {
        var bits = new List<bool>();
        void Emit(int value, int count) { for (int k = count - 1; k >= 0; k--) bits.Add(((value >> k) & 1) != 0); }

        int mode = Upper;
        int i = 0;
        while (i < data.Length)
        {
            int b = data[i] & 0xFF;
            if (b >= 128)
            {
                // Binary shift: B/S code in the current mode, a length, then raw bytes.
                int start = i;
                while (i < data.Length && (data[i] & 0xFF) >= 128) i++;
                int len = i - start;
                Emit(mode == Digit ? 15 : 31, ModeBits[mode]); // B/S (Digit has no B/S → shift to Upper first)
                if (mode == Digit) { mode = Upper; Emit(31, ModeBits[Upper]); }
                if (len <= 31) Emit(len, 5);
                else { Emit(0, 5); Emit(len - 31, 11); }
                for (int j = start; j < i; j++) Emit(data[j] & 0xFF, 8);
                continue;
            }

            if (CharToCode[mode][b] >= 0)
            {
                Emit(CharToCode[mode][b], ModeBits[mode]);
                i++;
                continue;
            }

            int target = ChooseMode(data, i, mode, out int runLen);
            // A single character reachable by a shift (to Punct, or to Upper from
            // Lower/Digit) is cheaper to shift than to latch-and-latch-back.
            if (runLen == 1 && TryShift(mode, target, out int shiftCode))
            {
                Emit(shiftCode, ModeBits[mode]);
                Emit(CharToCode[target][b], ModeBits[target]);
                i++;
                continue;
            }
            foreach (var (m, code) in LatchPath[mode, target].Seq) Emit(code, ModeBits[m]);
            mode = target;
        }
        return bits;
    }

    private static bool TryShift(int from, int to, out int code)
    {
        code = 0;
        if (to == Punct) { code = 0; return true; }                 // P/S exists in every mode
        if (to == Upper && (from == Lower || from == Digit)) { code = from == Digit ? 15 : 28; return true; }
        return false;
    }

    private static int ChooseMode(byte[] data, int i, int curMode, out int runLen)
    {
        int b = data[i] & 0xFF;
        int best = -1, bestRun = -1;
        foreach (int m in new[] { Digit, Upper, Lower, Mixed, Punct })
        {
            if (CharToCode[m][b] < 0) continue;
            int run = 0;
            while (i + run < data.Length && (data[i + run] & 0xFF) < 128 && CharToCode[m][data[i + run] & 0xFF] >= 0) run++;
            if (run > bestRun) { bestRun = run; best = m; }
        }
        runLen = Math.Max(1, bestRun);
        return best < 0 ? Upper : best;
    }

    // ── Reed-Solomon + bit stuffing ──────────────────────────────────────────
    private static List<bool> StuffBits(List<bool> bits, int wordSize)
    {
        var outBits = new List<bool>();
        int n = bits.Count;
        int mask = (1 << wordSize) - 2;
        for (int i = 0; i < n; i += wordSize)
        {
            int word = 0;
            for (int j = 0; j < wordSize; j++)
                if (i + j >= n || bits[i + j]) word |= 1 << (wordSize - 1 - j);
            if ((word & mask) == mask) { Append(outBits, word & mask, wordSize); i--; }
            else if ((word & mask) == 0) { Append(outBits, word | 1, wordSize); i--; }
            else Append(outBits, word, wordSize);
        }
        return outBits;
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (int k = count - 1; k >= 0; k--) bits.Add(((value >> k) & 1) != 0);
    }

    private static List<bool> GenerateCheckWords(List<bool> bitArray, int totalBits, int wordSize)
    {
        int messageSizeInWords = bitArray.Count / wordSize;
        int totalWords = totalBits / wordSize;
        var messageWords = new int[totalWords];
        for (int i = 0; i < messageSizeInWords; i++)
        {
            int value = 0;
            for (int j = 0; j < wordSize; j++)
                if (bitArray[i * wordSize + j]) value |= 1 << (wordSize - j - 1);
            messageWords[i] = value;
        }

        var gf = GetGf(wordSize);
        int ec = totalWords - messageSizeInWords;
        var check = RsEncode(messageWords, messageSizeInWords, ec, gf);
        Array.Copy(check, 0, messageWords, messageSizeInWords, ec);

        var result = new List<bool>();
        Append(result, 0, totalBits % wordSize); // leading pad
        foreach (var w in messageWords) Append(result, w, wordSize);
        return result;
    }

    private static List<bool> GenerateModeMessage(bool compact, int layers, int messageSizeInWords)
    {
        var modeMessage = new List<bool>();
        if (compact)
        {
            Append(modeMessage, layers - 1, 2);
            Append(modeMessage, messageSizeInWords - 1, 6);
            return GenerateCheckWords(modeMessage, 28, 4);
        }
        Append(modeMessage, layers - 1, 5);
        Append(modeMessage, messageSizeInWords - 1, 11);
        return GenerateCheckWords(modeMessage, 40, 4);
    }

    private static int TotalBitsInLayer(int layers, bool compact) => ((compact ? 88 : 112) + 16 * layers) * layers;

    private static int WordSize(int layers) =>
        layers <= 2 ? 6 : layers <= 8 ? 8 : layers <= 22 ? 10 : 12;

    // ── Galois fields for the code word sizes ────────────────────────────────
    private sealed class Gf
    {
        public readonly int Size;
        private readonly int[] _exp;
        private readonly int[] _log;
        public Gf(int primitive, int size)
        {
            Size = size;
            _exp = new int[size];
            _log = new int[size];
            int x = 1;
            for (int i = 0; i < size; i++) { _exp[i] = x; x <<= 1; if (x >= size) x = (x ^ primitive) & (size - 1); }
            for (int i = 0; i < size - 1; i++) _log[_exp[i]] = i;
        }
        public int Mul(int a, int b) => a == 0 || b == 0 ? 0 : _exp[(_log[a] + _log[b]) % (Size - 1)];
        public int Exp(int i) => _exp[i % (Size - 1)];
    }

    private static readonly Dictionary<int, Gf> Fields = new();
    private static Gf GetGf(int wordSize)
    {
        if (Fields.TryGetValue(wordSize, out var gf)) return gf;
        gf = wordSize switch
        {
            4 => new Gf(0b10011, 16),
            6 => new Gf(0b1000011, 64),
            8 => new Gf(0b100101101, 256),
            10 => new Gf(0b10000001001, 1024),
            _ => new Gf(0b1000001101001, 4096),
        };
        Fields[wordSize] = gf;
        return gf;
    }

    private static int[] RsEncode(int[] data, int dataLen, int ec, Gf gf)
    {
        // Generator polynomial (roots a^1 .. a^ec), highest degree first.
        var gen = new int[] { 1 };
        for (int i = 0; i < ec; i++)
        {
            int ak = gf.Exp(i + 1);
            var ng = new int[gen.Length + 1];
            ng[0] = gen[0];
            for (int j = 1; j < gen.Length; j++) ng[j] = gen[j] ^ gf.Mul(gen[j - 1], ak);
            ng[gen.Length] = gf.Mul(gen[gen.Length - 1], ak);
            gen = ng;
        }

        var rem = new int[dataLen + ec];
        Array.Copy(data, rem, dataLen);
        for (int i = 0; i < dataLen; i++)
        {
            int coef = rem[i];
            if (coef == 0) continue;
            for (int j = 0; j < gen.Length; j++) rem[i + j] ^= gf.Mul(gen[j], coef);
        }
        var result = new int[ec];
        Array.Copy(rem, dataLen, result, 0, ec);
        return result;
    }

    // ── Static tables ────────────────────────────────────────────────────────
    private static int[][] BuildCharTables()
    {
        var t = new int[5][];
        for (int m = 0; m < 5; m++) { t[m] = new int[256]; Array.Fill(t[m], -1); }

        t[Upper][' '] = 1;
        for (int c = 'A'; c <= 'Z'; c++) t[Upper][c] = 2 + (c - 'A');

        t[Lower][' '] = 1;
        for (int c = 'a'; c <= 'z'; c++) t[Lower][c] = 2 + (c - 'a');

        t[Digit][' '] = 1;
        for (int c = '0'; c <= '9'; c++) t[Digit][c] = 2 + (c - '0');
        t[Digit][','] = 12; t[Digit]['.'] = 13;

        t[Mixed][' '] = 1;
        for (int c = 1; c <= 13; c++) t[Mixed][c] = 1 + c;      // ^A..^M → 2..14
        for (int c = 27; c <= 31; c++) t[Mixed][c] = 15 + (c - 27); // ESC..US → 15..19
        t[Mixed]['@'] = 20; t[Mixed]['\\'] = 21; t[Mixed]['^'] = 22; t[Mixed]['_'] = 23;
        t[Mixed]['`'] = 24; t[Mixed]['|'] = 25; t[Mixed]['~'] = 26; t[Mixed][127] = 27;

        t[Punct][13] = 1; // CR (single-char punct entry)
        t[Punct]['!'] = 6; t[Punct]['"'] = 7; t[Punct]['#'] = 8; t[Punct]['$'] = 9; t[Punct]['%'] = 10;
        t[Punct]['&'] = 11; t[Punct]['\''] = 12; t[Punct]['('] = 13; t[Punct][')'] = 14; t[Punct]['*'] = 15;
        t[Punct]['+'] = 16; t[Punct][','] = 17; t[Punct]['-'] = 18; t[Punct]['.'] = 19; t[Punct]['/'] = 20;
        t[Punct][':'] = 21; t[Punct][';'] = 22; t[Punct]['<'] = 23; t[Punct]['='] = 24; t[Punct]['>'] = 25;
        t[Punct]['?'] = 26; t[Punct]['['] = 27; t[Punct][']'] = 28; t[Punct]['{'] = 29; t[Punct]['}'] = 30;

        return t;
    }

    private static (List<(int, int)>, int)[,] BuildLatchPaths()
    {
        // Floyd-Warshall over the 5 modes using DirectLatch edges (cost = FROM mode bits).
        int INF = 1 << 20;
        var cost = new int[5, 5];
        var next = new int[5, 5];
        for (int a = 0; a < 5; a++)
            for (int b = 0; b < 5; b++)
            {
                cost[a, b] = a == b ? 0 : INF;
                next[a, b] = -1;
                if (a != b && DirectLatch[a, b] >= 0) { cost[a, b] = ModeBits[a]; next[a, b] = b; }
            }
        for (int k = 0; k < 5; k++)
            for (int a = 0; a < 5; a++)
                for (int b = 0; b < 5; b++)
                    if (cost[a, k] + cost[k, b] < cost[a, b]) { cost[a, b] = cost[a, k] + cost[k, b]; next[a, b] = next[a, k]; }

        var paths = new (List<(int, int)>, int)[5, 5];
        for (int a = 0; a < 5; a++)
            for (int b = 0; b < 5; b++)
            {
                var seq = new List<(int, int)>();
                int cur = a;
                while (cur != b && next[cur, b] >= 0)
                {
                    int nx = next[cur, b];
                    seq.Add((cur, DirectLatch[cur, nx]));
                    cur = nx;
                }
                paths[a, b] = (seq, cost[a, b]);
            }
        return paths;
    }
}
