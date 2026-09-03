using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// ── The symbologies the editor offers ───────────────────────────────────────
// One table, read from three places: the insertion menu, the symbology picker on
// a selected code, and the check that tells the user why a code is not drawing.
// Keeping it in one place is what stops a symbology from being offered for
// insertion but unknown to the picker.
internal sealed record BarcodeSpec(
    string Key,
    string Label,
    string Command,             // the ZPL selector, without its ^
    Func<int, int, string> Args,// (bar height in dots, 2D module size) → arguments
    string Sample,              // data the symbology actually accepts
    bool TwoD);

internal static class BarcodeCatalog
{
    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static readonly BarcodeSpec[] All =
    {
        new("code128", "Code 128", "BC", (h, _) => $"N,{N(h)},Y,N,N", "1234567890", false),
        // The >; >8 prefix is how a GS1-128 carries its application identifiers.
        new("gs1-128", "GS1-128", "BC", (h, _) => $"N,{N(h)},Y,N,Y", ">;>80112345678901231", false),
        new("code39", "Code 39", "B3", (h, _) => $"N,N,{N(h)},Y,N", "ABC123", false),
        new("code93", "Code 93", "BA", (h, _) => $"N,{N(h)},Y,N", "ABC123", false),
        new("ean13", "EAN-13", "BE", (h, _) => $"N,{N(h)},Y,N", "123456789012", false),
        new("ean8", "EAN-8", "B8", (h, _) => $"N,{N(h)},Y,N", "1234567", false),
        new("upca", "UPC-A", "BU", (h, _) => $"N,{N(h)},Y,N", "12345678901", false),
        new("itf", "ITF (2 sur 5)", "B2", (h, _) => $"N,{N(h)},Y,N,N", "12345678", false),
        new("codabar", "Codabar", "BK", (h, _) => $"N,N,{N(h)},Y,N,A,A", "12345", false),
        new("qr", "QR Code", "BQ", (_, m) => $"N,2,{N(m)}", "QA,https://example.com", true),
        new("datamatrix", "Data Matrix", "BX", (_, m) => $"N,{N(m)},200", "DATA-MATRIX", true),
        new("aztec", "Aztec", "BO", (_, m) => $"N,{N(m)}", "AZTEC", true),
        new("pdf417", "PDF417", "B7", (_, m) => $"N,{N(m)},5", "PDF417", true),
    };

    public static BarcodeSpec? ByKey(string key)
        => All.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// Which entry a ^Bx command belongs to. Code 128 and GS1-128 share ^BC and are
    /// told apart by the data: only a GS1 payload opens with the >; escape.
    /// </summary>
    public static BarcodeSpec? ByCommand(string? command, string? data)
    {
        if (string.IsNullOrEmpty(command)) return null;
        if (command == "BC")
            return ByKey(data is not null && data.TrimStart().StartsWith(">;", StringComparison.Ordinal)
                ? "gs1-128" : "code128");
        return All.FirstOrDefault(s => s.Command == command);
    }

    /// <summary>
    /// Why this symbology will refuse the content, or null when it will take it.
    /// Deliberately shallow: it catches the mistakes that make a code vanish from
    /// the preview, not every rule in the specification.
    /// </summary>
    public static string? Validate(string key, string data)
    {
        data ??= "";
        if (data.Length == 0) return Message("empty", "");

        bool Digits(string s) => s.Length > 0 && s.All(char.IsDigit);
        string Trimmed = data.Trim();

        switch (key)
        {
            case "ean13":
                // 12 digits: the printer works out the thirteenth.
                return Digits(Trimmed) && Trimmed.Length is 12 or 13 ? null : Message("digits", "12");
            case "ean8":
                return Digits(Trimmed) && Trimmed.Length is 7 or 8 ? null : Message("digits", "7");
            case "upca":
                return Digits(Trimmed) && Trimmed.Length is 11 or 12 ? null : Message("digits", "11");
            case "itf":
                if (!Digits(Trimmed)) return Message("digitsOnly", "");
                return Trimmed.Length % 2 == 0 ? null : Message("even", "");
            case "code39":
            case "code93":
            {
                const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-. $/+%";
                return Trimmed.ToUpperInvariant().All(allowed.Contains) ? null : Message("charset", "");
            }
            case "codabar":
            {
                const string allowed = "0123456789-$:/.+";
                return Trimmed.All(allowed.Contains) ? null : Message("charset", "");
            }
            default:
                return null;
        }
    }

    private static string Message(string kind, string arg)
        => LocalizationService.Get("mode.props.invalid." + kind).Replace("{n}", arg);
}
