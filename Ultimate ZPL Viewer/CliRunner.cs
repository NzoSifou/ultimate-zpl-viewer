using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// Runs what the command line asked for when no window is to open: the outputs, the
// print, and the few questions a script needs answered (which printers, which
// papers, which version). Entered from App.OnLaunched, on the already-initialised
// UI thread so that measuring ZPL text stays pixel-accurate, and always ends the
// process with an exit code:
//
//   0  done
//   1  the command was understood but could not be carried out
//   2  the command line itself is wrong
//
// It never writes the settings. It READS them — the default density, the default
// printer and the default print values — because a run from a script should do
// what the same click in the application would do.
internal static class CliRunner
{
    public const int Ok = 0, Failed = 1, Usage = 2;

    // ── Information ──────────────────────────────────────────────────────────

    public static int PrintHelp()
    {
        Out(CommandLine.HelpText());
        return Ok;
    }

    public static int PrintVersion()
    {
        Out("Ultimate ZPL Viewer " + CommandLine.DisplayVersion());
        return Ok;
    }

    public static int UsageError(string message)
    {
        Err(M("error", message));
        Err(M("helpHint", "\"Ultimate ZPL Viewer.exe\" --help"));
        return Usage;
    }

    public static int ListPrinters()
    {
        var settings = AppSettings.Load();
        var printers = Printers();
        if (printers.Count == 0) { Out(M("noPrinters")); return Ok; }

        var windowsDefault = WindowsDefaultPrinter();
        int width = Math.Max(10, printers.Max(p => p.Length)) + 2;
        foreach (var p in printers)
        {
            var type = PrintJobService.ModeFor(settings, p) == SendMode.Raw ? "thermal" : "classic";
            var how = M(PrintJobService.IsPinned(settings, p) ? "typeChosen" : "typeDetected");
            var marks = new List<string>();
            if (string.Equals(p, DefaultPrinter(settings), StringComparison.OrdinalIgnoreCase)) marks.Add(M("markDefault"));
            else if (string.Equals(p, windowsDefault, StringComparison.OrdinalIgnoreCase)) marks.Add(M("markWindows"));
            Out($"{p.PadRight(width)}{type,-9}({how}){(marks.Count > 0 ? "  [" + string.Join(", ", marks) + "]" : "")}");
        }
        return Ok;
    }

    public static int ListPapers(string? requested)
    {
        var settings = AppSettings.Load();
        var printer = ResolvePrinter(requested, settings, out var error);
        if (printer is null) return Fail(error!);

        var papers = PrintJobService.PaperSizes(printer);
        if (papers.Count == 0) { Out(M("noPapers", printer)); return Ok; }
        Out(M("papersOf", printer));
        int width = papers.Max(p => p.Name.Trim().Length) + 2;
        foreach (var (name, w, h) in papers)
            Out($"  {name.Trim().PadRight(width)}{Mm(w)} x {Mm(h)} mm");
        return Ok;
    }

    /// <summary>
    /// A window is about to open without some of the files it was given: said in the
    /// terminal that asked, where a mistyped path would otherwise just be a window
    /// showing something else.
    /// </summary>
    public static void WarnAboutMissingFiles(IEnumerable<string> files)
    {
        foreach (var f in files)
            if (!File.Exists(f)) Warn(M("missingNotOpened", f));
    }

    // ── The work ─────────────────────────────────────────────────────────────

    public static int Run(HeadlessJob job, IEnumerable<string> warnings)
    {
        foreach (var w in warnings) Warn(w);

        if (!File.Exists(job.Input)) return Fail(M("missing", job.Input));
        string original;
        try { original = File.ReadAllText(job.Input); }
        catch (Exception ex) { return Fail(M("readFailed", job.Input, ex.Message)); }

        var settings = AppSettings.Load();
        var doc = job.Document;

        // The density the file is READ at, then the one it is written for after the
        // transform. A file that declares its own (^JM) keeps declaring it: that is
        // what the application does with it too.
        double readDpmm = doc.Dpmm ?? settings.DefaultDpmm;
        string zpl = original;
        double renderDpmm = readDpmm;

        if (doc.HasTransform)
        {
            ZplTransform.Plan plan;
            double current = readDpmm;
            try
            {
                var read = ZplRenderer.Parse(original, readDpmm);
                current = read.DeclaredDpmm ?? readDpmm;
                if (doc.TransformDpmm is { } same && Math.Abs(same - current) < 1e-9)
                    Warn(M("sameDensity", Dpmm(same)));
                plan = ZplTransform.Build(original, current, doc.TransformDpmm, doc.TransformRotate, doc.RoundUp);
            }
            catch (Exception ex) { return Fail(M("transformFailed", ex.Message)); }

            foreach (var e in plan.Edits.OrderByDescending(e => e.Start))
                zpl = zpl[..e.Start] + e.Text + zpl[e.End..];
            foreach (var note in plan.Notes)
                Warn(string.Format(LocalizationService.Get("transform.notes." + note.Kind), note.Command, note.Line));
            if (doc.TransformDpmm is { } target) renderDpmm = target;

            var what = new List<string>();
            if (doc.TransformDpmm is { } to) what.Add($"{Dpmm(current)} -> {Dpmm(to)} dpmm");
            if (doc.TransformRotate != 0) what.Add(M("rotation", doc.TransformRotate));
            Info(M("transformed", string.Join(", ", what)));
        }

        double viewRotate = doc.ViewRotate ?? 0;

        if (job.ZplOut is not null)
        {
            try
            {
                CreateParentDir(job.ZplOut);
                File.WriteAllText(job.ZplOut, zpl, new UTF8Encoding(false));
                Info(M("written", "ZPL", job.ZplOut));
            }
            catch (Exception ex) { return Fail(M("writeFailed", "ZPL", ex.Message)); }
        }

        if (job.PdfOut is not null || job.PngOut is not null)
        {
            double marginMm = job.MarginMm ?? 0;
            byte[] pdf;
            try { pdf = RenderPdf(zpl, renderDpmm, viewRotate, marginMm); }
            catch (Exception ex) { return Fail(M("renderFailed", ex.Message)); }

            if (job.PdfOut is not null)
            {
                try
                {
                    CreateParentDir(job.PdfOut);
                    File.WriteAllBytes(job.PdfOut, pdf);
                    Info(M("written", "PDF", job.PdfOut));
                }
                catch (Exception ex) { return Fail(M("writeFailed", "PDF", ex.Message)); }
            }

            if (job.PngOut is not null)
            {
                try
                {
                    // One ZPL dot is one pixel at scale 1, the label's own resolution.
                    int dpi = (int)Math.Round(25.4 * renderDpmm * job.PngScale);
                    using var bitmap = PDFtoImage.Conversion.ToImage(
                        pdf, options: new PDFtoImage.RenderOptions(Dpi: dpi));
                    CreateParentDir(job.PngOut);
                    using var stream = File.Create(job.PngOut);
                    bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
                    Info(M("written", "PNG", job.PngOut) + $" ({bitmap.Width} x {bitmap.Height} px)");
                }
                catch (Exception ex) { return Fail(M("writeFailed", "PNG", ex.Message)); }
            }
        }

        if (job.Print) return Print(job, settings, zpl, renderDpmm, viewRotate);
        return Ok;
    }

    // ── Printing ─────────────────────────────────────────────────────────────

    private static int Print(HeadlessJob job, AppSettings settings, string zpl, double dpmm, double viewRotate)
    {
        var printer = ResolvePrinter(job.Printer, settings, out var error);
        if (printer is null) return Fail(error!);

        var mode = job.PrinterType switch
        {
            "thermal" => SendMode.Raw,
            "classic" => SendMode.Image,
            _ => PrintJobService.ModeFor(settings, printer),
        };

        // The values the application's own defaults give, where the line said
        // nothing. Only FIXED defaults: "whatever the last print used" is a
        // memory of the window, and a script should not depend on it.
        int copies = job.Copies ?? (settings.CopiesMode == "fixed" ? settings.DefaultCopies : 1);
        int perPage = job.PerPage ?? (settings.PerPageMode == "fixed" ? settings.DefaultPerPage : 1);
        var layout = job.Layout ?? (settings.LayoutMode == "fixed"
            ? PrintJobService.LayoutFromKey(settings.DefaultLayout) : PrintLayout.Portrait);
        double margins = job.MarginMm ?? (settings.MarginsMode == "fixed" ? settings.DefaultMarginsMm : 0);

        string paper = "";
        if (job.Paper is { } wanted)
        {
            var papers = PrintJobService.PaperSizes(printer);
            var match = papers.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                return Fail(M("unknownPaper", printer, wanted,
                              $"--list-papers --printer \"{printer}\""));
            paper = match.Name;
        }

        var printJob = new PrintJob(printer, mode, copies, layout, paper, margins, perPage);

        try
        {
            if (mode == SendMode.Raw)
            {
                // A thermal printer is fed the ZPL and picks its own media: only the
                // copy count and a half turn can be said in ZPL. Anything else on the
                // line would be silently dropped, so it is said out loud instead.
                if (job.PerPage is not null && perPage > 1) Warn(M("rawIgnores", "--per-page"));
                if (job.Paper is not null) Warn(M("rawIgnores", "--paper"));
                if (job.MarginMm is not null && margins > 0) Warn(M("rawIgnores", "--margin"));
                if (layout is PrintLayout.Landscape or PrintLayout.LandscapeFlipped)
                    Warn(M("rawLandscape"));
                if (viewRotate != 0)
                    Warn(M("rawViewRotate"));

                var raw = PrintJobService.BuildRawZpl(zpl, printJob);
                // To a file, the bytes are written as they are: a virtual printer's
                // driver (PDF, XPS) silently drops RAW data, and what a thermal printer
                // would receive is exactly these bytes anyway.
                if (job.PrintFile is not null)
                {
                    CreateParentDir(job.PrintFile);
                    File.WriteAllBytes(job.PrintFile, Encoding.UTF8.GetBytes(raw));
                }
                else RawPrinterService.SendRaw(printer, raw, DocumentName(job));
            }
            else
            {
                var snapshot = RenderSnapshotFor(zpl, dpmm, viewRotate, out double wMm, out double hMm);
                PrintJobService.PrintImage(printJob, snapshot, wMm, hMm, job.PrintFile);
            }
        }
        catch (Exception ex) { return Fail(M("printFailed", printer, ex.Message)); }

        var sent = mode == SendMode.Raw
            ? M("sentRaw", printer, copies)
            : M("sentClassic", printer, copies, perPage, PrintJobService.KeyOf(layout))
              + (paper.Length > 0 ? $", {paper}" : "")
              + (margins > 0 ? ", " + M("margins", Mm(margins)) : "");
        Info(sent + (job.PrintFile is not null ? $" -> {job.PrintFile}" : ""));
        return Ok;
    }

    private static string DocumentName(HeadlessJob job)
        => "Ultimate ZPL Viewer - " + Path.GetFileName(job.Input);

    /// <summary>
    /// The printer to use: the one named, else the application's default printer
    /// when it names one that is there, else the one Windows prints to by default.
    /// </summary>
    private static string? ResolvePrinter(string? requested, AppSettings settings, out string? error)
    {
        error = null;
        var printers = Printers();
        if (printers.Count == 0) { error = M("noPrinters"); return null; }

        if (requested is not null)
        {
            var match = printers.FirstOrDefault(p => string.Equals(p, requested, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                error = M("printerNotFound", requested, string.Join(", ", printers));
            return match;
        }

        var chosen = DefaultPrinter(settings);
        if (chosen is not null) return chosen;
        var windows = WindowsDefaultPrinter();
        if (windows is not null && printers.Contains(windows, StringComparer.OrdinalIgnoreCase)) return windows;
        error = M("noDefaultPrinter");
        return null;
    }

    private static string? DefaultPrinter(AppSettings settings)
    {
        var chosen = settings.DefaultPrinter;
        if (string.IsNullOrWhiteSpace(chosen) || chosen == "last") return null;
        return Printers().FirstOrDefault(p => string.Equals(p, chosen, StringComparison.OrdinalIgnoreCase));
    }

    private static string? WindowsDefaultPrinter()
    {
        try { return new PrinterSettings().PrinterName; }
        catch { return null; }
    }

    private static List<string> Printers()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Print\Printers");
        return key?.GetSubKeyNames()
            // The application's own capture printer is not somewhere to print to.
            .Where(n => !string.Equals(n, VirtualPrinterService.PrinterName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n)
            .ToList() ?? new List<string>();
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private static byte[] RenderPdf(string zpl, double dpmm, double rotate, double marginMm)
    {
        var model = ZplRenderer.Parse(zpl, dpmm);
        return ZplRenderer.ToPdf(model, dpmm, rotate, marginMm * dpmm);
    }

    /// <summary>
    /// The label as pixels, the way the print dialog hands them to a classic printer:
    /// no border (the page margins are the printer's business), at the label's own
    /// resolution, turned the way the view is. The size comes back in millimetres.
    /// </summary>
    private static RenderSnapshot RenderSnapshotFor(string zpl, double dpmm, double rotate,
                                                    out double widthMm, out double heightMm)
    {
        var pdf = RenderPdf(zpl, dpmm, rotate, 0);
        int dpi = (int)Math.Round(25.4 * dpmm);
        using var rendered = PDFtoImage.Conversion.ToImage(pdf, options: new PDFtoImage.RenderOptions(Dpi: dpi));
        using var bgra = rendered.Copy(SkiaSharp.SKColorType.Bgra8888);
        widthMm = bgra.Width / dpmm;
        heightMm = bgra.Height / dpmm;
        return new RenderSnapshot(bgra.Width, bgra.Height, bgra.Bytes);
    }

    // ── Console ──────────────────────────────────────────────────────────────

    private static int Fail(string message)
    {
        Err(M("error", message));
        return Failed;
    }

    private static void Info(string message) => Out(message);
    private static void Warn(string message) => Err(M("warning", message));
    private static string M(string key, params object?[] args) => CommandLine.Msg(key, args);

    // Written in the console's own code page, which every French letter is in. Only
    // the typographic punctuation that old code pages lack is flattened.
    private static void Out(string message)
    {
        EnsureConsole();
        Console.Out.WriteLine(CommandLine.ConsoleSafe(message));
        Console.Out.Flush();
    }

    private static void Err(string message)
    {
        EnsureConsole();
        Console.Error.WriteLine(CommandLine.ConsoleSafe(message));
        Console.Error.Flush();
    }

    private static string Mm(double v) => v.ToString(v < 100 ? "0.#" : "0", CultureInfo.InvariantCulture);
    private static string Dpmm(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static void CreateParentDir(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    // A WinExe (GUI-subsystem) process has no console of its own; it borrows the
    // one of the terminal that started it, so that what it says is seen there. It
    // must happen before anything touches Console, which binds its streams once.
    private static bool _consoleReady;
    internal static void EnsureConsole()
    {
        if (_consoleReady) return;
        _consoleReady = true;
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
