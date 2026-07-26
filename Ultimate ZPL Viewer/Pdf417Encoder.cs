using System;
using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// PDF417 encoder (ISO/IEC 15438) — text/numeric/byte compaction, Reed-Solomon
// error correction mod 929, row indicators and the 3-cluster bar patterns.
// Returns the module matrix (rows × modules, true = black). Used by ^B7 fields.
public static class Pdf417Encoder
{
    private const int StartPattern = 0x1FEA8; // 17 modules
    private const int StopPattern = 0x3FA29;  // 18 modules
    private const int Padding = 900;

    // Text compaction sub-modes.
    private const int Upper = 0, Lower = 1, Mixed = 2, Punct = 3;

    // char → (submode → value); built once.
    private static readonly Dictionary<int, Dictionary<int, int>> CharLookup = BuildCharLookup();

    private static Dictionary<int, Dictionary<int, int>> BuildCharLookup()
    {
        var t = new Dictionary<int, Dictionary<int, int>>();
        void Add(int ch, int submode, int value)
        {
            if (!t.TryGetValue(ch, out var d)) t[ch] = d = new Dictionary<int, int>();
            d[submode] = value;
        }
        for (int c = 'A'; c <= 'Z'; c++) Add(c, Upper, c - 'A');
        for (int c = 'a'; c <= 'z'; c++) Add(c, Lower, c - 'a');
        for (int c = '0'; c <= '9'; c++) Add(c, Mixed, c - '0');
        Add(' ', Upper, 26); Add(' ', Lower, 26); Add(' ', Mixed, 26);
        var mixed = new (char C, int V)[] { ('&',10),('\r',11),('\t',12),(',',13),(':',14),('#',15),('-',16),('.',17),('$',18),('/',19),('+',20),('%',21),('*',22),('=',23),('^',24) };
        foreach (var (c, v) in mixed) Add(c, Mixed, v);
        var punct = new (char C, int V)[] { (';',0),('<',1),('>',2),('@',3),('[',4),('\\',5),(']',6),('_',7),('`',8),('~',9),('!',10),('\r',11),('\t',12),(',',13),(':',14),('\n',15),('-',16),('.',17),('$',18),('/',19),('\"',20),('|',21),('*',22),('(',23),(')',24),('?',25),('{',26),('}',27),('\'',28) };
        foreach (var (c, v) in punct) Add(c, Punct, v);
        return t;
    }

    // SWITCH_CODES[from][to] = interim codes emitted to switch text sub-mode.
    private static readonly int[][][] SwitchCodes =
    {
        /*from UPPER*/ new[] { Array.Empty<int>(), new[]{27}, new[]{28}, new[]{28,25} },
        /*from LOWER*/ new[] { new[]{28,28}, Array.Empty<int>(), new[]{28}, new[]{28,25} },
        /*from MIXED*/ new[] { new[]{28}, new[]{27}, Array.Empty<int>(), new[]{25} },
        /*from PUNCT*/ new[] { new[]{29}, new[]{29,27}, new[]{29,28}, Array.Empty<int>() },
    };

    public static bool[,]? Encode(string data, int columns, int rows, int security)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);

        // Column count: explicit, or derived from a target row count, or default 6.
        var probeWords = Compact(bytes).ToList();
        int cols = columns is >= 1 and <= 30 ? columns : 0;
        int sec = security is >= 0 and <= 8 ? security
            : probeWords.Count <= 40 ? 2 : probeWords.Count <= 160 ? 3 : 4;
        int ecCount = 1 << (sec + 1);
        if (cols == 0)
        {
            int total = probeWords.Count + ecCount + 1;
            cols = rows is >= 3 and <= 90 ? Math.Clamp((total + rows - 1) / rows, 1, 30) : 6;
        }

        var codewords = EncodeHigh(bytes, cols, sec);
        if (codewords is null) return null;

        int numRows = (codewords.Count + cols - 1) / cols;
        if (numRows < 3 || numRows > 90) return null;

        // Bit layout: start(17) + left(17) + cols×17 + right(17) + stop(18).
        int rowBits = 17 * (cols + 3) + 18;
        var matrix = new bool[numRows, rowBits];
        var codes = Pdf417Tables.Codes;

        for (int r = 0; r < numRows; r++)
        {
            int cluster = r % 3;
            int leftVal = 30 * (r / 3) + cluster switch
            {
                0 => (numRows - 1) / 3,
                1 => sec * 3 + (numRows - 1) % 3,
                _ => cols - 1,
            };
            int rightVal = 30 * (r / 3) + cluster switch
            {
                0 => cols - 1,
                1 => (numRows - 1) / 3,
                _ => sec * 3 + (numRows - 1) % 3,
            };

            int bit = 0;
            void Put(int pattern, int len)
            {
                for (int k = len - 1; k >= 0; k--)
                    matrix[r, bit++] = ((pattern >> k) & 1) != 0;
            }
            Put(StartPattern, 17);
            Put(codes[cluster][leftVal], 17);
            for (int c = 0; c < cols; c++)
            {
                int idx = r * cols + c;
                Put(codes[cluster][idx < codewords.Count ? codewords[idx] : Padding], 17);
            }
            Put(codes[cluster][rightVal], 17);
            Put(StopPattern, 18);
        }
        return matrix;
    }

    // Length descriptor + compacted data + padding + error correction words.
    private static List<int>? EncodeHigh(byte[] data, int cols, int sec)
    {
        var dataWords = Compact(data).ToList();
        int ecCount = 1 << (sec + 1);
        int total = dataWords.Count + ecCount + 1;
        int mod = total % cols;
        int padCount = mod > 0 ? cols - mod : 0;
        int lengthDescriptor = dataWords.Count + padCount + 1;
        if (lengthDescriptor > 928) return null;

        var extended = new List<int> { lengthDescriptor };
        extended.AddRange(dataWords);
        for (int i = 0; i < padCount; i++) extended.Add(Padding);

        var ec = ErrorCorrection(extended, sec);
        extended.AddRange(ec);
        return extended;
    }

    private static List<int> ErrorCorrection(List<int> words, int level)
    {
        var factors = Pdf417Tables.EcFactors[level];
        int count = 1 << (level + 1);
        var ec = new int[count];
        foreach (var w in words)
        {
            int temp = (w + ec[count - 1]) % 929;
            for (int x = count - 1; x >= 0; x--)
            {
                int prev = x > 0 ? ec[x - 1] : 0;
                ec[x] = (prev + 929 - temp * factors[x] % 929) % 929;
            }
        }
        return ec.Select(v => v > 0 ? 929 - v : 0).Reverse().ToList();
    }

    // ── Compaction: split into text/numeric/byte chunks, then encode each ──────
    private enum Fn { Text, Numeric, Byte }

    private static IEnumerable<int> Compact(byte[] data)
    {
        // Split into chunks by optimal compaction function.
        var chunks = new List<(Fn Fn, List<byte> Data)>();
        foreach (var b in data)
        {
            Fn fn = b is >= 48 and <= 57 ? Fn.Numeric
                : CharLookup.ContainsKey(b) ? Fn.Text
                : Fn.Byte;
            if (chunks.Count > 0 && chunks[^1].Fn == fn) chunks[^1].Data.Add(b);
            else chunks.Add((fn, new List<byte> { b }));
        }

        // Short numeric runs bordered by text are cheaper in text mode.
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Fn != Fn.Numeric || chunks[i].Data.Count >= 13) continue;
            bool bordersText = (i > 0 && chunks[i - 1].Fn == Fn.Text)
                            || (i + 1 < chunks.Count && chunks[i + 1].Fn == Fn.Text);
            if (bordersText) chunks[i] = (Fn.Text, chunks[i].Data);
        }
        // Merge adjacent chunks with the same function.
        for (int i = chunks.Count - 1; i > 0; i--)
            if (chunks[i].Fn == chunks[i - 1].Fn)
            {
                chunks[i - 1].Data.AddRange(chunks[i].Data);
                chunks.RemoveAt(i);
            }

        var output = new List<int>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var (fn, chunk) = chunks[i];
            if (i > 0 || fn != Fn.Text)
                output.Add(fn switch
                {
                    Fn.Text => 900,
                    Fn.Numeric => 902,
                    _ => chunk.Count % 6 == 0 ? 924 : 901,
                });
            switch (fn)
            {
                case Fn.Text: output.AddRange(CompactText(chunk)); break;
                case Fn.Numeric: output.AddRange(CompactNumeric(chunk)); break;
                default: output.AddRange(CompactBytes(chunk)); break;
            }
        }
        return output;
    }

    private static IEnumerable<int> CompactText(List<byte> chars)
    {
        var interim = new List<int>();
        int submode = Upper;
        foreach (var ch in chars)
        {
            var modes = CharLookup[ch];
            if (!modes.ContainsKey(submode))
            {
                // Preferred target submode: LOWER, UPPER, MIXED, PUNCT.
                int target = modes.ContainsKey(Lower) ? Lower
                    : modes.ContainsKey(Upper) ? Upper
                    : modes.ContainsKey(Mixed) ? Mixed : Punct;
                interim.AddRange(SwitchCodes[submode][target]);
                submode = target;
            }
            interim.Add(modes[submode]);
        }
        for (int i = 0; i < interim.Count; i += 2)
        {
            int hi = interim[i];
            int lo = i + 1 < interim.Count ? interim[i + 1] : 29; // 29 = neutral pad
            yield return 30 * hi + lo;
        }
    }

    private static IEnumerable<int> CompactNumeric(List<byte> digits)
    {
        for (int i = 0; i < digits.Count; i += 44)
        {
            var group = digits.Skip(i).Take(44).ToList();
            // value = int("1" + digits) in base 900.
            var big = System.Numerics.BigInteger.Parse("1" + new string(group.Select(b => (char)b).ToArray()));
            var stack = new Stack<int>();
            while (big > 0) { stack.Push((int)(big % 900)); big /= 900; }
            foreach (var v in stack) yield return v;
        }
    }

    private static IEnumerable<int> CompactBytes(List<byte> bytes)
    {
        for (int i = 0; i < bytes.Count; i += 6)
        {
            var group = bytes.Skip(i).Take(6).ToList();
            if (group.Count == 6)
            {
                // Base 256 → base 900: 6 bytes become exactly 5 codewords.
                System.Numerics.BigInteger big = 0;
                foreach (var b in group) big = big * 256 + b;
                var five = new int[5];
                for (int k = 4; k >= 0; k--) { five[k] = (int)(big % 900); big /= 900; }
                foreach (var v in five) yield return v;
            }
            else
            {
                foreach (var b in group) yield return b;
            }
        }
    }
}
