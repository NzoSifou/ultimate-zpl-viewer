using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// ── The command line ────────────────────────────────────────────────────────
//
// One grammar for everything the executable can be asked from a terminal. Whether a
// window opens is decided by ONE thing: an action. With --pdf, --png, --zpl or
// --print on the line, the file is processed and the program exits without ever
// showing itself; without one, the application opens as it would from a
// double-click, shaped by whatever the line said.
//
// The table below IS the grammar. The parser runs off it, the --help screen is
// printed from it and the settings page draws it. A switch that is not in it does
// not exist, and one that is in it cannot be documented differently in two places.
//
// Rules that hold for every switch:
//   * --name value and --name=value are the same thing;
//   * a switch that takes a list takes it either way: --hide toolbar,editor or
//     --hide toolbar --hide editor;
//   * nothing is guessed. An unknown switch, a value out of range, or a switch that
//     means nothing in this context is a usage error with exit code 2 — a script
//     that mistyped something finds out now, not after printing the wrong thing;
//   * the command line never writes the settings. What it forces on a window lasts
//     as long as that window.

/// <summary>What a parsed command line asks the executable to do.</summary>
internal enum CommandKind { Gui, Headless, Help, Version, ListPrinters, ListPapers, Error }

/// <summary>How the document is read, and whether it is rewritten on the way.</summary>
public sealed record DocumentOptions(
    double? Dpmm,            // the density the file is read at; null = the settings' default
    double? ViewRotate,      // the "Tourner" rotation: the render only
    double? TransformDpmm,   // "Transformer" › density: the ZPL is rewritten
    int TransformRotate,     // "Transformer" › rotation: the ZPL is rewritten (0, 90, 180, 270)
    bool RoundUp)            // "Transformer" › how a module that falls between two dots rounds
{
    public static readonly DocumentOptions None = new(null, null, null, 0, true);
    public bool HasTransform => TransformDpmm is not null || TransformRotate != 0;
}

/// <summary>Everything a run without a window needs.</summary>
internal sealed class HeadlessJob
{
    public string Input = "";
    public DocumentOptions Document = DocumentOptions.None;

    public string? PdfOut;
    public string? PngOut;
    public string? ZplOut;
    public double PngScale = 1;
    public double? MarginMm;         // null = the settings' print default when printing, 0 otherwise

    public bool Print;
    public string? Printer;
    public string PrinterType = "auto";   // auto | thermal | classic
    public int? Copies;
    public int? PerPage;
    public string? Paper;
    public PrintLayout? Layout;
    public string? PrintFile;

    public bool HasOutput => PdfOut is not null || PngOut is not null || ZplOut is not null;
}

internal sealed class ParsedCommand
{
    public CommandKind Kind;
    public string? Error;
    public LaunchOptions Gui = new();
    public HeadlessJob Job = new();
    public string? Printer;                     // --list-papers
    public List<string> Warnings { get; } = new();
}

/// <summary>One switch: its name, what it takes, where it is listed, and what it does.</summary>
internal sealed record CliOption(
    string Name,
    string? Value,
    string Group,
    string Key,
    Action<CliBuilder, string?> Apply,
    bool AllowsDashValue = false,
    string[]? Aliases = null);

internal sealed class CliBuilder
{
    public readonly List<string> Files = new();
    public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
    public readonly HeadlessJob Job = new();

    public double? Dpmm, ViewRotate, TransformDpmm;
    public int TransformRotate;
    public bool RoundUp = true;

    public bool? EditMode, ShowToolbar, ShowEditor;
    public bool NewWindow;
    public bool Help, Version, ListPrinters, ListPapers;

    public string? Error;
    public void Fail(string message) => Error ??= message;
}

internal static class CommandLine
{
    // The four densities the application knows, in dots per millimetre.
    private static readonly double[] Densities = { 6, 8, 12, 24 };

    public static readonly IReadOnlyList<string> Groups =
        new[] { "open", "read", "transform", "output", "print", "info" };

    public static readonly IReadOnlyList<CliOption> Options = new CliOption[]
    {
        // ── Opening (no action on the line) ─────────────────────────────────
        new("--mode", "view|edit", "open", "mode", (b, v) =>
        {
            b.EditMode = Lower(v) switch
            {
                "view" => false,
                "edit" => true,
                _ => Bad(b, "--mode", v, Choice("view", "edit")),
            };
        }),
        new("--show", "toolbar,editor", "open", "show", (b, v) => Panes(b, "--show", v, true)),
        new("--hide", "toolbar,editor", "open", "hide", (b, v) => Panes(b, "--hide", v, false)),
        new("--new-window", null, "open", "newWindow", (b, _) => b.NewWindow = true),

        // ── Reading the document ────────────────────────────────────────────
        new("--dpmm", "6|8|12|24", "read", "dpmm", (b, v) => b.Dpmm = Density(b, "--dpmm", v)),
        new("--view-rotate", "@degrees", "read", "viewRotate", (b, v) =>
        {
            if (Number(v) is { } deg) b.ViewRotate = ((deg % 360) + 360) % 360;
            else Bad(b, "--view-rotate", v, Msg("accept.angle"));
        }, AllowsDashValue: true),

        // ── Transforming it ─────────────────────────────────────────────────
        new("--transform-dpmm", "6|8|12|24", "transform", "transformDpmm",
            (b, v) => b.TransformDpmm = Density(b, "--transform-dpmm", v)),
        new("--transform-rotate", "90|180|270", "transform", "transformRotate", (b, v) =>
        {
            if (Number(v) is { } deg && ((int)deg % 360 + 360) % 360 is var r && deg == Math.Floor(deg) && r % 90 == 0)
                b.TransformRotate = r;
            else Bad(b, "--transform-rotate", v, Choice("90", "180", "270"));
        }, AllowsDashValue: true),
        new("--rounding", "up|down", "transform", "rounding", (b, v) =>
        {
            b.RoundUp = Lower(v) switch
            {
                "up" => true,
                "down" => false,
                _ => Bad(b, "--rounding", v, Choice("up", "down")) ?? true,
            };
        }),

        // ── Output ──────────────────────────────────────────────────────────
        new("--pdf", "@pdf", "output", "pdf", (b, v) => b.Job.PdfOut = v),
        new("--png", "@png", "output", "png", (b, v) => b.Job.PngOut = v),
        new("--zpl", "@zpl", "output", "zpl", (b, v) => b.Job.ZplOut = v),
        new("--png-scale", "@scale", "output", "pngScale", (b, v) =>
        {
            if (Number(v) is { } s && s > 0 && s <= 8) b.Job.PngScale = s;
            else Bad(b, "--png-scale", v, Msg("accept.scale"));
        }),
        new("--margin", "@length", "output", "margin", (b, v) =>
        {
            if (Length(v) is { } mm && mm >= 0) b.Job.MarginMm = mm;
            else Bad(b, "--margin", v, Msg("accept.length"));
        }),

        // ── Printing ────────────────────────────────────────────────────────
        new("--print", null, "print", "print", (b, _) => b.Job.Print = true),
        new("--printer", "@name", "print", "printer", (b, v) => b.Job.Printer = v),
        new("--printer-type", "auto|thermal|classic", "print", "printerType", (b, v) =>
        {
            var t = Lower(v);
            if (t is "auto" or "thermal" or "classic") b.Job.PrinterType = t;
            else Bad(b, "--printer-type", v, Choice("auto", "thermal", "classic"));
        }),
        new("--copies", "n", "print", "copies", (b, v) => b.Job.Copies = Count(b, "--copies", v)),
        new("--per-page", "n", "print", "perPage", (b, v) => b.Job.PerPage = Count(b, "--per-page", v)),
        new("--paper", "@name", "print", "paper", (b, v) => b.Job.Paper = v),
        new("--layout", "portrait|landscape|portrait-flipped|landscape-flipped", "print", "layout", (b, v) =>
        {
            b.Job.Layout = Lower(v) switch
            {
                "portrait" => PrintLayout.Portrait,
                "landscape" => PrintLayout.Landscape,
                "portrait-flipped" => PrintLayout.PortraitFlipped,
                "landscape-flipped" => PrintLayout.LandscapeFlipped,
                _ => Bad<PrintLayout?>(b, "--layout", v,
                    Choice("portrait", "landscape", "portrait-flipped", "landscape-flipped")),
            };
        }),
        new("--print-file", "@file", "print", "printFile", (b, v) => b.Job.PrintFile = v),

        // ── Information ─────────────────────────────────────────────────────
        new("--list-printers", null, "info", "listPrinters", (b, _) => b.ListPrinters = true),
        new("--list-papers", null, "info", "listPapers", (b, _) => b.ListPapers = true),
        new("--version", null, "info", "version", (b, _) => b.Version = true),
        new("--help", null, "info", "help", (b, _) => b.Help = true, Aliases: new[] { "-h", "/?", "-?", "/h" }),
    };

    // Switches that shape a WINDOW, and so mean nothing when no window opens.
    private static readonly string[] GuiOnly = { "--mode", "--show", "--hide", "--new-window" };

    // Switches that only mean something to a print.
    private static readonly string[] PrintOnly =
        { "--printer-type", "--copies", "--per-page", "--paper", "--layout", "--print-file" };

    private static readonly string[] Actions = { "--pdf", "--png", "--zpl", "--print" };

    // ── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>Reads a command line. args[0] is the executable, as Environment gives it.</summary>
    public static ParsedCommand Parse(string[] args)
    {
        var b = new CliBuilder();

        for (int i = 1; i < args.Length && b.Error is null; i++)
        {
            var token = args[i];

            // Positional: the documents. A lone "-" is not a switch either.
            if (!IsSwitch(token))
            {
                b.Files.Add(token);
                continue;
            }

            // --name=value
            string name = token;
            string? inline = null;
            int eq = token.IndexOf('=');
            if (eq > 0 && token.StartsWith("--", StringComparison.Ordinal))
            {
                name = token[..eq];
                inline = token[(eq + 1)..];
            }

            var option = Find(name);
            if (option is null)
            {
                b.Fail(UnknownMessage(name));
                break;
            }

            string? value = null;
            if (option.Value is not null)
            {
                if (inline is not null) value = inline;
                else if (i + 1 < args.Length && (option.AllowsDashValue || !IsSwitch(args[i + 1])))
                    value = args[++i];
                else
                {
                    b.Fail(Msg("needsValue", option.Name, ValueName(option.Value)));
                    break;
                }
                if (string.IsNullOrWhiteSpace(value))
                {
                    b.Fail(Msg("needsValue", option.Name, ValueName(option.Value)));
                    break;
                }
            }
            else if (inline is not null)
            {
                b.Fail(Msg("noValue", option.Name));
                break;
            }

            // A switch that takes a single value may not be given twice with two
            // different ones; the lists (--show, --hide) accumulate instead.
            bool accumulates = option.Name is "--show" or "--hide";
            if (!accumulates && !b.Seen.Add(option.Name) && option.Value is not null)
            {
                b.Fail(Msg("twice", option.Name));
                break;
            }
            b.Seen.Add(option.Name);
            option.Apply(b, value);
        }

        return Resolve(b);
    }

    private static ParsedCommand Resolve(CliBuilder b)
    {
        var result = new ParsedCommand();
        ParsedCommand Error(string message)
        {
            result.Kind = CommandKind.Error;
            result.Error = message;
            return result;
        }

        if (b.Error is not null) return Error(b.Error);

        // --help wins over everything, including mistakes elsewhere on the line: it
        // is what someone types when they do not know what else to type.
        if (b.Help) { result.Kind = CommandKind.Help; return result; }

        // The information switches stand alone. --list-papers is the exception that
        // needs to know which printer it is about.
        int infos = (b.Version ? 1 : 0) + (b.ListPrinters ? 1 : 0) + (b.ListPapers ? 1 : 0);
        if (infos > 0)
        {
            if (infos > 1) return Error(Msg("infoTogether"));
            var others = b.Seen.Where(s => s is not ("--version" or "--list-printers" or "--list-papers")
                                           && !(b.ListPapers && s == "--printer")).ToList();
            if (others.Count > 0 || b.Files.Count > 0)
                return Error(b.ListPapers ? Msg("alonePapers") : Msg("alone", InfoName(b)));
            result.Kind = b.Version ? CommandKind.Version
                        : b.ListPrinters ? CommandKind.ListPrinters : CommandKind.ListPapers;
            result.Printer = b.Job.Printer;
            return result;
        }

        var document = new DocumentOptions(b.Dpmm, b.ViewRotate, b.TransformDpmm, b.TransformRotate, b.RoundUp);
        bool headless = Actions.Any(b.Seen.Contains);

        if (b.Seen.Contains("--rounding") && b.TransformDpmm is null)
            return Error(Msg("roundingOnly"));


        if (headless)
        {
            if (b.Seen.FirstOrDefault(s => GuiOnly.Contains(s, StringComparer.OrdinalIgnoreCase)) is { } gui)
                return Error(Msg("guiOnly", gui));
            if (b.Files.Count == 0)
                return Error(Msg("noFile"));
            if (b.Files.Count > 1)
                return Error(Msg("oneFile", b.Files.Count));
            if (!b.Job.Print)
            {
                if (b.Seen.FirstOrDefault(s => PrintOnly.Contains(s, StringComparer.OrdinalIgnoreCase)) is { } p)
                    return Error(Msg("printOnly", p));
                if (b.Seen.Contains("--printer"))
                    return Error(Msg("printerOnly"));
            }
            if (b.Seen.Contains("--png-scale") && b.Job.PngOut is null)
                return Error(Msg("pngScaleOnly"));
            if (b.Seen.Contains("--margin") && b.Job.HasOutput && b.Job.PdfOut is null && b.Job.PngOut is null && !b.Job.Print)
                return Error(Msg("marginZpl"));

            b.Job.Input = b.Files[0];
            b.Job.Document = document;
            result.Kind = CommandKind.Headless;
            result.Job = b.Job;
            return result;
        }

        // A window opens. Everything that belongs to a job without one is a mistake.
        var jobOnly = new[] { "--png-scale", "--margin", "--printer" }.Concat(PrintOnly)
            .FirstOrDefault(b.Seen.Contains);
        if (jobOnly is not null)
            return Error(Msg("jobOnly", jobOnly));

        // --mode and --view-rotate describe the window and still mean something on
        // a home page: the documents opened in it later take them. A density and a
        // transform are about particular documents, and there are none.
        if (b.Files.Count == 0 && (b.Dpmm is not null || document.HasTransform))
            return Error(Msg("noDocuments"));

        result.Kind = CommandKind.Gui;
        result.Gui = new LaunchOptions
        {
            Files = b.Files.ToList(),
            EditMode = b.EditMode,
            ShowToolbar = b.ShowToolbar,
            ShowEditor = b.ShowEditor,
            NewWindow = b.NewWindow,
            Document = document,
        };
        return result;
    }

    private static string InfoName(CliBuilder b)
        => b.Version ? "--version" : b.ListPrinters ? "--list-printers" : "--list-papers";

    // A negative number is a value, never a switch: "--margin -3" is a wrong margin,
    // not a margin without a value.
    private static bool IsSwitch(string token)
        => token.Length > 1
           && ((token[0] == '-' && !char.IsDigit(token[1]) && token[1] != '.')
               || (token[0] == '/' && token.Length <= 3));

    private static CliOption? Find(string name)
        => Options.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
            || (o.Aliases?.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ?? false));

    private static string UnknownMessage(string name)
    {
        var closest = Options
            .Select(o => (o.Name, Distance: Levenshtein(name.ToLowerInvariant(), o.Name)))
            .OrderBy(x => x.Distance)
            .First();
        var message = Msg("unknown", name);
        if (closest.Distance <= Math.Max(2, closest.Name.Length / 4))
            message += " " + Msg("didYouMean", closest.Name);
        else if (name.Equals("-o", StringComparison.OrdinalIgnoreCase) || name.Equals("--output", StringComparison.OrdinalIgnoreCase))
            message += " " + Msg("hintOutput");
        else if (name.Equals("--rotate", StringComparison.OrdinalIgnoreCase))
            message += " " + Msg("hintRotate");
        else if (name.Equals("--unit", StringComparison.OrdinalIgnoreCase))
            message += " " + Msg("hintUnit");
        return message;
    }

    // ── Value readers ────────────────────────────────────────────────────────

    private static string Lower(string? value) => (value ?? "").Trim().ToLowerInvariant();

    private static bool? Bad(CliBuilder b, string name, string? value, string accepted)
    {
        b.Fail(Msg("badValue", name, value, accepted));
        return null;
    }

    private static T? Bad<T>(CliBuilder b, string name, string? value, string accepted)
    {
        b.Fail(Msg("badValue", name, value, accepted));
        return default;
    }

    /// <summary>A message of the command line, in the application's language.</summary>
    internal static string Msg(string key, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture,
                         LocalizationService.Get("settings.commandLine.msg." + key), args);

    /// <summary>"a, b or c", with the language's own "or".</summary>
    private static string Choice(params string[] values)
        => values.Length < 2 ? string.Concat(values)
         : string.Join(", ", values[..^1]) + " " + Msg("or") + " " + values[^1];

    /// <summary>What a value is called: a literal list, or a word from the language file.</summary>
    internal static string ValueName(string value)
        => value.StartsWith('@') ? LocalizationService.Get("settings.commandLine.val." + value[1..]) : value;

    private static double? Number(string? value)
        => double.TryParse((value ?? "").Trim().Replace(',', '.'),
                           NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? Density(CliBuilder b, string name, string? value)
    {
        if (Number(value) is { } d && Densities.Contains(d)) return d;
        Bad(b, name, value, Choice("6", "8", "12", "24"));
        return null;
    }

    private static int? Count(CliBuilder b, string name, string? value)
    {
        if (int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && n >= 1 && n <= 9999) return n;
        Bad(b, name, value, Msg("accept.count"));
        return null;
    }

    // A length with an optional unit stuck to it; a bare number is millimetres.
    private static double? Length(string? value)
    {
        var v = Lower(value).Replace(" ", "");
        double factor = 1;
        if (v.EndsWith("mm")) v = v[..^2];
        else if (v.EndsWith("cm")) { v = v[..^2]; factor = 10; }
        else if (v.EndsWith("in")) { v = v[..^2]; factor = 25.4; }
        return Number(v) is { } n ? n * factor : null;
    }

    private static void Panes(CliBuilder b, string name, string? value, bool show)
    {
        foreach (var raw in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pane = raw.Trim().ToLowerInvariant();
            bool? previous;
            switch (pane)
            {
                case "toolbar":
                    previous = b.ShowToolbar;
                    b.ShowToolbar = show;
                    break;
                case "editor":
                    previous = b.ShowEditor;
                    b.ShowEditor = show;
                    break;
                default:
                    Bad(b, name, raw.Trim(), Choice("toolbar", "editor"));
                    return;
            }
            if (previous is not null && previous != show)
            {
                b.Fail(Msg("showAndHide", pane));
                return;
            }
        }
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                   d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ── The help screen ──────────────────────────────────────────────────────

    /// <summary>The usage screen, in the application's language.</summary>
    public static string HelpText()
    {
        var sb = new StringBuilder();
        string L(string key) => LocalizationService.Get("settings.commandLine." + key);

        sb.AppendLine();
        sb.AppendLine("Ultimate ZPL Viewer " + DisplayVersion());
        sb.AppendLine(L("subtitle"));
        sb.AppendLine();
        sb.AppendLine(L("help.usage"));
        sb.AppendLine("  \"Ultimate ZPL Viewer.exe\" [" + L("help.files") + "] [options]");
        sb.AppendLine();
        foreach (var line in Wrap(L("help.rule"), 76)) sb.AppendLine("  " + line);

        foreach (var group in Groups)
        {
            sb.AppendLine();
            sb.AppendLine(L("sec." + group).ToUpperInvariant());
            var intro = L("desc." + group);
            if (intro.Length > 0)
                foreach (var line in Wrap(intro, 76)) sb.AppendLine("  " + line);
            foreach (var o in Options.Where(o => o.Group == group))
            {
                var head = "  " + Signature(o);
                var text = Wrap(L("opt." + o.Key), 48).ToList();
                if (head.Length > 29)
                {
                    sb.AppendLine(head);
                    foreach (var line in text) sb.AppendLine(new string(' ', 30) + line);
                }
                else
                {
                    sb.AppendLine(head.PadRight(30) + (text.Count > 0 ? text[0] : ""));
                    foreach (var line in text.Skip(1)) sb.AppendLine(new string(' ', 30) + line);
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(L("help.exitTitle").ToUpperInvariant());
        foreach (var line in Wrap(L("help.exitCodes"), 76)) sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine(L("sec.examples").ToUpperInvariant());
        foreach (var (command, key) in Examples)
        {
            sb.AppendLine("  " + L("example." + key));
            sb.AppendLine("    " + command);
        }
        sb.AppendLine();
        foreach (var line in Wrap(L("help.wait"), 76)) sb.AppendLine("  " + line);
        return sb.ToString();
    }

    /// <summary>"--name &lt;value&gt;", with the aliases of --help in front.</summary>
    public static string Signature(CliOption o)
    {
        var names = o.Aliases is { Length: > 0 } ? $"{o.Aliases[0]}, {o.Name}" : o.Name;
        return o.Value is null ? names : $"{names} <{ValueName(o.Value)}>";
    }

    public static readonly IReadOnlyList<(string Command, string Key)> Examples = new[]
    {
        ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --mode edit --hide editor", "open"),
        ("\"Ultimate ZPL Viewer.exe\" a.zpl b.zpl --new-window", "several"),
        ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --pdf etiquette.pdf --png etiquette.png", "export"),
        ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --transform-dpmm 12 --zpl etiquette-300dpi.zpl", "convert"),
        ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --transform-rotate 90 --zpl tournee.zpl --pdf tournee.pdf", "rotate"),
        ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --print --printer \"Canon\" --paper A4 --per-page 4 --margin 5mm", "print"),
        ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --print --printer \"ZDesigner ZD420\" --copies 10", "printRaw"),
    };

    /// <summary>
    /// Text a console prints faithfully: every accented letter French uses is in the
    /// console's own code page, but typographic punctuation often is not and comes out
    /// as '?' — so that, and only that, is flattened.
    /// </summary>
    internal static string ConsoleSafe(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c switch
            {
                '\u201c' or '\u201d' => "\"",
                '\u2019' or '\u2018' => "'",
                '\u2014' or '\u2013' or '\u2212' => "-",
                '\u2026' => "...",
                '\u00a0' or '\u202f' => " ",
                '\u2192' => "->",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>The version as people write it: 1.6.1, not 1.6.1.0.</summary>
    internal static string DisplayVersion()
    {
        var v = UpdateService.CurrentVersion();
        while (v.EndsWith(".0") && v.Count(c => c == '.') > 2) v = v[..^2];
        return v;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in Words(paragraph))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }

    // The words of a paragraph, with French spaced punctuation kept on the word it
    // belongs to: a line never starts with "»", ":" or ";", nor ends with "«".
    private static IEnumerable<string> Words(string paragraph)
    {
        var words = new List<string>();
        string? opening = null;
        foreach (var w in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (w == "«") { opening = w; continue; }
            var word = opening is null ? w : opening + "\u00a0" + w;
            opening = null;
            if (words.Count > 0 && w is "»" or ":" or ";" or "!" or "?")
                words[^1] += "\u00a0" + w;
            else if (words.Count > 0 && w.StartsWith('»'))
                words[^1] += "\u00a0" + w;
            else
                words.Add(word);
        }
        if (opening is not null) words.Add(opening);
        return words;
    }
}
