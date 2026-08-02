using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using WinRT;

namespace Ultimate_ZPL_Viewer
{
    public sealed partial class MainWindow : Window
    {
        private DesktopAcrylicController? _acrylicController;
        private SystemBackdropConfiguration? _backdropConfig;

        /// <summary>The page hosting this window's documents.</summary>
        public PreviewPage? Page => rootFrame.Content as PreviewPage;

        public MainWindow(LaunchOptions launchOptions, DocTab? adopt = null)
        {
            InitializeComponent();
            WindowManager.Register(this);
            if (adopt is not null) launchOptions = launchOptions with { Adopt = adopt };

            // Windows 11-style custom title bar: content extends into the frame,
            // the 40 px bar is our XAML, the caption buttons stay system-drawn.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragRegion);

            // The title bar follows the app theme (settings), not the OS theme.
            WindowRoot.RequestedTheme = AppSettings.Load().ToElementTheme();
            WindowRoot.ActualThemeChanged += (_, _) =>
            {
                UpdateCaptionButtonColors();
                UpdateBackdrop();
                UpdateTitleBarBackground();
            };
            UpdateCaptionButtonColors();

            AppTitleBar.Loaded      += (_, _) => UpdateCaptionSpacer();
            AppTitleBar.SizeChanged += (_, _) => UpdateCaptionSpacer();

            TrySetAcrylicBackdrop();
            EnforceMinimumSize();

            // Window / Alt-Tab / taskbar icon (unpackaged: set explicitly from the
            // .ico shipped next to the exe).
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch { /* icon is cosmetic; never block startup */ }

            // Title-bar icon (both the normal and the fullscreen bars). A relative
            // "Assets/..." XAML source resolves via ms-appx, which is unreliable
            // unpackaged — load the PNG from the output folder by file path instead.
            try
            {
                var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                if (System.IO.File.Exists(pngPath))
                {
                    var src = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(pngPath));
                    TitleBarIcon.Source = src;
                    FullScreenIcon.Source = src;
                }
            }
            catch { /* icon is cosmetic; never block startup */ }

            AppWindow.Closing += OnAppWindowClosing;

            LocalizeTitleBar();
            rootFrame.Navigate(typeof(PreviewPage), launchOptions);
        }

        // Localizes the title-bar tooltips (titlebar.* keys). Called at startup and
        // again after a live language change. The window title text itself is set
        // by SetDocumentTitle / EnterSettingsMode using the localized suffixes.
        public void LocalizeTitleBar()
        {
            string T(string k) => LocalizationService.Get("titlebar." + k);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(TitleBarBackButton, T("tooltipBack"));
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SettingsTitleButton, T("tooltipSettings"));
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(FullScreenButton, T("tooltipFullscreen"));
            // The toolbar-toggle tooltip depends on its current state; refresh it.
            SetToolbarToggleGlyph(_lastToolbarVisible);
            if (_inSettings) AppTitleText.Text = SettingsTitle();
        }

        private bool _lastToolbarVisible = true;
        private static string SettingsTitle() =>
            "Ultimate ZPL Viewer - " + LocalizationService.Get("titlebar.settings");

        // Intercepts the window close: the page resolves unsaved documents
        // (save / discard / cancel) and persists the session before we let go.
        private bool _closeApproved;
        private bool _closeFlowRunning;

        private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
        {
            if (_closeApproved) return;
            e.Cancel = true;
            if (_closeFlowRunning) return; // a dialog is already up
            _closeFlowRunning = true;
            try
            {
                if (rootFrame.Content is not PreviewPage page || await page.PrepareAppCloseAsync())
                {
                    _closeApproved = true;
                    Close();
                }
            }
            finally
            {
                _closeFlowRunning = false;
            }
        }

        /// <summary>
        /// Closes without the unsaved-documents question: the documents are not being
        /// discarded, they have just moved to another window.
        /// </summary>
        public void CloseWithoutPrompt()
        {
            _closeApproved = true;
            Close();
        }

        // ── Merging a lone document by dragging the title bar ────────────────
        //
        // A window showing a single document has no tab strip, so there is no tab to
        // drag: the user grabs the title bar instead. We watch the end of the window
        // move and, if the pointer landed on another window's tab area, hand the
        // document over and disappear — the same result as dragging a tab across.

        private void TryMergeIntoWindowUnderCursor()
        {
            if (Page?.SingleDocument() is not { } single) return;   // several tabs → drag the tab
            if (!GetCursorPos(out var cursor)) return;

            var target = WindowManager.AtScreenPoint(cursor.X, cursor.Y, ignore: this);
            if (target?.Page is not { } targetPage) return;
            if (!targetPage.IsOverTabDropZone(cursor.X, cursor.Y)) return;

            var carried = Page.GiveAwayTab(single.Item, single.Tab);
            targetPage.TakeOverDocument(carried);
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);

        /// <summary>Restores and raises the window (a second launch was routed here).</summary>
        public void BringToFront()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                Activate();
                SetForegroundWindow(hwnd);
            }
            catch { /* focus is best-effort */ }
        }

        private const int SW_RESTORE = 9;
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Places the window so its top-left corner sits at a screen point.</summary>
        public void MoveTo(int screenX, int screenY)
        {
            try
            {
                var size = AppWindow.Size;
                AppWindow.Move(new Windows.Graphics.PointInt32(screenX, screenY));
                AppWindow.Resize(size);
            }
            catch { /* placement is cosmetic */ }
        }

        // Called by the settings dialog so the title bar follows theme changes.
        public void SetTheme(ElementTheme theme) => WindowRoot.RequestedTheme = theme;

        // ── Document title ───────────────────────────────────────────────────

        private string _documentTitle = "Ultimate ZPL Viewer";
        private bool _inSettings;

        // Sets the base title (shown when not in the settings screen).
        public void SetDocumentTitle(string title)
        {
            _documentTitle = title;
            Title = title; // taskbar / alt-tab text
            if (!_inSettings) AppTitleText.Text = title;
        }

        // ── Settings mode (back arrow + title in the title bar) ──────────────

        private Action? _onTitleBarBack;

        public void EnterSettingsMode(Action onBack)
        {
            _inSettings = true;
            _onTitleBarBack = onBack;
            TitleBarBackButton.Visibility = Visibility.Visible;
            TitleContent.Margin = new Thickness(48, 0, 0, 0); // aligned with normal mode (settings ↔ back button swap)
            AppTitleText.Text = SettingsTitle();
            // Settings look: opaque title bar (no acrylic see-through), and the
            // settings / fullscreen buttons are pointless here — hide them.
            SettingsTitleButton.Visibility = Visibility.Collapsed;
            ToolbarToggleButton.Visibility = Visibility.Collapsed;
            FullScreenButton.Visibility = Visibility.Collapsed;
            UpdateTitleBarBackground();
        }

        public void ExitSettingsMode()
        {
            _inSettings = false;
            _onTitleBarBack = null;
            TitleBarBackButton.Visibility = Visibility.Collapsed;
            TitleContent.Margin = new Thickness(48, 0, 0, 0); // right of the settings button
            AppTitleText.Text = _documentTitle;
            SettingsTitleButton.Visibility = Visibility.Visible;
            ToolbarToggleButton.Visibility = Visibility.Visible;
            FullScreenButton.Visibility = Visibility.Visible;
            UpdateTitleBarBackground();
        }

        // In settings the title bar is opaque, matching the settings page
        // background (SolidBackgroundFillColorBase); otherwise transparent so
        // the acrylic backdrop shows through.
        private void UpdateTitleBarBackground()
        {
            if (!_inSettings) { AppTitleBar.Background = null; return; }
            bool dark = WindowRoot.ActualTheme == ElementTheme.Dark;
            AppTitleBar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20)
                : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3));
        }

        private void TitleBarBackButton_Click(object sender, RoutedEventArgs e) => _onTitleBarBack?.Invoke();

        private void SettingsTitleButton_Click(object sender, RoutedEventArgs e)
            => (rootFrame.Content as PreviewPage)?.RequestOpenSettings();

        // Toolbar show/hide toggle (persisted in the app settings by PreviewPage).
        private void ToolbarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((rootFrame.Content as PreviewPage)?.ToggleToolbar() is bool visible)
                SetToolbarToggleGlyph(visible);
        }

        public void SetToolbarToggleGlyph(bool toolbarVisible)
        {
            ToolbarToggleIcon.Glyph = toolbarVisible ? "" : ""; // chevron up / down
            _lastToolbarVisible = toolbarVisible;
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ToolbarToggleButton,
                LocalizationService.Get(toolbarVisible ? "titlebar.tooltipHideToolbar" : "titlebar.tooltipShowToolbar"));
        }

        // ── Acrylic backdrop (tuned for a clearly visible effect in both themes) ──

        private void TrySetAcrylicBackdrop()
        {
            if (!DesktopAcrylicController.IsSupported()) return;

            _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
            Activated += (_, e) =>
                _backdropConfig.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
            Closed += (_, _) =>
            {
                _acrylicController?.Dispose();
                _acrylicController = null;
            };

            _acrylicController = new DesktopAcrylicController();
            UpdateBackdrop();
            _acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        }

        private void UpdateBackdrop()
        {
            if (_acrylicController is null || _backdropConfig is null) return;

            bool dark = WindowRoot.ActualTheme == ElementTheme.Dark;
            _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

            // Lower tint/luminosity opacities than the default let more of the
            // desktop show through, so the acrylic reads clearly — including in
            // light mode where the default is nearly opaque.
            if (dark)
            {
                _acrylicController.TintColor         = Windows.UI.Color.FromArgb(255, 0x1C, 0x1C, 0x1C);
                _acrylicController.FallbackColor     = Windows.UI.Color.FromArgb(255, 0x1C, 0x1C, 0x1C);
                _acrylicController.TintOpacity       = 0.35f;
                _acrylicController.LuminosityOpacity = 0.55f;
            }
            else
            {
                _acrylicController.TintColor         = Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                _acrylicController.FallbackColor     = Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
                _acrylicController.TintOpacity       = 0.20f;
                _acrylicController.LuminosityOpacity = 0.55f;
            }
        }

        private void UpdateCaptionButtonColors()
        {
            var tb = AppWindow.TitleBar;
            bool dark = WindowRoot.ActualTheme == ElementTheme.Dark;

            tb.ButtonBackgroundColor         = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor         = dark ? Colors.White : Colors.Black;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
            tb.ButtonHoverForegroundColor    = dark ? Colors.White : Colors.Black;
            tb.ButtonHoverBackgroundColor    = dark
                ? Windows.UI.Color.FromArgb(25, 255, 255, 255)
                : Windows.UI.Color.FromArgb(15, 0, 0, 0);
            tb.ButtonPressedForegroundColor  = dark ? Colors.White : Colors.Black;
            tb.ButtonPressedBackgroundColor  = dark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(25, 0, 0, 0);
        }

        // Keeps the XAML content clear of the system caption buttons.
        private void UpdateCaptionSpacer()
        {
            var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
            CaptionSpacerColumn.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale));
        }

        // ── Fullscreen ───────────────────────────────────────────────────────
        // The title bar disappears entirely; moving the mouse to the top edge
        // reveals an overlay bar with minimize / exit fullscreen / close.

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            AppTitleBar.Visibility       = Visibility.Collapsed;
            FullScreenTopEdge.Visibility = Visibility.Visible;
        }

        private void ExitFullScreen()
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            AppTitleBar.Visibility       = Visibility.Visible;
            FullScreenTopEdge.Visibility = Visibility.Collapsed;
            FullScreenBar.Visibility     = Visibility.Collapsed;
            UpdateCaptionSpacer();
        }

        private void FullScreenTopEdge_PointerEntered(object sender, PointerRoutedEventArgs e)
            => FullScreenBar.Visibility = Visibility.Visible;

        private void FullScreenBar_PointerExited(object sender, PointerRoutedEventArgs e)
            => FullScreenBar.Visibility = Visibility.Collapsed;

        private void FsExitButton_Click(object sender, RoutedEventArgs e)
            => ExitFullScreen();

        private void FsCloseButton_Click(object sender, RoutedEventArgs e)
            => Close();

        private void FsMinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            // AppWindow has no Minimize while the fullscreen presenter is active;
            // go through Win32 directly.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_MINIMIZE);
        }

        private const int SW_MINIMIZE = 6;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // ── Minimum window size (848 × 480, i.e. 480p 16:9) ──────────────────

        private const int MinWidthDip = 848;
        private const int MinHeightDip = 480;
        private SUBCLASSPROC? _subclassProc; // kept alive to avoid GC

        private void EnforceMinimumSize()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _subclassProc = MinSizeSubclassProc;
            SetWindowSubclass(hwnd, _subclassProc, 1, IntPtr.Zero);
        }

        private IntPtr MinSizeSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
        {
            const uint WM_GETMINMAXINFO = 0x0024;
            const uint WM_EXITSIZEMOVE = 0x0232;
            // The user just finished dragging the window: a lone document dropped on
            // another window's tab area joins it (see TryMergeIntoWindowUnderCursor).
            if (uMsg == WM_EXITSIZEMOVE) TryMergeIntoWindowUnderCursor();
            if (uMsg == WM_GETMINMAXINFO)
            {
                var dpi = GetDpiForWindow(hWnd);
                var scale = dpi / 96.0;
                // MINMAXINFO.ptMinTrackSize is at byte offset 24 (x) / 28 (y).
                Marshal.WriteInt32(lParam, 24, (int)(MinWidthDip * scale));
                Marshal.WriteInt32(lParam, 28, (int)(MinHeightDip * scale));
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
