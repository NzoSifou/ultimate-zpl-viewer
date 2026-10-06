using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ultimate_ZPL_Viewer;

public sealed class ZplDiagnostic
{
    public int Start { get; init; }        // character offset in the document
    public int End { get; init; }
    public int Line { get; init; }         // 1-based line for the error list
    public string Message { get; init; } = "";
    public int Severity { get; init; }     // Monaco MarkerSeverity: 8 = Error, 4 = Warning, 2 = LowWarning (Info)

    public string Display => $"{Icon}  {string.Format(ZplStaticAnalyzer.M("line"), Line)} — {Message}";

    private string Icon => Severity switch
    {
        ZplStaticAnalyzer.Error => "⛔",
        ZplStaticAnalyzer.Warning => "⚠️",
        _ => "ℹ️", // low warning
    };
}

// Static analyzer for ZPL code. Three tiers:
//  Errors (8) — a document that is not a valid label at all:
//  - missing ^XA (start of format) or missing ^XZ (end of format), anywhere.
//  Warnings (4) — things that really hurt the output/print:
//  - unknown commands
//  - a required parameter absent/empty; a number parameter given a non-number
//  - elements (^FO / ^FT) placed outside ^PW / ^LL, or at negative coordinates
//    (drawn off-canvas / not visible)
//  - several ^XA or ^XZ; commands placed before ^XA or after ^XZ
//  Low warnings (2, toggled off via the settings) — clean-code hints:
//  - unrecognized content between commands (printers ignore it)
//  - extra parameters beyond what the command accepts
public static class ZplStaticAnalyzer
{
    public const int Error = 8;
    public const int Warning = 4;
    public const int LowWarning = 2;

    public static List<ZplDiagnostic> Analyze(string text, bool includeLowWarnings = true)
    {
        var diags = new List<ZplDiagnostic>();
        // An empty document is not a label either, and used to be the one case that
        // reported nothing at all — while a file of plain prose got its two errors.
        if (string.IsNullOrWhiteSpace(text))
        {
            Add(diags, new List<int> { 0 }, 0, 0, M("missingXA"), Error);
            Add(diags, new List<int> { 0 }, 0, 0, M("missingXZ"), Error);
            return diags;
        }

        var lookup     = ZplHighlighter.Lookup;
        var textParams = ZplHighlighter.TextParams;
        var lineStarts = BuildLineStarts(text);

        void AddLow(int s, int e, string m)
        { if (includeLowWarnings) Add(diags, lineStarts, s, e, m, LowWarning); }

        // Every command token in order, for the ^XA/^XZ structure checks below.
        var commands = new List<(int Start, int TokenEnd, string Cmd)>();
        int firstXaStart = -1, lastXzStart = -1, xaCount = 0, xzCount = 0;
        double? pwValue = null, llValue = null;
        var fieldOrigins = new List<(int Start, int End, string Cmd, double X, double Y)>();

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\r' or '\n' or ' ' or '\t') { i++; continue; }

            if (c is '^' or '~')
            {
                // Longest-match command lookup: 3 chars (^A@, ^B0…) before 2 chars (^A…).
                ZplCommandDef? def = null;
                int cmdLen = 0;
                if (i + 3 <= text.Length && lookup.TryGetValue(text.Substring(i, 3), out var d3)) { def = d3; cmdLen = 3; }
                else if (i + 2 <= text.Length && lookup.TryGetValue(text.Substring(i, 2), out var d2)) { def = d2; cmdLen = 2; }

                if (def is null)
                {
                    // Unknown command: report the token, skip its arguments to avoid cascading.
                    int tokEnd = i + 1;
                    while (tokEnd < text.Length && tokEnd - i <= 2 && IsCommandChar(text[tokEnd])) tokEnd++;
                    Add(diags, lineStarts, i, tokEnd, M("unknown", text[i..tokEnd]), Warning);
                    i = SkipArgs(text, tokEnd);
                    continue;
                }

                var cmd = text.Substring(i, cmdLen);
                int argsStart = i + cmdLen;
                int argsEnd   = SkipArgs(text, argsStart);
                commands.Add((i, argsStart, cmd));

                if (cmd == "^XA") { xaCount++; if (firstXaStart < 0) firstXaStart = i; }
                if (cmd == "^XZ") { xzCount++; lastXzStart = i; }

                // Collect document size and field origins for the bounds check below.
                if (cmd is "^PW" or "^LL")
                {
                    var v = FirstNumber(text, argsStart, argsEnd);
                    if (v > 0) { if (cmd == "^PW") pwValue = v; else llValue = v; }
                }
                else if (cmd is "^FO" or "^FT")
                {
                    var nums = FirstTwoNumbers(text, argsStart, argsEnd);
                    if (nums is not null)
                        fieldOrigins.Add((i, argsEnd, cmd, nums.Value.X, nums.Value.Y));
                }

                AnalyzeArgs(diags, lineStarts, text, cmd, def, argsStart, argsEnd, textParams, includeLowWarnings);
                i = argsEnd;
                continue;
            }

            // Unrecognized content between commands: printers ignore it.
            int strayEnd = i;
            while (strayEnd < text.Length && text[strayEnd] is not ('^' or '~' or '\r' or '\n')) strayEnd++;
            AddLow(i, strayEnd, M("stray"));
            i = strayEnd;
        }

        // ── ^XA / ^XZ structure ──────────────────────────────────────────────
        // The ONLY errors: a missing frame delimiter (wherever it should be).
        if (xaCount == 0)
            Add(diags, lineStarts, 0, 1, M("missingXA"), Error);
        if (xzCount == 0)
            Add(diags, lineStarts, Math.Max(0, text.Length - 1), text.Length,
                M("missingXZ"), Error);

        // A document may hold several labels, one ^XA…^XZ format after another —
        // the preview pages through them. What is wrong is a format left open or
        // closed twice, and a command standing outside every format.
        if (xaCount > 0 && xzCount > 0)
        {
            bool open = false;
            foreach (var cmd in commands)
            {
                if (cmd.Cmd == "^XA")
                {
                    if (open)
                        Add(diags, lineStarts, cmd.Start, cmd.TokenEnd,
                            M("xaUnclosed"), Warning);
                    open = true;
                }
                else if (cmd.Cmd == "^XZ")
                {
                    if (!open)
                        Add(diags, lineStarts, cmd.Start, cmd.TokenEnd,
                            M("xzWithoutXa"), Warning);
                    open = false;
                }
                else if (!open)
                {
                    string message = cmd.Start < firstXaStart
                        ? M("beforeXa", cmd.Cmd)
                        : cmd.Start > lastXzStart
                            ? M("afterXz", cmd.Cmd)
                            : M("betweenLabels", cmd.Cmd);
                    Add(diags, lineStarts, cmd.Start, cmd.TokenEnd, message, Warning);
                }
            }
        }

        // Elements drawn off-canvas: outside the declared size, or at negative
        // coordinates — invisible / troublesome at print time.
        foreach (var origin in fieldOrigins)
        {
            bool outX = pwValue.HasValue && origin.X >= pwValue.Value;
            bool outY = llValue.HasValue && origin.Y >= llValue.Value;
            bool negX = origin.X < 0, negY = origin.Y < 0;
            if (!outX && !outY && !negX && !negY) continue;

            var parts = new List<string>();
            if (negX) parts.Add(M("xNegative", N(origin.X)));
            else if (outX) parts.Add(M("xBeyond", N(origin.X), N(pwValue!.Value)));
            if (negY) parts.Add(M("yNegative", N(origin.Y)));
            else if (outY) parts.Add(M("yBeyond", N(origin.Y), N(llValue!.Value)));
            Add(diags, lineStarts, origin.Start, origin.End,
                M("outside", origin.Cmd, string.Join(M("and"), parts)), Warning);
        }

        diags.Sort((a, b) => a.Start.CompareTo(b.Start));
        return diags;
    }

    private static double FirstNumber(string text, int start, int end)
    {
        var segEnd = text.IndexOf(',', start, end - start);
        if (segEnd < 0) segEnd = end;
        return double.TryParse(text[start..segEnd].Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static (double X, double Y)? FirstTwoNumbers(string text, int start, int end)
    {
        var parts = text[start..end].Split(',');
        if (parts.Length < 2) return null;
        if (double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var y))
            return (x, y);
        return null;
    }

    // Builds the Monaco setMarkers message from a diagnostics list.
    public static string GetMarkersJson(List<ZplDiagnostic> diags)
    {
        var sb = new StringBuilder("{\"type\":\"setMarkers\",\"markers\":[");
        for (int i = 0; i < diags.Count; i++)
        {
            var d = diags[i];
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"s\":{d.Start},\"e\":{d.End},\"sev\":{d.Severity},\"m\":{JsonSerializer.Serialize(d.Message)}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void AnalyzeArgs(List<ZplDiagnostic> diags, List<int> lineStarts, string text,
        string cmd, ZplCommandDef def, int argsStart, int argsEnd, HashSet<string> textParams,
        bool includeLowWarnings)
    {
        var declared = def.Parameters ?? [];

        if (argsEnd - argsStart == 0)
        {
            var requiredNames = declared.Where(p => p.Required).Select(p => p.Name).ToList();
            if (requiredNames.Count == 1)
                Add(diags, lineStarts, argsStart - cmd.Length, argsStart,
                    M("requiredOne", cmd, Quote(requiredNames[0])), Warning);
            else if (requiredNames.Count > 1)
                Add(diags, lineStarts, argsStart - cmd.Length, argsStart,
                    M("requiredMany", cmd, JoinQuoted(requiredNames)), Warning);
            return;
        }

        if (declared.Count == 0)
        {
            if (includeLowWarnings)
                Add(diags, lineStarts, argsStart, argsEnd, M("noParams", cmd), LowWarning);
            return;
        }

        // Split arguments on commas. If the last declared parameter holds free text
        // (data, comment), commas inside it belong to the data: stop splitting there.
        bool lastIsText = textParams.Contains(declared[^1].Name);
        var segs = new List<(int S, int E)>();
        int segStart = argsStart;
        for (int j = argsStart; j <= argsEnd; j++)
        {
            bool boundary = j == argsEnd || text[j] == ',';
            if (!boundary) continue;
            if (lastIsText && segs.Count == declared.Count - 1)
            {
                segs.Add((segStart, argsEnd));
                segStart = argsEnd + 1;
                break;
            }
            segs.Add((segStart, j));
            segStart = j + 1;
        }

        // Required parameters absent/empty → warning (the command won't render right).
        for (int k = 0; k < declared.Count; k++)
        {
            if (!declared[k].Required) continue;
            bool missing = k >= segs.Count || text[segs[k].S..segs[k].E].Trim().Length == 0;
            if (!missing) continue;
            var (s, e) = k < segs.Count ? segs[k] : (argsStart - cmd.Length, argsEnd);
            Add(diags, lineStarts, s, e,
                M("requiredOne", cmd, Quote(declared[k].Name)), Warning);
        }

        // Extra parameters are ignored by the printer → low warning (clean code).
        if (segs.Count > declared.Count && includeLowWarnings)
        {
            var message = declared.Count == 1
                ? M("tooManyOne", cmd, segs.Count, Names(declared))
                : M("tooMany", cmd, segs.Count, declared.Count, Names(declared));
            Add(diags, lineStarts, segs[declared.Count].S, argsEnd, message, LowWarning);
        }

        // A number parameter given a non-number → warning (wrong render).
        int n = Math.Min(segs.Count, declared.Count);
        for (int k = 0; k < n; k++)
        {
            var p = declared[k];
            if (!p.IsNumber) continue;
            var (s, e) = segs[k];
            var val = text[s..e].Trim();
            if (val.Length == 0) continue; // omitted middle parameter (",,") — allowed
            if (!IsNumeric(val))
                Add(diags, lineStarts, s, e,
                    M("notNumber", cmd, Quote(p.Name), Quote(val)), Warning);
        }
    }

    // Returns the command definition whose span (command + arguments) contains
    // the given offset, or null when the caret is not on a known command.
    public static ZplCommandDef? FindCommandAt(string text, int offset, out string matched)
    {
        matched = "";
        if (string.IsNullOrEmpty(text)) return null;
        var lookup = ZplHighlighter.Lookup;

        int i = 0;
        while (i < text.Length)
        {
            if (text[i] is not ('^' or '~')) { i++; continue; }

            ZplCommandDef? def = null;
            int cmdLen = 0;
            if (i + 3 <= text.Length && lookup.TryGetValue(text.Substring(i, 3), out var d3)) { def = d3; cmdLen = 3; }
            else if (i + 2 <= text.Length && lookup.TryGetValue(text.Substring(i, 2), out var d2)) { def = d2; cmdLen = 2; }
            if (def is null) { i++; continue; }

            int end = SkipArgs(text, i + cmdLen);
            if (offset >= i && offset <= end)
            {
                matched = text.Substring(i, cmdLen);
                return def;
            }
            i = end;
        }
        return null;
    }

    // Arguments run until the next command prefix or end of line (same rule as the highlighter).
    private static int SkipArgs(string text, int start)
    {
        int i = start;
        while (i < text.Length && text[i] is not ('^' or '~' or '\r' or '\n')) i++;
        return i;
    }

    private static bool IsCommandChar(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '@';

    private static bool IsNumeric(string s)
    {
        int i = s[0] is '+' or '-' ? 1 : 0;
        if (i == s.Length) return false;
        bool dot = false, digit = false;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '.') { if (dot) return false; dot = true; }
            else if (c is >= '0' and <= '9') digit = true;
            else return false;
        }
        return digit;
    }

    private static string Names(List<ZplParamDef> parameters)
        => string.Join(", ", parameters.Select(p => p.Name));

    private static string JoinQuoted(List<string> names)
        => string.Join(", ", names.Select(Quote));

    private static string Quote(string text) => M("quote", text);

    private static string N(double value) => value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    // The messages come from the language file (app.analyzer.*).
    internal static string M(string key, params object[] args)
    {
        var format = LocalizationService.Get("app.analyzer." + key);
        return args.Length == 0 ? format : string.Format(format, args);
    }

    private static void Add(List<ZplDiagnostic> diags, List<int> lineStarts,
        int start, int end, string message, int severity)
    {
        diags.Add(new ZplDiagnostic
        {
            Start    = start,
            End      = Math.Max(end, start + 1), // Monaco needs a non-empty range
            Line     = OffsetToLine(lineStarts, start),
            Message  = message,
            Severity = severity,
        });
    }

    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int OffsetToLine(List<int> lineStarts, int offset)
    {
        int lo = 0, hi = lineStarts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo + 1; // 1-based
    }
}
