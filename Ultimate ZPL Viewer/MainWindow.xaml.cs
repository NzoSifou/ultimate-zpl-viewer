using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
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

        /// <param name="deferPage">The page is built by <see cref="ShowPage"/> instead,
        /// once the window is on screen (the first window at launch).</param>
        public MainWindow(LaunchOptions launchOptions, DocTab? adopt = null, bool deferPage = false)
        {
            InitializeComponent();
            WindowManager.Register(this);
            if (adopt is not null) launchOptions = launchOptions with { Adopt = adopt };

            // Windows 11-style custom title bar: content extends into the frame,
            // the 40 px bar is our XAML, the caption buttons stay system-drawn.
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragRegion);

            // The title bar follows the app theme (settings), not the OS theme.
            WindowRoot.RequestedTheme = AppSettings.Current.ToElementTheme();
            WindowRoot.ActualThemeChanged += (_, _) =>
            {
                UpdateCaptionButtonColors();
                UpdateBackdrop();
                UpdateTitleBarBackground();
            };
            UpdateCaptionButtonColors();

            AppTitleBar.Loaded      += (_, _) => UpdateCaptionSpacer();
            AppTitleBar.LayoutUpdated += (_, _) => UpdateTitleBarPassthrough();
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

            InitSearchBoxes();
            LocalizeTitleBar();
            _launchOptions = launchOptions;
            if (!deferPage) ShowPage();
        }

        private LaunchOptions? _launchOptions;

        /// <summary>
        /// Builds the page. The first window of a launch shows itself before doing
        /// it: the page takes a few hundred milliseconds to build, and a window that
        /// waited for it appeared late and then black until its first frame.
        /// </summary>
        public void ShowPage()
        {
            if (_launchOptions is not { } options) return;
            _launchOptions = null;
            PerfLog.Mark("MainWindow: navigate");
            rootFrame.Navigate(typeof(PreviewPage), options);
            PerfLog.Mark("MainWindow: navigated");
        }

        // Localizes the title-bar tooltips (titlebar.* keys). Called at startup and
        // again after a live language change. The window title text itself is set
        // by SetDocumentTitle / EnterSettingsMode using the localized suffixes.
        public void LocalizeTitleBar()
        {
            string T(string k) => LocalizationService.Get("titlebar." + k);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(TitleBarBackButton, T("tooltipBack"));
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SettingsTitleButton, T("tooltipSettings"));
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(FsSettingsButton, T("tooltipSettings"));
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(FsBackButton, T("tooltipBack"));
            if (_titleSearchBox is not null) _titleSearchBox.PlaceholderText = LocalizationService.Get("settings.search.placeholder");
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
            if (!_inSettings) FsTitleText.Text = title;
        }

        // ── Settings mode (back arrow + title in the title bar) ──────────────

        private Action? _onTitleBarBack;

        public void EnterSettingsMode(Action onBack, Action<string>? onSearch = null)
        {
            _inSettings = true;
            _onTitleBarBack = onBack;
            _onSearch = onSearch;
            SetSearchText("");
            _titleSearchHost.Visibility = onSearch is null ? Visibility.Collapsed : Visibility.Visible;
            // Taller, like Windows' Settings, to hold the search box.
            SetTallTitleBar(onSearch is not null);
            TitleBarBackButton.Visibility = Visibility.Visible;
            TitleContent.Margin = new Thickness(48, 0, 0, 0); // aligned with normal mode (settings ↔ back button swap)
            AppTitleText.Text = SettingsTitle();
            FsTitleText.Text = SettingsTitle();
            // Settings look: opaque title bar (no acrylic see-through). The settings
            // button and the toolbar arrow have nothing to act on here; full screen
            // stays — the settings can be read full screen too.
            SettingsTitleButton.Visibility = Visibility.Collapsed;
            ToolbarToggleButton.Visibility = Visibility.Collapsed;
            FsSettingsButton.Visibility = Visibility.Collapsed;
            FsBackButton.Visibility = Visibility.Visible;
            FsToolbarToggleButton.Visibility = Visibility.Collapsed;
            UpdateTitleBarBackground();
            UpdateFullScreenSettingsChrome();
        }

        public void ExitSettingsMode()
        {
            _inSettings = false;
            _onTitleBarBack = null;
            _onSearch = null;
            SetSearchText("");
            _titleSearchHost.Visibility = Visibility.Collapsed;
            SetTallTitleBar(false);
            TitleBarBackButton.Visibility = Visibility.Collapsed;
            TitleContent.Margin = new Thickness(48, 0, 0, 0); // right of the settings button
            AppTitleText.Text = _documentTitle;
            FsTitleText.Text = _documentTitle;
            SettingsTitleButton.Visibility = Visibility.Visible;
            ToolbarToggleButton.Visibility = _inHome ? Visibility.Collapsed : Visibility.Visible;
            FullScreenButton.Visibility = Visibility.Visible;
            FsSettingsButton.Visibility = Visibility.Visible;
            FsBackButton.Visibility = Visibility.Collapsed;
            FsToolbarToggleButton.Visibility = ToolbarToggleButton.Visibility;
            UpdateTitleBarBackground();
            UpdateFullScreenSettingsChrome();
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) FullScreenBar.Visibility = Visibility.Collapsed;
        }

        // ── First-run wizard ─────────────────────────────────────────────────
        // The wizard covers the page, so the title bar must not offer doors into an
        // application the user has not been let into yet: settings, the toolbar
        // toggle and fullscreen all go, leaving the name and the caption buttons.

        public void EnterOnboardingMode()
        {
            SettingsTitleButton.Visibility = Visibility.Collapsed;
            ToolbarToggleButton.Visibility = Visibility.Collapsed;
            FullScreenButton.Visibility = Visibility.Collapsed;
            FsSettingsButton.Visibility = Visibility.Collapsed;
            FsToolbarToggleButton.Visibility = Visibility.Collapsed;
            AppTitleText.Text = "Ultimate ZPL Viewer";
        }

        public void ExitOnboardingMode()
        {
            if (_inSettings) return;   // the wizard handed over to the settings screen
            SettingsTitleButton.Visibility = Visibility.Visible;
            ToolbarToggleButton.Visibility = _inHome ? Visibility.Collapsed : Visibility.Visible;
            FullScreenButton.Visibility = Visibility.Visible;
            FsSettingsButton.Visibility = Visibility.Visible;
            FsToolbarToggleButton.Visibility = ToolbarToggleButton.Visibility;
            AppTitleText.Text = _documentTitle;
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

        // ── What the title bar lets through ──────────────────────────────────
        // The bar is the window's caption: the system takes the pointer there to
        // drag the window, and a text box in it never got a keystroke. Its
        // interactive parts are declared as passthrough regions instead.

        private string? _passthroughShown;

        private void UpdateTitleBarPassthrough()
        {
            if (AppTitleBar.XamlRoot is null) return;
            double scale = AppTitleBar.XamlRoot.RasterizationScale;
            var rects = new System.Collections.Generic.List<Windows.Graphics.RectInt32>();
            foreach (FrameworkElement e in new FrameworkElement[]
                     { TitleBarBackButton, SettingsTitleButton, ToolbarToggleButton, FullScreenButton, _titleSearchHost })
            {
                if (e.Visibility != Visibility.Visible || e.ActualWidth <= 0) continue;
                var o = e.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                rects.Add(new Windows.Graphics.RectInt32(
                    (int)Math.Round(o.X * scale), (int)Math.Round(o.Y * scale),
                    (int)Math.Round(e.ActualWidth * scale), (int)Math.Round(e.ActualHeight * scale)));
            }
            var signature = string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));
            if (signature == _passthroughShown) return;
            _passthroughShown = signature;
            try
            {
                Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                    .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, rects.ToArray());
            }
            catch { /* older systems: the bar keeps its default behaviour */ }
        }

        // The settings' title bar is 48 px, like Windows' Settings: every button
        // in it takes the whole height — ours, and the system's caption buttons,
        // which Windows draws tall on request.
        private void SetTallTitleBar(bool tall)
        {
            double h = tall ? 48 : 32;
            AppTitleBar.Height = h;
            foreach (var b in new[] { TitleBarBackButton, SettingsTitleButton, ToolbarToggleButton, FullScreenButton })
                b.Height = h;
            try
            {
                AppWindow.TitleBar.PreferredHeightOption = tall
                    ? Microsoft.UI.Windowing.TitleBarHeightOption.Tall
                    : Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
            }
            catch { /* not every system offers it */ }
            UpdateCaptionSpacer();
        }

        // ── The settings' search box ─────────────────────────────────────────
        // In the middle of the title bar, like Windows' Settings. Full screen has
        // no title bar: the page then shows its twin at the top of the categories.

        private Microsoft.UI.Xaml.Controls.Grid _titleSearchHost = null!;
        private Microsoft.UI.Xaml.Controls.TextBox _titleSearchBox = null!;
        private Action<string>? _onSearch;
        private bool _settingSearchText;

        private void InitSearchBoxes()
        {
            (_titleSearchHost, _titleSearchBox) = SettingsSearchBox.Create(480);
            _titleSearchHost.HorizontalAlignment = HorizontalAlignment.Center;
            _titleSearchHost.Visibility = Visibility.Collapsed;
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(_titleSearchHost, 3);
            AppTitleBar.Children.Add(_titleSearchHost);
            _titleSearchBox.TextChanged += (_, _) =>
            {
                if (!_settingSearchText) _onSearch?.Invoke(_titleSearchBox.Text);
            };
        }

        private void SetSearchText(string text)
        {
            _settingSearchText = true;
            _titleSearchBox.Text = text;
            _settingSearchText = false;
        }

        /// <summary>Ctrl+F in the settings.</summary>
        public void FocusSettingsSearch()
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) Page?.FocusPaneSearch();
            else _titleSearchBox.Focus(FocusState.Keyboard);
        }

        /// <summary>The page left the search (a category was chosen): the box empties.</summary>
        public void ClearSettingsSearch() => SetSearchText("");

        // Full screen: the page shows the search itself; back in a window, the title
        // bar's box takes the text over.
        private void UpdateFullScreenSettingsChrome()
        {
            bool fullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            Page?.SetFullScreenSettings(_inSettings && fullScreen);
            if (_inSettings && !fullScreen && Page is { } page) SetSearchText(page.SettingsQuery);
        }
        private void SettingsTitleButton_Click(object sender, RoutedEventArgs e)
            => (rootFrame.Content as PreviewPage)?.RequestOpenSettings();

        // Toolbar show/hide toggle (persisted in the app settings by PreviewPage).
        private void ToolbarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if ((rootFrame.Content as PreviewPage)?.ToggleToolbar() is bool visible)
                SetToolbarToggleGlyph(visible);
        }

        // The home page has no toolbar and no document, so the button that shows
        // and hides the toolbar has nothing to act on: it goes away with it and
        // comes back with the first document.
        private bool _inHome;

        public void SetHomeMode(bool on)
        {
            _inHome = on;
            ToolbarToggleButton.Visibility = on || _inSettings ? Visibility.Collapsed : Visibility.Visible;
            FsToolbarToggleButton.Visibility = ToolbarToggleButton.Visibility;
        }

        public void SetToolbarToggleGlyph(bool toolbarVisible)
        {
            var glyph = toolbarVisible ? "" : ""; // chevron up / down
            ToolbarToggleIcon.Glyph = glyph;
            FsToolbarToggleIcon.Glyph = glyph;
            _lastToolbarVisible = toolbarVisible;
            // The tooltip names what the arrow acts on (a setting).
            var what = AppSettings.Current.ChromeToggleTarget switch { "tabs" => "Tabs", "both" => "Both", _ => "Toolbar" };
            var tip = LocalizationService.Get((toolbarVisible ? "titlebar.tooltipHide" : "titlebar.tooltipShow") + what);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ToolbarToggleButton, tip);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(FsToolbarToggleButton, tip);
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
            => EnterFullScreen();

        private void EnterFullScreen()
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            AppTitleBar.Visibility       = Visibility.Collapsed;
            FullScreenTopEdge.Visibility = Visibility.Visible;
            UpdateFullScreenSettingsChrome();
        }

        /// <summary>F11: same button, both ways.</summary>
        public void ToggleFullScreen()
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen) ExitFullScreen();
            else EnterFullScreen();
        }

        private void ExitFullScreen()
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            AppTitleBar.Visibility       = Visibility.Visible;
            FullScreenTopEdge.Visibility = Visibility.Collapsed;
            FullScreenBar.Visibility     = Visibility.Collapsed;
            UpdateCaptionSpacer();
            UpdateFullScreenSettingsChrome();
        }

        private void FullScreenTopEdge_PointerEntered(object sender, PointerRoutedEventArgs e)
            => FullScreenBar.Visibility = Visibility.Visible;

        private void FullScreenBar_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            FullScreenBar.Visibility = Visibility.Collapsed;
        }

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
