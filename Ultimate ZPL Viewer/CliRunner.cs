using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Ultimate_ZPL_Viewer;

// Headless command-line conversion: turns a .zpl file into a PDF and/or PNG
// without ever opening the app window. Entered from App.OnLaunched when the
// command line carries an output flag; runs on the already-initialised UI
// thread (so ZPL text measurement stays pixel-accurate) and exits the process.
//
//   ultimatezplviewer.exe path/to/file.zpl -o --pdf path/to/file.pdf
//   ultimatezplviewer.exe path/to/file.zpl -o --png path/to/file.png
//   (optional: --dpmm <n> [default 8], --rotate <deg> [default 0],
//              --margin <n> --unit <mm|cm|in> [default 0 mm] → border on all sides)
internal static class CliRunner
{
    internal sealed class Job
    {
        public string Input = "";
        public string? PdfOut;
        public string? PngOut;
        public double Dpmm = 8;
        public double Rotate;
        public double Margin;        // value in Unit; 0 = no margin
        public string Unit = "mm";   // mm | cm | in
    }

    // Returns a Job when the command line requests a conversion, else null (the
    // app then starts normally). args come from Environment.GetCommandLineArgs()
    // (args[0] is the executable path).
    public static Job? Parse(string[] args)
    {
        var job = new Job();
        string? input = null;
        bool hasOutputFlag = false;

        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--pdf":
                    hasOutputFlag = true;
                    if (i + 1 < args.Length) job.PdfOut = args[++i];
                    break;
                case "--png":
                    hasOutputFlag = true;
                    if (i + 1 < args.Length) job.PngOut = args[++i];
                    break;
                case "--dpmm":
                    if (i + 1 < args.Length &&
                        double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                        job.Dpmm = d;
                    break;
                case "--rotate":
                    if (i + 1 < args.Length &&
                        double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                        job.Rotate = r;
                    break;
                case "--margin":
                    if (i + 1 < args.Length &&
                        double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var mg) && mg >= 0)
                        job.Margin = mg;
                    break;
                case "--unit":
                    if (i + 1 < args.Length)
                    {
                        var u = args[++i].Trim().ToLowerInvariant();
                        if (u is "mm" or "cm" or "in") job.Unit = u;
                    }
                    break;
                case "-o":
                case "--output":
                    // Accepted for convenience; the real target is --pdf/--png.
                    break;
                default:
                    // First non-flag token is the input file.
                    if (input is null && !a.StartsWith('-')) input = a;
                    break;
            }
        }

        if (!hasOutputFlag) return null; // no conversion requested → normal launch
        job.Input = input ?? "";
        return job;
    }

    // Performs the conversion and returns a process exit code (0 = success).
    public static int Run(Job job)
    {
        EnsureConsole();

        if (string.IsNullOrWhiteSpace(job.Input))
            return Fail("Aucun fichier ZPL en entrée n'a été indiqué.");
        if (!File.Exists(job.Input))
            return Fail($"Fichier introuvable : {job.Input}");

        string zpl;
        try { zpl = File.ReadAllText(job.Input); }
        catch (Exception ex) { return Fail($"Lecture impossible de « {job.Input} » : {ex.Message}"); }

        // Margin (--margin N --unit cm|mm|in) → dots. The model itself is always
        // auto-sized from the elements when ^PW/^LL are absent (ZplRenderer.Parse).
        double marginMm = job.Unit switch { "cm" => job.Margin * 10, "in" => job.Margin * 25.4, _ => job.Margin };
        double marginDots = marginMm * job.Dpmm;

        ZplRenderModel model;
        byte[] pdf;
        try
        {
            model = ZplRenderer.Parse(zpl, job.Dpmm);
            pdf = ZplRenderer.ToPdf(model, job.Dpmm, job.Rotate, marginDots);
        }
        catch (Exception ex) { return Fail($"Échec du rendu ZPL : {ex.Message}"); }

        if (job.PdfOut is not null)
        {
            try
            {
                CreateParentDir(job.PdfOut);
                File.WriteAllBytes(job.PdfOut, pdf);
                Info($"PDF écrit : {job.PdfOut}");
            }
            catch (Exception ex) { return Fail($"Écriture PDF impossible : {ex.Message}"); }
        }

        if (job.PngOut is not null)
        {
            try
            {
                // Rasterise the vector PDF at the label's own resolution so one
                // ZPL dot maps to exactly one pixel (matches Labelary at N dpmm).
                int dpi = (int)Math.Round(25.4 * job.Dpmm);
                using var skBitmap = PDFtoImage.Conversion.ToImage(
                    pdf, options: new PDFtoImage.RenderOptions(Dpi: dpi));
                CreateParentDir(job.PngOut);
                using var stream = File.Create(job.PngOut);
                skBitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
                Info($"PNG écrit : {job.PngOut}");
            }
            catch (Exception ex) { return Fail($"Écriture PNG impossible : {ex.Message}"); }
        }

        return 0;
    }

    private static void CreateParentDir(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private static int Fail(string message)
    {
        EnsureConsole();
        Console.Error.WriteLine($"[Ultimate ZPL Viewer] Erreur : {message}");
        return 1;
    }

    private static void Info(string message)
    {
        EnsureConsole();
        Console.WriteLine($"[Ultimate ZPL Viewer] {message}");
    }

    // A WinExe (GUI-subsystem) process has no console; attach to the parent's so
    // status/error text is visible when launched from a terminal.
    private static bool _consoleReady;
    private static void EnsureConsole()
    {
        if (_consoleReady) return;
        _consoleReady = true;
        try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
