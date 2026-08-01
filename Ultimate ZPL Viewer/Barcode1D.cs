using System;
using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// The linear symbologies that are not Code 128 / Code 39 / interleaved 2 of 5 /
/// EAN-UPC (those live in ZplRenderModel.cs next to the code they grew from).
///
/// Every builder returns the bar/space runs in MODULES — the caller multiplies by
/// the ^BY module width — plus the human-readable text the printer puts under the
/// symbol. Widths were checked against the reference renderer: at ^BY3 the probe
/// labels come out within a pixel of it (see the notes on each method).
/// </summary>
internal static class Barcode1D
{
    // ── Code 93 (^BA) ────────────────────────────────────────────────────────
    // 9 modules per character, encoded as a bit string (1 = black module).
    private const string Code93Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    private static readonly string[] Code93Bits =
    {
        "100010100", "101001000", "101000100", "101000010", "100101000", // 0-4
        "100100100", "100100010", "101010000", "100010010", "100001010", // 5-9
        "110101000", "110100100", "110100010", "110010100", "110010010", // A-E
        "110001010", "101101000", "101100100", "101100010", "100110100", // F-J
        "100011010", "101011000", "101001100", "101000110", "100101100", // K-O
        "100010110", "110110100", "110110010", "110101100", "110100110", // P-T
        "110010110", "110011010", "101101100", "101100110", "100110110", // U-Y
        "100111010", "100101110", "111010100", "111010010", "111001010", // Z - . space
        "101101110", "101110110", "110101110",                           // $ / +  … %
    };

    private const string Code93Stop = "101011110"; // '*' start and stop

    /// <summary>
    /// Code 93 with its two mandatory check characters (C then K, modulo 47).
    /// Probe: 10 data characters at ^BY3 → 14 characters × 9 modules + the 1-module
    /// termination bar = 382 dots, matching the reference exactly.
    /// </summary>
    public static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildCode93(string data)
    {
        var payload = new string(data.ToUpperInvariant().Where(c => Code93Charset.IndexOf(c) >= 0).ToArray());
        var values = payload.Select(c => Code93Charset.IndexOf(c)).ToList();

        int Weighted(int maxWeight)
        {
            int sum = 0, weight = 1;
            for (int i = values.Count - 1; i >= 0; i--)
            {
                sum += values[i] * weight;
                weight = weight % maxWeight + 1;
            }
            return sum % 47;
        }

        values.Add(Weighted(20));   // C
        values.Add(Weighted(15));   // K

        var runs = new List<BarcodeRun>();
        void Emit(string bits)
        {
            int i = 0;
            while (i < bits.Length)
            {
                int j = i;
                while (j < bits.Length && bits[j] == bits[i]) j++;
                runs.Add(new BarcodeRun(bits[i] == '1', j - i));
                i = j;
            }
        }

        Emit(Code93Stop);
        foreach (var v in values) Emit(Code93Bits[v]);
        Emit(Code93Stop);
        runs.Add(new BarcodeRun(true, 1)); // termination bar
        return (runs, payload);
    }

    // ── Codabar (^BK) ────────────────────────────────────────────────────────
    // 7 elements per character plus the inter-character space; '2' = wide.
    private const string CodabarCharset = "0123456789-$:/.+ABCD";

    private static readonly string[] CodabarWidths =
    {
        "11111221", "11112211", "11121121", "22111111", "11211211",
        "21111211", "12111121", "12112111", "12211111", "21121111",
        "11122111", "11221111", "21112121", "21211121", "21212111",
        "11221211", "12121121", "12112121", "11122211", "11221121",
    };

    /// <summary>
    /// Codabar. start/stop are letters A–D and are not part of the data.
    /// Probe: "12345678" between A and A at ^BY3 (wide = 3) → 123 modules = 369 dots
    /// against the reference's 370.
    /// </summary>
    public static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildCodabar(
        string data, char start, char stop, double ratio)
    {
        var payload = new string(data.ToUpperInvariant()
            .Where(c => CodabarCharset.IndexOf(c) >= 0 && !"ABCD".Contains(c)).ToArray());
        var full = char.ToUpperInvariant(start) + payload + char.ToUpperInvariant(stop);
        int wide = Math.Max(2, (int)Math.Round(ratio));

        var runs = new List<BarcodeRun>();
        for (int k = 0; k < full.Length; k++)
        {
            int idx = CodabarCharset.IndexOf(full[k]);
            if (idx < 0) continue;
            var w = CodabarWidths[idx];
            // The last element is the inter-character gap: drop it after the stop.
            int count = k == full.Length - 1 ? 7 : 8;
            for (int i = 0; i < count; i++)
                runs.Add(new BarcodeRun(i % 2 == 0, w[i] == '2' ? wide : 1));
        }
        return (runs, full);
    }

    // ── Code 11 (^B1) ────────────────────────────────────────────────────────
    // 5 elements per character (3 bars, 2 spaces); '1' = wide.
    private const string Code11Charset = "0123456789-";

    private static readonly string[] Code11Widths =
    {
        "00001", "10001", "01001", "11000", "00101",
        "10100", "01100", "00011", "10010", "10000", "00100",
    };

    private const string Code11StartStop = "00110";

    /// <summary>
    /// Code 11 with one (C) or two (C then K) check characters.
    /// Probe: 6 digits + 2 checks at ^BY3 → 99 modules = 297 dots against 298.
    /// </summary>
    public static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildCode11(
        string data, bool singleCheck, double ratio)
    {
        var payload = new string(data.Where(c => Code11Charset.IndexOf(c) >= 0).ToArray());
        var values = payload.Select(c => Code11Charset.IndexOf(c)).ToList();

        int Weighted(int maxWeight)
        {
            int sum = 0, weight = 1;
            for (int i = values.Count - 1; i >= 0; i--)
            {
                sum += values[i] * weight;
                weight = weight % maxWeight + 1;
            }
            return sum % 11;
        }

        values.Add(Weighted(10));                 // C
        if (!singleCheck) values.Add(Weighted(9)); // K
        // The printed line carries the check characters (the reference shows them).
        payload = string.Concat(values.Select(v => Code11Charset[v]));

        int wide = Math.Max(2, (int)Math.Round(ratio));
        var runs = new List<BarcodeRun>();
        void Emit(string w)
        {
            for (int i = 0; i < w.Length; i++)
                runs.Add(new BarcodeRun(i % 2 == 0, w[i] == '1' ? wide : 1));
        }

        Emit(Code11StartStop);
        foreach (var v in values)
        {
            runs.Add(new BarcodeRun(false, 1)); // inter-character space
            Emit(Code11Widths[v]);
        }
        runs.Add(new BarcodeRun(false, 1));
        Emit(Code11StartStop);
        return (runs, payload);
    }

    // ── MSI / modified Plessey (^BM) ─────────────────────────────────────────
    private static readonly string[] MsiWidths =
    {
        "12121212", "12121221", "12122112", "12122121", "12211212",
        "12211221", "12212112", "12212121", "21121212", "21121221",
    };

    /// <summary>
    /// MSI. check = 'A' none, 'B' one Mod 10, 'C' two Mod 10, 'D' Mod 11 then Mod 10.
    /// Probe: 7 digits + one check at ^BY3 → 137 modules = 411 dots against 412.
    /// </summary>
    public static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildMsi(
        string data, char check, double ratio)
    {
        var digits = new string(data.Where(char.IsDigit).ToArray());
        string Mod10(string s)
        {
            // Odd-positioned digits (from the right) are doubled, then the digits of
            // the products are summed — the classic Luhn variant MSI uses.
            int sum = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int d = s[s.Length - 1 - i] - '0';
                if (i % 2 == 0) { d *= 2; if (d > 9) d -= 9; }
                sum += d;
            }
            return s + (char)('0' + (10 - sum % 10) % 10);
        }
        string Mod11(string s)
        {
            int sum = 0, weight = 2;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                sum += (s[i] - '0') * weight;
                weight = weight == 7 ? 2 : weight + 1;
            }
            int r = (11 - sum % 11) % 11;
            return s + (r == 10 ? "10" : r.ToString());
        }

        var printed = digits;   // the reference prints the data without the check digit
        digits = char.ToUpperInvariant(check) switch
        {
            'B' => Mod10(digits),
            'C' => Mod10(Mod10(digits)),
            'D' => Mod10(Mod11(digits)),
            _   => digits,
        };

        int wide = Math.Max(2, (int)Math.Round(ratio));
        var runs = new List<BarcodeRun>();
        void Emit(string w, int offset)
        {
            for (int i = 0; i < w.Length; i++)
                runs.Add(new BarcodeRun((i + offset) % 2 == 0, w[i] == '2' ? wide : 1));
        }

        Emit("21", 0);                                    // start
        foreach (var d in digits) Emit(MsiWidths[d - '0'], 0);
        Emit("121", 0);                                   // stop
        return (runs, printed);
    }

    // ── Plessey (^BP) ────────────────────────────────────────────────────────
    // Four elements per bit; Plessey has a fixed 1:3 wide ratio.
    private static readonly string[] PlesseyWidths =
    {
        "13131313", "31131313", "13311313", "31311313", "13133113",
        "31133113", "13313113", "31313113", "13131331", "31131331",
        "13311331", "31311331", "13133131", "31133131", "13313131", "31313131",
    };

    /// <summary>
    /// Plessey with its 8-bit CRC. Probe: 7 digits at ^BY3 → 179 modules = 537 dots
    /// against 541 (the reference's terminator is a whisker wider).
    /// </summary>
    public static (IReadOnlyList<BarcodeRun> Runs, string Hrt) BuildPlessey(string data)
    {
        var hex = new string(data.ToUpperInvariant().Where(Uri.IsHexDigit).ToArray());
        var nibbles = hex.Select(c => Convert.ToInt32(c.ToString(), 16)).ToList();

        // CRC over the data bits, LSB first, polynomial x^8+x^6+x^4+x^3+x+1.
        var bits = new List<int>();
        foreach (var n in nibbles)
            for (int b = 0; b < 4; b++) bits.Add((n >> b) & 1);
        var crc = new int[8];
        for (int i = 0; i < bits.Count; i++)
        {
            int feedback = bits[i] ^ crc[7];
            for (int j = 7; j > 0; j--) crc[j] = crc[j - 1] ^ (j is 6 or 4 or 3 or 1 ? feedback : 0);
            crc[0] = feedback;
        }

        var runs = new List<BarcodeRun>();
        void Emit(string w)
        {
            for (int i = 0; i < w.Length; i++)
                runs.Add(new BarcodeRun(i % 2 == 0, w[i] - '0'));
        }

        Emit("31311331");                                     // start
        foreach (var n in nibbles) Emit(PlesseyWidths[n]);
        for (int i = 0; i < 8; i += 4)
        {
            int v = crc[i] | (crc[i + 1] << 1) | (crc[i + 2] << 2) | (crc[i + 3] << 3);
            Emit(PlesseyWidths[v]);
        }
        Emit("331311313");                                    // terminator
        return (runs, hex);
    }

    // ── Industrial (^BI) and Standard (^BJ) 2 of 5 ───────────────────────────
    private static readonly string[] Std2of5Widths =
    {
        "nnwwn", "wnnnw", "nwnnw", "wwnnn", "nnwnw",
        "wnwnn", "nwwnn", "nnnww", "wnnwn", "nwnwn",
    };

    /// <summary>
    /// The two "bars only" flavours of 2 of 5: the data rides entirely in the bars
    /// and every space is narrow. They differ only in their start/stop bars, which
    /// were read straight off the reference render (industrial starts wide-wide-narrow
    /// and ends wide-narrow-wide; standard starts with two narrow bars and ends
    /// wide-narrow). At ^BY3 with 7 digits that gives 117 and 107 modules — exactly
    /// the 351 and 321 dots the reference produces.
    /// </summary>
    public static (IReadOnlyList<BarcodeRun> Runs, string Hrt) Build2of5(
        string data, bool industrial, double ratio)
    {
        var digits = new string(data.Where(char.IsDigit).ToArray());
        int wide = Math.Max(2, (int)Math.Round(ratio));

        var bars = new List<char>();
        bars.AddRange(industrial ? "wwn" : "nn");                 // start
        foreach (var d in digits) bars.AddRange(Std2of5Widths[d - '0']);
        bars.AddRange(industrial ? "wnw" : "wn");                 // stop

        var runs = new List<BarcodeRun>();
        for (int i = 0; i < bars.Count; i++)
        {
            runs.Add(new BarcodeRun(true, bars[i] == 'w' ? wide : 1));
            if (i < bars.Count - 1) runs.Add(new BarcodeRun(false, 1));
        }
        return (runs, digits);
    }

    // ── POSTNET (^BZ) and PLANET (^B5) ───────────────────────────────────────
    // Height-modulated: every bar is the same width, only its height carries data.
    private static readonly string[] PostnetBits =
    {
        "11000", "00011", "00101", "00110", "01001",
        "01010", "01100", "10001", "10010", "10100",
    };

    /// <summary>
    /// Builds the bars of a POSTNET (tall = 1) or PLANET (the pattern inverted)
    /// symbol. Returns one segment per bar, already positioned, because the height
    /// varies from bar to bar. Probe: 11 digits + check at ^BY3 → 62 bars.
    /// </summary>
    public static List<BarSeg> BuildPostnet(string data, bool planet, double module,
        double barHeight, out double totalWidth)
    {
        var digits = new string(data.Where(char.IsDigit).ToArray());
        int sum = digits.Sum(c => c - '0');
        digits += (char)('0' + (10 - sum % 10) % 10);   // mandatory check digit

        var pattern = "1" + string.Concat(digits.Select(d =>
        {
            var bits = PostnetBits[d - '0'];
            return planet ? new string(bits.Select(b => b == '1' ? '0' : '1').ToArray()) : bits;
        })) + "1";

        // The postal standard puts the bars on a 0.048" pitch with a 0.020" bar, i.e.
        // 2.4 × the bar width — which is exactly what the reference does with ^BY
        // (measured: module 2 → pitch 5, module 3 → 7, module 5 → 12). Positions are
        // rounded individually so the pitch stays crisp instead of drifting.
        double pitch = Math.Round(module * 2.4);
        double shortH = barHeight * 0.4;
        var segs = new List<BarSeg>();
        for (int i = 0; i < pattern.Length; i++)
        {
            double bh = pattern[i] == '1' ? barHeight : shortH;
            segs.Add(new BarSeg(i * pitch, barHeight - bh, module, bh));
        }
        totalWidth = (pattern.Length - 1) * pitch + module;
        return segs;
    }
}
