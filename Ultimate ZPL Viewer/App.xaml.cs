using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ultimate_ZPL_Viewer
{
    public partial class App : Application
    {
        private Window? _window;
        public Window? MainWindow => _window;

        private Mutex? _singleInstanceMutex;
        private FileSystemWatcher? _spoolWatcher;

        // Set when a print-capture window loads, so captured jobs can be routed to it.
        public PreviewPage? ActivePreviewPage { get; set; }

        public App()
        {
            InitializeComponent();
            // Crash diagnostics: XAML stowed exceptions (0xc000027b) hide the real
            // error; log it before the process dies.
            UnhandledException += (_, e) =>
            {
                try
                {
                    var log = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Ultimate ZPL Viewer", "crash.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    File.AppendAllText(log,
                        $"[{DateTime.Now:o}] {e.Message}\n{e.Exception}\n\n");
                }
                catch { }
            };
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // --help / -h : print the usage screen to the parent console and exit,
            // instead of opening the window.
            if (CliRunner.IsHelpRequested(Environment.GetCommandLineArgs()))
            {
                CliRunner.PrintHelp();
                Environment.Exit(0);
                return;
            }

            // Headless command-line conversion (ultimatezplviewer.exe in.zpl --pdf/--png out):
            // convert and exit without ever creating a window. Runs here so the UI
            // thread + XAML are initialised (ZPL text measurement needs them).
            if (CliRunner.Parse(Environment.GetCommandLineArgs()) is { } job)
            {
                int code = CliRunner.Run(job);
                Environment.Exit(code);
                return;
            }

            // The print-capture scheduled task relaunches the app with this flag
            // (unpackaged: a plain command-line argument, no MSIX protocol).
            bool launchedFromCapture = Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--print-capture", StringComparison.OrdinalIgnoreCase));

            // Single instance: if a job is printed while the app is already open,
            // the scheduled task relaunches us — but the running instance's watcher
            // already handles the spool file, so this launch just exits. A normal
            // launch (or a capture launch while closed) runs.
            _singleInstanceMutex = new Mutex(initiallyOwned: true, "UltimateZplViewer.SingleInstance", out bool isNew);
            if (!isNew && launchedFromCapture)
            {
                Exit();
                return;
            }

            // Register as a .zpl handler (HKCU) so the app appears in "Open with".
            FileAssociationService.EnsureRegistered();

            AccentColorService.ApplyAtStartup(this);

            // Load the active language before the window so the title bar localizes.
            LocalizationService.SetLanguage(AppSettings.Load().Language);
            _window = new MainWindow(LaunchOptions.Parse(Environment.GetCommandLineArgs()));
            _window.Activate();

            // Watch the spool folder and process anything already pending. The
            // printer install itself is offered to the user by PreviewPage.
            StartSpoolWatcher();
        }

        private void StartSpoolWatcher()
        {
            try
            {
                Directory.CreateDirectory(VirtualPrinterService.SpoolFolder);

                _spoolWatcher = new FileSystemWatcher(VirtualPrinterService.SpoolFolder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _spoolWatcher.Created += (_, _) => DispatcherEnqueue(ProcessPendingJob);
                _spoolWatcher.Changed += (_, _) => DispatcherEnqueue(ProcessPendingJob);
            }
            catch
            {
                // Spool folder unavailable (printer not installed) — nothing to watch.
            }

            // A job may have printed while the app was closed (this launch may be
            // the scheduled-task relaunch), so process what is already there.
            ProcessPendingJob();
        }

        private bool _processing;

        private void ProcessPendingJob()
        {
            if (_processing) return;
            // Do not consume the spool file until we have somewhere to route it,
            // otherwise TryReadPending would delete the job and lose it. It stays
            // on disk and is retried on the next watcher event / readiness.
            if (_window is null || ActivePreviewPage is null) return;

            _processing = true;
            try
            {
                var job = VirtualPrinterService.TryReadPending();
                if (job is null) return;

                if (job.IsZpl)
                    ActivePreviewPage.LoadCapturedZpl(job.Content);
                else
                    ActivePreviewPage.ShowUnsupportedPrintFormat();

                BringToForeground();
            }
            finally
            {
                _processing = false;
            }
        }

        private void BringToForeground()
        {
            if (_window is null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow?.Show();
            SetForegroundWindow(hwnd);
        }

        private void DispatcherEnqueue(Action action)
        {
            var queue = _window?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is not null) queue.TryEnqueue(() => action());
            else action();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
