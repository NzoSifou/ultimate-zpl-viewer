using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using System.Text.Json;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Ultimate_ZPL_Viewer;

public sealed partial class PreviewPage : Page
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<DpmmOption> _densityOptions = new();
    // Maximum physical size of a label dimension (50 cm). Enforced in millimetres so
    // it is density-independent — see the clamp in RefreshPreview.
    private const double MaxDimensionMm = 500.0;
    private ZplRenderModel _model = new();
    // Runtime editor/toolbar visibility. Initialised from settings, or forced by a
    // --hide launch flag. While _suppressLayoutPersist is set (a --hide launch),
    // toggles apply live but are NOT written back to the saved settings.
    private bool _editorVisible = true;
    private bool _toolbarVisible = true;
    private bool _suppressLayoutPersist;
    private bool _updating;
    private double _rotationDegrees;
    private string _currentText = "";
    private bool _editorReady;
    private string _editorLang = "fr"; // Monaco UI language currently loaded
    private string? _currentFilePath;  // null = never saved
    private bool _isDirty;             // unsaved changes
    private DispatcherTimer? _highlightTimer;
    private bool _isPanning;
    private Windows.Foundation.Point _panStart;
    private double _panStartH;
    private double _panStartV;

    public PreviewPage()
    {
        InitializeComponent();
        // Route captured print jobs (Ultimate ZPL Viewer printer) to this page.
        if (Application.Current is App app) app.ActivePreviewPage = this;
        ZplColorSchemeService.EnsureUserConfig();
        LocalizationService.SetLanguage(_settings.Language); // load languages/{code}.json
        // React live to language files being added/edited/removed on disk.
        LocalizationService.StartWatching();
        LocalizationService.LanguagesChanged += OnLanguagesChanged;
        Unloaded += (_, _) => LocalizationService.LanguagesChanged -= OnLanguagesChanged;
        Root.RequestedTheme = _settings.ToElementTheme();
        ApplyPreviewTheme(); // preview background (may differ from the chrome)
        LoadDensityOptions(_settings.DefaultDpmm);
        LoadPrinters();
        ApplyToolbarStrings(); // localize the toolbar button labels/tooltips
        ApplyPreviewCaptionVisibility();
        RebuildToolbar(); // place the toolbar groups per the saved layout
        PreviewScrollViewer.PointerWheelChanged += PreviewScrollViewer_PointerWheelChanged;
        RotateSplitButton.Click += RotateButton_Click;
        PreviewScrollViewer.PointerPressed      += PreviewScrollViewer_PointerPressed;
        PreviewScrollViewer.PointerMoved        += PreviewScrollViewer_PointerMoved;
        PreviewScrollViewer.PointerReleased     += PreviewScrollViewer_PointerReleased;
        PreviewScrollViewer.PointerCaptureLost  += PreviewScrollViewer_PointerCaptureLost;
        // Refit whenever the preview viewport itself changes size: splitter drag,
        // window resize, and the initial layout pass at startup.
        PreviewScrollViewer.SizeChanged += (_, _) => { ApplyDefaultZoom(); DrawRulers(); };
        PreviewScrollViewer.ViewChanged += (_, e) =>
        {
            // Settled view: this is the only place that knows the zoom the
            // ScrollViewer actually landed on (a requested factor can drift,
            // and pinch/native zoom never goes through our own code).
            if (!e.IsIntermediate) CaptureSettledZoom();
            UpdatePreviewCaption();
            DrawRulers();
        };
        RulerTopCanvas.SizeChanged  += (_, _) => DrawRulers();
        RulerLeftCanvas.SizeChanged += (_, _) => DrawRulers();
        // Toolbar zoom spin control [loupe− | 100 % | loupe+]: ±10 % snapped to the
        // tens, press-and-hold auto-repeats with acceleration, manual entry
        // committed on Enter / focus loss, clamped 20 %..max zoomable.
        AttachZoomRepeat(ZoomMinusButton, -1);
        AttachZoomRepeat(ZoomPlusButton, +1);
        ZoomValueBox.KeyDown += (s, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                CommitZoomBox();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Up)   { StepZoom(+1); e.Handled = true; }
            else if (e.Key == VirtualKey.Down) { StepZoom(-1); e.Handled = true; }
        };
        ZoomValueBox.LostFocus += (_, _) => CommitZoomBox();
        ZoomValueBox.GotFocus  += (_, _) => ZoomValueBox.SelectAll();
        // Deferred: clicking the button steals focus from the zoom box, whose
        // LostFocus commit issues a ChangeView first — a second ChangeView in the
        // same tick is swallowed by the ScrollViewer, so run it on the next one.
        ZoomResetButton.Click += (_, _) =>
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => ApplyZoomPercent(100));
        PreviewSurface.SizeChanged += (_, _) => DrawPreviewGrid();
        ApplyRulerVisibility();
        // Keep Monaco's theme and the accent brushes in sync (covers explicit
        // changes and system theme flips).
        // Both theme variants of the accent brushes are pre-installed, so a theme
        // change needs no accent work — only Monaco has to be notified.
        Root.ActualThemeChanged += (_, _) =>
        {
            if (_editorReady) ApplyEditorTheme();
            ApplyPreviewTheme(); // background + grid + rulers + caption
        };
        // Startup accent is applied in the App constructor (before any control
        // resolves the colors) — nothing to do here.
        HideDeleteButton(WidthBox);
        HideDeleteButton(HeightBox);
        // Rebuild the recent-files dropdown each time it opens.
        RecentFilesFlyout.Opening += (_, _) => PopulateRecentFilesMenu(RecentFilesFlyout);
        // Editor/toolbar visibility is initialised in OnNavigatedTo (it may be
        // overridden by a --hide launch flag), then applied + synced there.
        // Normalise the display once the user leaves the field ("10,50" → "10.5"):
        // while it has focus UpdateSizeBoxes leaves the text alone (decimals fix).
        WidthBox.LostFocus  += (_, _) => UpdateSizeBoxes(fillEmptyBoxes: false);
        HeightBox.LostFocus += (_, _) => UpdateSizeBoxes(fillEmptyBoxes: false);
        // Ctrl+S also works when the editor doesn't have focus (Monaco handles it
        // when it does, via its own command).
        var saveAccel = new KeyboardAccelerator { Key = VirtualKey.S, Modifiers = VirtualKeyModifiers.Control };
        saveAccel.Invoked += (s, args) =>
        {
            args.Handled = true;
            if (SettingsOverlay.Visibility == Visibility.Visible) return; // no save from settings
            _ = SaveAsync();
        };
        Root.KeyboardAccelerators.Add(saveAccel);
        // No automatic "Ctrl+S" tooltip on hover (it even leaked into the settings).
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        // Cursor set per state in ApplyEditorLayout (resize when shown, hand when hidden).
        EditorSplitter.PointerPressed     += EditorSplitter_PointerPressed;
        EditorSplitter.PointerMoved       += EditorSplitter_PointerMoved;
        EditorSplitter.PointerReleased    += EditorSplitter_PointerReleased;
        EditorSplitter.PointerCaptureLost += (_, _) => _isResizingEditor = false;
        ErrorPanelSplitter.SetCursor(Microsoft.UI.Input.InputSystemCursor.Create(
            Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth));
        ErrorPanelSplitter.PointerPressed     += ErrorPanelSplitter_PointerPressed;
        ErrorPanelSplitter.PointerMoved       += ErrorPanelSplitter_PointerMoved;
        ErrorPanelSplitter.PointerReleased    += ErrorPanelSplitter_PointerReleased;
        ErrorPanelSplitter.PointerCaptureLost += (_, _) => _isResizingErrorPanel = false;
        DocPanelSplitter.SetCursor(Microsoft.UI.Input.InputSystemCursor.Create(
            Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth));
        DocPanelSplitter.PointerPressed     += DocPanelSplitter_PointerPressed;
        DocPanelSplitter.PointerMoved       += DocPanelSplitter_PointerMoved;
        DocPanelSplitter.PointerReleased    += DocPanelSplitter_PointerReleased;
        DocPanelSplitter.PointerCaptureLost += (_, _) => _isResizingDocPanel = false;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;

        // OnNavigatedTo runs while the window is still being constructed, so the
        // title set there is lost — re-apply it now that the window exists.
        UpdateDocumentTitle();

        // The user color-scheme JSON must match the bundled schema. If not, the
        // only choices are to quit or open the file to fix it (the app exits
        // either way, then reloads a corrected file on the next launch).
        if (!await ValidateColorSchemeOrExitAsync()) return;

        // Initialise WebView2 and navigate to the Monaco editor page.
        // Transparent default background: the browser otherwise paints an opaque
        // square surface that shows as dark corners behind the rounded clip.
        // Transparent browser background, or the default dark canvas (#202020)
        // shows as square corners behind the page's rounded clip. The env var is
        // the reliable channel (AARRGGBB, read by the WebView2 loader at creation);
        // the property alone is ignored by this WinAppSDK version.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
        // Keep WebView2's user-data folder OUT of the install directory. By default
        // it lands next to the exe (<exe>.WebView2\), which breaks a read-only
        // Program Files install and litters the folder so uninstall can't remove it.
        // An explicit environment pointed at a per-user writable location is the
        // reliable channel (the env var is read too late, once the default folder
        // has already been created).
        var wvUserData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ultimate ZPL Viewer", "WebView2");
        Directory.CreateDirectory(wvUserData);
        var wvEnv = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(
            null, wvUserData, new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions());
        await EditorWebView.EnsureCoreWebView2Async(wvEnv);
        EditorWebView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
        EditorWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "zpl-editor.local", assetsPath, CoreWebView2HostResourceAccessKind.Allow);
        EditorWebView.CoreWebView2.WebMessageReceived += EditorWebView_WebMessageReceived;
        _editorLang = _settings.Language == "en" ? "en" : "fr";
        EditorWebView.Source = new Uri($"https://zpl-editor.local/editor.html?lang={_editorLang}");

        // Editor corner rounding happens inside the page (editor.html clip-path):
        // neither a XAML CornerRadius nor a composition clip affects the browser
        // output of WebView2, as verified empirically.

        // Real-size scaling: compute for the current monitor, and recompute when
        // the window moves to another monitor or its scale changes.
        UpdateRealSizeScaleAsync(preserve: false);
        if (XamlRoot is not null)
            XamlRoot.Changed += (_, _) => UpdateRealSizeScaleAsync(preserve: true);
        if (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) is MainWindow mw)
            mw.AppWindow.Changed += (_, args) => { if (args.DidPositionChange) UpdateRealSizeScaleAsync(preserve: true); };

        var missing = FontService.GetMissingFonts();
        if (missing.Count > 0)
            await ShowMissingFontsDialogAsync(missing);

        await MaybePromptPrinterInstallAsync();
        await MaybePromptZplAssociationAsync();
        await MaybePromptScreenSizeAsync();
    }

    // Offers, once, to make Ultimate ZPL Viewer the default app for .zpl files.
    // Skipped if already default or if the user ticked "don't ask again".
    private async Task MaybePromptZplAssociationAsync()
    {
        if (!_settings.AskZplAssociation) return;
        if (FileAssociationService.IsDefault()) return;

        var body = new StackPanel { Spacing = 10, MinWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = SL("general.zplPrompt.body"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = SL("general.zplPrompt.desc"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });
        var dontAsk = new CheckBox { Content = SL("general.zplPrompt.dontAsk"), Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(dontAsk);

        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            RequestedTheme    = _settings.ToElementTheme(),
            Title             = SL("general.zplPrompt.title"),
            Content           = body,
            PrimaryButtonText = SL("general.zplPrompt.setDefault"),
            CloseButtonText   = SL("general.zplPrompt.no"),
            DefaultButton     = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            FileAssociationService.SetAsDefault();
        if (dontAsk.IsChecked == true)
        {
            _settings.AskZplAssociation = false;
            _settings.Save();
        }
    }

    // Validates the user color-scheme JSON against the schema. On success returns
    // true. On failure shows a blocking error dialog with two choices — open the
    // JSON (in the default editor) or quit — and exits the app, returning false.
    private async Task<bool> ValidateColorSchemeOrExitAsync()
    {
        var error = ZplColorSchemeService.ValidateUserConfig();
        if (error is null) return true;

        var body = new TextBlock
        {
            Text = "Le fichier de configuration des couleurs ne respecte pas le schéma attendu :\n\n" +
                   error +
                   "\n\nCorrigez le fichier puis relancez l'application.",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title = "Schéma de couleurs invalide",
            Content = new ScrollViewer { MaxHeight = 340, Content = body },
            PrimaryButtonText = "Ouvrir le JSON et quitter l'application",
            CloseButtonText = "Quitter l'application",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            OpenUserColorSchemeFile();

        Application.Current.Exit();
        return false;
    }

    private static void OpenUserColorSchemeFile()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                ZplColorSchemeService.UserConfigPath) { UseShellExecute = true });
        }
        catch { /* no handler for .json / launch blocked — nothing else to do */ }
    }

    // Offers to configure the screen size when it couldn't be read from the EDID,
    // unless already dismissed or the monitor is already configured.
    private async Task MaybePromptScreenSizeAsync()
    {
        if (_settings.ScreenSizePromptDismissed) return;

        var mon = await DisplayMetrics.GetCurrentMonitorAsync(GetWindowHandle());
        if (mon is null) return;
        if (mon.EdidDiagonalInches is not null) return;                        // auto-detected
        if (_settings.ManualScreenSizesInches.ContainsKey(mon.InterfaceId)) return; // already set

        var monitors = await DisplayMetrics.EnumerateMonitorsAsync();
        bool multi = monitors.Count > 1;

        var message = multi
            ? $"Nous n'avons pas pu déterminer la taille de l'écran « {mon.FriendlyName} » afin de rendre " +
              "l'aperçu du document ZPL aux dimensions réelles.\n" +
              "Si vous connaissez la taille de cet écran (en pouces ou en centimètres), vous pouvez la configurer dans les paramètres."
            : "Nous n'avons pas pu déterminer la taille de votre écran afin de rendre l'aperçu du document ZPL " +
              "aux dimensions réelles.\n" +
              "Si vous connaissez la taille de votre écran (en pouces ou en centimètres), vous pouvez la configurer dans les paramètres.";

        var body = new StackPanel { Spacing = 12, MinWidth = 440 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var dontShow = new CheckBox { Content = "Ne plus afficher" };
        body.Children.Add(dontShow);

        var dialog = new ContentDialog
        {
            XamlRoot            = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title               = "Configuration manuelle requise",
            Content             = body,
            PrimaryButtonText   = "Aller aux paramètres",
            CloseButtonText     = "Pas maintenant",
            DefaultButton       = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();

        if (dontShow.IsChecked == true)
        {
            _settings.ScreenSizePromptDismissed = true;
            _settings.Save();
        }

        if (result == ContentDialogResult.Primary)
            OpenSettings("screen");
    }

    // Offers to install the "Ultimate ZPL Viewer" virtual printer when it is
    // missing, unless the user asked not to be prompted again.
    private async Task MaybePromptPrinterInstallAsync()
    {
        if (_settings.SkipPrinterInstallPrompt) return;
        if (VirtualPrinterService.IsInstalled()) return;

        var body = new StackPanel { Spacing = 10, MinWidth = 440 };
        body.Children.Add(new TextBlock
        {
            Text = "Ultimate ZPL Viewer peut créer une imprimante virtuelle du même nom.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = "Lorsque vous imprimez un fichier ZPL sur cette imprimante virtuelle, " +
                   "l'application s'ouvre automatiquement pour afficher un aperçu du document avant son impression.\n" +
                   "Vous pouvez ensuite l'imprimer sur une imprimante Zebra directement depuis l'application.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        });
        body.Children.Add(new TextBlock
        {
            Text = "L'installation nécessite une autorisation administrateur (une seule fois).",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
        });
        var dontAsk = new CheckBox { Content = "Ne plus me demander", Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(dontAsk);

        var dialog = new ContentDialog
        {
            XamlRoot            = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title               = "Installer l'imprimante virtuelle « Ultimate ZPL Viewer » ?",
            Content             = body,
            PrimaryButtonText   = "Installer l'imprimante",
            CloseButtonText     = "Annuler",
            DefaultButton       = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();

        if (result != ContentDialogResult.Primary)
        {
            // "Ne pas installer": only stop prompting if the checkbox is ticked.
            if (dontAsk.IsChecked == true)
            {
                _settings.SkipPrinterInstallPrompt = true;
                _settings.Save();
            }
            return;
        }

        // Install (elevated, one UAC prompt) off the UI thread, then report.
        var install = await Task.Run(VirtualPrinterService.EnsureInstalled);
        if (install.Ok)
            await ShowMessageAsync("Imprimante installée",
                "L'imprimante virtuelle « Ultimate ZPL Viewer » est prête. Vous pouvez désormais imprimer des fichier ZPL sur cette imprimante pour les visualiser dans cette application.");
        else
            await ShowMessageAsync("Échec de l'installation",
                $"L'imprimante n'a pas pu être installée.\n\n{install.Error}");
    }

    private async Task ShowMissingFontsDialogAsync(List<FontInfo> missing)
    {
        var autoInstallable = missing.Where(f => f.CanAutoInstall).ToList();
        var commercial      = missing.Where(f => !f.CanAutoInstall).ToList();

        // ── Intro ─────────────────────────────────────────────────────────
        var listPanel = new StackPanel { Spacing = 4, MinWidth = 420 };
        listPanel.Children.Add(new TextBlock
        {
            Text = "Cette application requiert les polices suivantes pour un rendu ZPL fidèle.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section 1 : polices installables automatiquement ──────────────
        if (autoInstallable.Count > 0)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = "Polices installables automatiquement :",
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.75,
            });
            foreach (var f in autoInstallable)
                listPanel.Children.Add(new TextBlock
                {
                    Text = $"  • {f.DisplayName}",
                    Margin = new Thickness(0, 2, 0, 0),
                });
        }

        // ── Section 2 : polices commerciales ──────────────────────────────
        if (commercial.Count > 0)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = "Polices commerciales (installation manuelle requise) :",
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.75,
                Margin = new Thickness(0, autoInstallable.Count > 0 ? 10 : 0, 0, 0),
            });
            foreach (var f in commercial)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = $"  • {f.DisplayName}",
                    Margin = new Thickness(0, 2, 0, 0),
                });
                listPanel.Children.Add(new TextBlock
                {
                    Text = "    Achetez et installez cette police manuellement, puis relancez l'application.",
                    Opacity = 0.6,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        // ── Progress controls (hidden initially) ──────────────────────────
        var progressBar = new ProgressBar
        {
            Minimum = 0, Maximum = 1, IsIndeterminate = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 12, 0, 4),
        };
        var progressStatus = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(listPanel);
        content.Children.Add(progressBar);
        content.Children.Add(progressStatus);

        // ── Button label ──────────────────────────────────────────────────
        var primaryText = autoInstallable.Count == 0
            ? string.Empty
            : commercial.Count == 0
                ? "Installer les polices"
                : $"Installer {autoInstallable.Count} police{(autoInstallable.Count > 1 ? "s" : "")}";

        var dialog = new ContentDialog
        {
            XamlRoot            = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title               = "Polices requises manquantes",
            Content             = content,
            PrimaryButtonText   = primaryText,
            SecondaryButtonText = "Quitter",
            DefaultButton       = autoInstallable.Count > 0
                                    ? ContentDialogButton.Primary
                                    : ContentDialogButton.None,
        };

        IReadOnlyList<FontInstallResult> installResults = Array.Empty<FontInstallResult>();
        var cts = new CancellationTokenSource();

        if (autoInstallable.Count > 0)
        {
            dialog.PrimaryButtonClick += async (d, args) =>
            {
                var deferral = args.GetDeferral();
                args.Cancel = true; // Keep dialog open while installing.
                try
                {
                    d.IsPrimaryButtonEnabled   = false;
                    d.IsSecondaryButtonEnabled = false;
                    d.Title                    = "Installation en cours…";
                    listPanel.Visibility       = Visibility.Collapsed;
                    progressBar.Visibility     = Visibility.Visible;
                    progressStatus.Visibility  = Visibility.Visible;

                    var prog = new Progress<(double Value, string Status)>(r =>
                    {
                        progressBar.Value   = r.Value;
                        progressStatus.Text = r.Status;
                    });

                    installResults = await FontService.InstallAsync(autoInstallable, prog, cts.Token);
                    args.Cancel = false; // Allow the dialog to close → ShowAsync returns Primary.
                }
                catch (OperationCanceledException) { args.Cancel = false; }
                catch                              { args.Cancel = false; }
                finally                            { deferral.Complete(); }
            };
        }

        var result = await dialog.ShowAsync();
        cts.Cancel();

        if (result is ContentDialogResult.Secondary or ContentDialogResult.None)
        {
            Application.Current.Exit();
            return;
        }

        // Installation attempted → show confirmation popup.
        await ShowInstallationResultDialogAsync(installResults, commercial);
    }

    private async Task ShowInstallationResultDialogAsync(
        IReadOnlyList<FontInstallResult> results,
        List<FontInfo> commercial)
    {
        var successes = results.Where(r =>  r.Success).ToList();
        var failures  = results.Where(r => !r.Success).ToList();
        var allDone   = failures.Count == 0 && commercial.Count == 0;

        var title = allDone          ? "Installation réussie"    :
                    failures.Count > 0 ? "Installation incomplète" :
                                         "Action requise";

        var content = new StackPanel { Spacing = 4, MinWidth = 420 };

        // ── Successes ─────────────────────────────────────────────────────
        if (successes.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "✔ Polices installées avec succès :",
                FontWeight = FontWeights.SemiBold,
            });
            foreach (var r in successes)
                content.Children.Add(new TextBlock
                {
                    Text = $"  • {r.DisplayName}",
                    Margin = new Thickness(0, 2, 0, 0),
                });
        }

        // ── Failures ──────────────────────────────────────────────────────
        if (failures.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "✖ Échecs :",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, successes.Count > 0 ? 10 : 0, 0, 0),
            });
            foreach (var r in failures)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"  • {r.DisplayName}",
                    Margin = new Thickness(0, 2, 0, 0),
                });
                if (r.Error is not null)
                    content.Children.Add(new TextBlock
                    {
                        Text = $"    {r.Error}",
                        Opacity = 0.6,
                        FontStyle = Windows.UI.Text.FontStyle.Italic,
                        TextWrapping = TextWrapping.Wrap,
                    });
            }
        }

        // ── Commercial ────────────────────────────────────────────────────
        if (commercial.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "⚠ Polices commerciales à installer manuellement :",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, (successes.Count > 0 || failures.Count > 0) ? 10 : 0, 0, 0),
            });
            foreach (var f in commercial)
                content.Children.Add(new TextBlock
                {
                    Text = $"  • {f.DisplayName}",
                    Margin = new Thickness(0, 2, 0, 0),
                });
        }

        // ── Footer ────────────────────────────────────────────────────────
        content.Children.Add(new TextBlock
        {
            Text = "Redémarrez l'application pour appliquer les changements.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Opacity = 0.8,
        });

        var dialog = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title             = title,
            Content           = content,
            PrimaryButtonText = "Redémarrer",
            DefaultButton     = ContentDialogButton.Primary,
        };

        await dialog.ShowAsync();

        try
        {
            AppInstance.Restart(string.Empty);
        }
        catch
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Exit();
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var options = e.Parameter as LaunchOptions ?? new LaunchOptions(null, false, false, false);
        // A --hide launch forces the panes hidden and, while it lasts, prevents the
        // visibility from being persisted on exit (it is a one-off override).
        _suppressLayoutPersist = options.Forced;
        _toolbarVisible = options.HideToolbar ? false : _settings.ToolbarVisible;
        _editorVisible  = options.HideEditor  ? false : _settings.EditorVisible;
        ApplyToolbarVisibility();
        ApplyEditorLayout();
        Loaded += (_, _) =>
            (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.SetToolbarToggleGlyph(_toolbarVisible);

        _rotationDegrees = Math.Clamp(_settings.DefaultRotation, 0, 359.99);

        string text;
        bool openedFromFile = false;
        var extraTabs = new List<(string Path, string Text)>(); // session tabs beyond the first
        if (!string.IsNullOrWhiteSpace(options.FilePath) && File.Exists(options.FilePath))
        {
            text = await File.ReadAllTextAsync(options.FilePath);
            _currentFilePath = options.FilePath;
            openedFromFile = true;
            AddRecentFile(options.FilePath);
        }
        else if (_settings.ReopenLastFile && await LoadPreviousSessionAsync(extraTabs) is { } firstDoc)
        {
            text = firstDoc.Text;
            _currentFilePath = firstDoc.Path;
            openedFromFile = true;
        }
        else
        {
            text = "^XA\n^PW812\n^LL406\n^FO40,40^GB732,326,3^FS\n^FO70,85^A0N,44,44^FDUltimate ZPL Viewer^FS\n^FO70,150^A0N,28,28^FDRendu local sans API externe^FS\n^XZ";
            _currentFilePath = null; // sample document (never saved)
        }
        // Opening a file uses the "open" default density; a new/sample document
        // keeps the "new document" default already selected at startup.
        if (openedFromFile) ApplyOpenDensity();
        // A never-saved document counts as unsaved from the start, so closing its
        // tab (or the app) asks the save question like for any other document.
        _isDirty = _currentFilePath is null;

        // Normalise to LF — Monaco always outputs LF from getValue(), so _currentText must match.
        _currentText = text.Replace("\r\n", "\n").Replace('\r', '\n');
        InitFirstTab();
        foreach (var (path, content) in extraTabs)
            DocTabs.TabItems.Add(MakeTabItem(new DocTab
            {
                FilePath = path,
                Text = content.Replace("\r\n", "\n").Replace('\r', '\n'),
            }));
        UpdateTabBar();
        UpdateDocumentTitle();
        RefreshPreview(SizeUpdate.DocumentLoaded);
        // WebView2 is not ready yet; OnEditorReady() will push _currentText to Monaco when it fires.
    }

    // Restores the documents open at the last exit: returns the first one (the
    // tab that becomes active) and fills extraTabs with the rest, or null when
    // nothing can be restored (falls back to the sample document).
    private async Task<(string Path, string Text)?> LoadPreviousSessionAsync(List<(string Path, string Text)> extraTabs)
    {
        var files = _settings.OpenFiles.Where(File.Exists).Distinct().ToList();
        if (files.Count == 0
            && !string.IsNullOrWhiteSpace(_settings.LastFilePath) && File.Exists(_settings.LastFilePath))
            files.Add(_settings.LastFilePath); // pre-tabs versions only stored this
        if (files.Count == 0) return null;

        foreach (var f in files.Skip(1))
        {
            try { extraTabs.Add((f, await File.ReadAllTextAsync(f))); }
            catch { /* unreadable → simply not restored */ }
        }
        try { return (files[0], await File.ReadAllTextAsync(files[0])); }
        catch { return null; }
    }

    // ── Customizable toolbar ─────────────────────────────────────────────────

    // Groups with several buttons (Buttons != null) are shown in the designer as
    // one card containing a mini-button per real button, with a single grip.
    private static readonly (string Id, string Label, string Glyph, (string Glyph, string Label)[]? Buttons)[] ToolbarItemDefs =
    {
        ("file", "Fichier", "", new[]
            { ("", "Nouveau fichier"), ("", "Ouvrir un fichier"), ("", "Enregistrer") }),
        ("density", "Densité", "", null),
        ("size", "Taille", "", null),
        ("rotate", "Tourner", "", null),
        ("zoom", "Zoom", "", null),
        ("download", "Téléchargement", "", new[]
            { ("", "PDF"), ("", "PNG") }),
        ("print", "Imprimer", "", null),
    };

    private FrameworkElement? GetToolbarGroup(string id) => id switch
    {
        "file"     => FileGroup,
        "density"  => DensityGroup,
        "size"     => SizeGroup,
        "rotate"   => RotateGroup,
        "zoom"     => ZoomGroup,
        "download" => DownloadGroup,
        "print"    => PrintGroup,
        _          => null,
    };

    // Removes the element from wherever it currently sits. FrameworkElement.Parent
    // can still be null right after InitializeComponent, so the known hosts are
    // also cleaned explicitly (Children.Remove is a safe no-op when absent).
    private void Detach(FrameworkElement fe)
    {
        if (fe.Parent is Panel p) { p.Children.Remove(fe); return; }
        ToolbarItemHolder.Children.Remove(fe);
        foreach (var child in ToolbarLines.Children)
            if (child is Panel zp) zp.Children.Remove(fe);
    }

    // Localizes the toolbar button labels/tooltips + size labels from the active
    // language file (toolbar.* keys). Called once at startup.
    private void ApplyToolbarStrings()
    {
        string T(string key) => LocalizationService.Get("toolbar." + key);
        NewFileText.Text  = T("newFile");
        OpenFileText.Text = T("openFile");
        SaveText.Text     = T("save");
        SaveAsMenuItem.Text = T("saveAs");
        RotateText.Text   = T("rotate");
        PdfText.Text      = T("pdf");
        PngText.Text      = T("png");
        PrintText.Text    = T("print");
        DensityLabel.Text = T("density");
        SizeLabel.Text    = T("size");
        ToolTipService.SetToolTip(ZoomMinusButton, T("zoomOut"));
        ToolTipService.SetToolTip(ZoomPlusButton,  T("zoomIn"));
        ToolTipService.SetToolTip(ZoomResetButton, T("zoomReset"));
        ToolTipService.SetToolTip(WidthWarningIcon,  T("widthTooLarge"));
        ToolTipService.SetToolTip(HeightWarningIcon, T("heightTooLarge"));
    }

    // Rebuilds the toolbar: one wrap panel per non-empty row (rows stack
    // vertically), separators between groups within a row. Each row still wraps
    // on narrow windows so no control is ever clipped.
    private void RebuildToolbar()
    {
        _settings.ToolbarRows = ToolbarItems.NormalizeRows(_settings.ToolbarRows);

        foreach (var id in ToolbarItems.AllIds)
            if (GetToolbarGroup(id) is { } g) Detach(g);
        ToolbarLines.Children.Clear();

        foreach (var row in _settings.ToolbarRows)
        {
            if (row.Count == 0) continue;
            var wrap = new ToolbarWrapPanel { HorizontalSpacing = 10, VerticalSpacing = 8 };
            bool first = true;
            foreach (var id in row)
            {
                if (GetToolbarGroup(id) is not { } g) continue;
                if (!first) wrap.Children.Add(MakeToolbarSeparator());
                wrap.Children.Add(g);
                first = false;
            }
            ToolbarLines.Children.Add(wrap);
        }
    }

    private static Microsoft.UI.Xaml.Shapes.Rectangle MakeToolbarSeparator() => new()
    {
        Width = 1, Height = 16, Opacity = 0.5,
        Margin = new Thickness(2, 0, 2, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Fill = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
    };

    public void RequestOpenSettings() => OpenSettings("general");

    // Title-bar toolbar toggle: flips and applies the visibility, persisting it
    // unless the app was launched with --hide. Returns the new state so the
    // caller can update its glyph/tooltip.
    public bool ToggleToolbar()
    {
        _toolbarVisible = !_toolbarVisible;
        PersistLayoutState();
        ApplyToolbarVisibility();
        return _toolbarVisible;
    }

    private void ApplyToolbarVisibility() =>
        ToolbarBorder.Visibility = _toolbarVisible ? Visibility.Visible : Visibility.Collapsed;

    // Editor collapse handle (the thin full-height strip): flips the editor's
    // visibility, same persistence rule as the toolbar.
    private void ToggleEditor()
    {
        _editorVisible = !_editorVisible;
        PersistLayoutState();
        ApplyEditorLayout();
    }

    // Saves the current editor/toolbar visibility — except after a --hide launch,
    // whose override must not overwrite the user's saved preference.
    private void PersistLayoutState()
    {
        if (_suppressLayoutPersist) return;
        _settings.ToolbarVisible = _toolbarVisible;
        _settings.EditorVisible = _editorVisible;
        _settings.Save();
    }


    private double _editorWidth = 420;

    // Places the editor and the preview in the left/right columns per the swap
    // setting, sizes the columns, and aligns the splitter hairline against the
    // preview. The editor's column is fixed-width (resizable); the preview fills.
    private void ApplyEditorLayout()
    {
        bool swap = _settings.SwapEditorPreview;
        // Hidden editor: its column collapses to 0 so the handle sits at the window
        // edge; the card is hidden and its margin removed so nothing shows through.
        var editorWidth = new GridLength(_editorVisible ? _editorWidth : 0);
        var star = new GridLength(1, GridUnitType.Star);

        EditorHost.Visibility = _editorVisible ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(EditorHost, swap ? 2 : 0);
        Grid.SetColumn(PreviewSurface, swap ? 0 : 2);

        if (swap)
        {
            ColRight.Width = editorWidth;
            ColLeft.Width  = star;
            EditorHost.Margin = _editorVisible ? new Thickness(0, 14, 14, 14) : new Thickness(0);
            SplitterHairline.HorizontalAlignment = HorizontalAlignment.Left;  // preview on the left
        }
        else
        {
            ColLeft.Width  = editorWidth;
            ColRight.Width = star;
            EditorHost.Margin = _editorVisible ? new Thickness(14, 14, 0, 14) : new Thickness(0);
            SplitterHairline.HorizontalAlignment = HorizontalAlignment.Right; // preview on the right
        }

        // Hairline only makes sense as a boundary when the editor is shown.
        SplitterHairline.Visibility = _editorVisible ? Visibility.Visible : Visibility.Collapsed;

        // Chevron points toward the editor to collapse it, away from it to expand.
        // left when (visible XOR swapped): editor-left+shown, or editor-right+hidden.
        bool pointLeft = _editorVisible ^ swap;
        EditorCollapseChevron.Glyph = pointLeft ? "" : ""; // ChevronLeft / ChevronRight
        ToolTipService.SetToolTip(EditorSplitter,
            _editorVisible ? "Masquer l'éditeur" : "Afficher l'éditeur");

        // Resize cursor when the editor can be resized, hand when it is just a button.
        EditorSplitter.SetCursor(Microsoft.UI.Input.InputSystemCursor.Create(
            _editorVisible ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast
                           : Microsoft.UI.Input.InputSystemCursorShape.Hand));
    }

    private ColumnDefinition EditorColumnDef => _settings.SwapEditorPreview ? ColRight : ColLeft;

    // Only the four standard Zebra densities exist — a document has no intrinsic
    // DPI, so nothing is ever auto-detected or added to this list.
    private void LoadDensityOptions(double selectedDpmm)
    {
        _densityOptions.Clear();
        foreach (var dpmm in new[] { 6d, 8d, 12d, 24d })
        {
            _densityOptions.Add(new DpmmOption(dpmm));
        }

        DensityComboBox.ItemsSource = _densityOptions;
        DensityComboBox.DisplayMemberPath = nameof(DpmmOption.Label);
        SelectDensity(selectedDpmm);
    }

    private void SelectDensity(double dpmm)
    {
        // Snap to the closest standard density.
        DensityComboBox.SelectedItem = _densityOptions
            .OrderBy(d => Math.Abs(d.Dpmm - dpmm))
            .First();
    }

    private void LoadPrinters()
    {
        PrinterComboBox.Items.Clear();
        foreach (var printer in GetInstalledPrinters())
        {
            PrinterComboBox.Items.Add(printer);
        }

        var preferred = _settings.DefaultPrinter == "last" ? _settings.LastPrinter : _settings.DefaultPrinter;
        if (!string.IsNullOrWhiteSpace(preferred) && PrinterComboBox.Items.Contains(preferred))
        {
            PrinterComboBox.SelectedItem = preferred;
        }
        else if (PrinterComboBox.Items.Count > 0)
        {
            PrinterComboBox.SelectedIndex = 0;
        }
    }

    private double SelectedDpmm => (DensityComboBox.SelectedItem as DpmmOption)?.Dpmm ?? _settings.DefaultDpmm;

    // What triggered the refresh — decides whether the document size may be updated.
    private enum SizeUpdate
    {
        DocumentLoaded,  // file open / new file / density change: apply full auto sizing
        TextEdited,      // ZPL edit: apply auto sizing only per the configured policy
        KeepCurrent,     // rotation, manual size edit…: never touch the size
    }

    private double? _lastPw;
    private double? _lastLl;

    private void RefreshPreview(SizeUpdate kind)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            var parsed = ZplRenderer.Parse(_currentText, SelectedDpmm);

            // Detect ^PW / ^LL additions, removals and value changes.
            var pw = parsed.DeclaredWidthDots;
            var ll = parsed.DeclaredHeightDots;
            bool pwChanged = pw != _lastPw;
            bool llChanged = ll != _lastLl;
            _lastPw = pw;
            _lastLl = ll;
            UpdateSizeBoxLocks();

            bool auto      = _settings.AutoDocSize;
            bool elemsMode = _settings.AutoDocSizeMode == 1;

            // Per-dimension decision: null → keep the current (manual) value.
            double? autoW = null, autoH = null;
            switch (kind)
            {
                case SizeUpdate.DocumentLoaded:
                    autoW = parsed.Size.WidthDots;
                    autoH = parsed.Size.HeightDots;
                    break;

                case SizeUpdate.TextEdited when auto:
                    // A ^PW/^LL command always wins, but only re-applies when it
                    // was just added or its value changed — ordinary edits leave
                    // the user's manual size alone. In elements mode, a dimension
                    // without a command follows the computed size on every edit.
                    if (pw.HasValue) { if (pwChanged) autoW = pw; }
                    else if (elemsMode) autoW = parsed.ContentWidthDots;
                    if (ll.HasValue) { if (llChanged) autoH = ll; }
                    else if (elemsMode) autoH = parsed.ContentHeightDots;
                    break;
            }

            // Current size, per field. An emptied field falls back to a value that
            // depends on the auto-size setting; the preview keeps using that value
            // and the field shows it as a placeholder (see UpdateSizeBoxes).
            double? typedW = UnitConverter.TryParseLength(WidthBox.Text, out var wVal)
                ? UnitConverter.ToMillimeters(wVal, _settings.Unit) * SelectedDpmm : null;
            double? typedH = UnitConverter.TryParseLength(HeightBox.Text, out var hVal)
                ? UnitConverter.ToMillimeters(hVal, _settings.Unit) * SelectedDpmm : null;

            double EmptyFallback(double? declared, double content, double previous) =>
                !_settings.AutoDocSize    ? previous               // keep the value the field had
                : _settings.AutoDocSizeMode == 0
                    ? declared ?? previous                          // ^PW/^LL, else keep
                    : declared ?? content;                          // ^PW/^LL, else elements

            double manualW = typedW ?? EmptyFallback(pw, parsed.ContentWidthDots,  _model.Size.WidthDots);
            double manualH = typedH ?? EmptyFallback(ll, parsed.ContentHeightDots, _model.Size.HeightDots);

            // The requested size (what the user asked for), then the effective size:
            // every dimension is CAPPED at 50 cm of physical size for the preview only,
            // whatever the source (^PW/^LL, computed, or typed). The size field keeps
            // the requested value (so you can type any number); only the rendered
            // document and the bottom-right caption stay clamped at 50 cm, with a warning.
            // The limit is in millimetres so it holds at any density (50 cm = 500 mm × dpmm).
            double maxDots = MaxDimensionMm * SelectedDpmm;
            double reqW = autoW ?? manualW;
            double reqH = autoH ?? manualH;
            bool widthClamped  = reqW > maxDots + 0.5;
            bool heightClamped = reqH > maxDots + 0.5;
            double finalW = widthClamped  ? maxDots : reqW;
            double finalH = heightClamped ? maxDots : reqH;
            WidthWarningIcon.Visibility  = widthClamped  ? Visibility.Visible : Visibility.Collapsed;
            HeightWarningIcon.Visibility = heightClamped ? Visibility.Visible : Visibility.Collapsed;
            _requestedSize = new LabelSize(reqW, reqH);

            _model = new ZplRenderModel
            {
                DeclaredDpmm       = parsed.DeclaredDpmm,
                DeclaredWidthDots  = parsed.DeclaredWidthDots,
                DeclaredHeightDots = parsed.DeclaredHeightDots,
                ContentWidthDots   = parsed.ContentWidthDots,
                ContentHeightDots  = parsed.ContentHeightDots,
                Size      = new LabelSize(finalW, finalH),
                Drawables = parsed.Drawables,
                InvertOrientation = parsed.InvertOrientation,
            };

            UpdateSizeBoxes(fillEmptyBoxes: kind == SizeUpdate.DocumentLoaded);
            ZplRenderer.Draw(PreviewCanvas, _model, SelectedDpmm, _rotationDegrees);

            // Re-apply the default zoom after every redraw (low priority: waits
            // for the new canvas size to be measured).
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => { ApplyDefaultZoom(); DrawRulers(); });
        }
        finally
        {
            _updating = false;
        }
    }

    // Suppresses the TextBox clear ("✕") button. WinUI exposes no property for it,
    // so the template's DeleteButton is zeroed out once the control is loaded —
    // the visual states only toggle its Visibility, so the zero width persists.
    private static void HideDeleteButton(TextBox box)
    {
        box.Loaded += (_, _) =>
        {
            if (FindDescendantByName(box, "DeleteButton") is FrameworkElement button)
            {
                button.Width = 0;
                button.MinWidth = 0;
                button.MaxWidth = 0;
                button.Margin = new Thickness(0);
                if (button is Control buttonControl) buttonControl.Padding = new Thickness(0);
                button.Opacity = 0;
                button.IsHitTestVisible = false;
            }
            // The text area keeps a right padding reserved for the button — remove it.
            if (FindDescendantByName(box, "ContentElement") is FrameworkElement content)
            {
                var p = box.Padding;
                content.Margin = new Thickness(0);
                if (content is Control cc) cc.Padding = new Thickness(p.Left, p.Top, p.Left, p.Bottom);
            }
        };
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendantByName(child, name) is { } found) return found;
        }
        return null;
    }

    // Draws the optional background grid of the preview surface (24 px squares).
    private void DrawPreviewGrid()
    {
        PreviewGridCanvas.Children.Clear();
        var width  = PreviewSurface.ActualWidth;
        var height = PreviewSurface.ActualHeight;
        // The caption carries a scale bar matching one grid cell, so it must be
        // refreshed on EVERY path here — including when the grid is switched off,
        // which is what drops the scale bar again.
        if (!_settings.ShowPreviewGrid || width <= 0 || height <= 0)
        {
            UpdatePreviewCaption();
            return;
        }

        // Custom colour if chosen, else the faint default keyed to the PREVIEW's
        // theme (dark lines on a light preview even when the app chrome is dark).
        var brush = new SolidColorBrush(EffectiveGridColor());
        // The spacing is stored in the chosen unit; convert to screen DIPs using
        // the real-size scale (mm/cm/in) or 1:1 (px). Clamp so lines never collapse.
        var step = GridStepDip;
        for (double x = step; x < width; x += step)
        {
            PreviewGridCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = height, Stroke = brush, StrokeThickness = 1,
            });
        }
        for (double y = step; y < height; y += step)
        {
            PreviewGridCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = y, X2 = width, Y2 = y, Stroke = brush, StrokeThickness = 1,
            });
        }
        // The caption carries a scale bar matching one grid cell — it has to follow
        // any spacing change and re-align on the new grid lines.
        UpdatePreviewCaption();
    }

    // ── Preview rulers (top / left) ──────────────────────────────────────────
    // Band thickness in DIPs for the three size presets (thin / normal / large).
    private double RulerBandDip => _settings.RulerBandSize switch { 0 => 16, 2 => 30, _ => 22 };

    // Applies the toggles: shows/hides each overlay ruler band + the shared corner,
    // and sizes the bands. The bands overlay the preview edges without consuming
    // layout space, so toggling them never shifts the preview or its grid. Called
    // at startup and whenever a ruler setting changes.
    private void ApplyRulerVisibility()
    {
        bool h = _settings.ShowRulerHorizontal, v = _settings.ShowRulerVertical;
        double band = RulerBandDip;
        RulerTopCanvas.Height   = band;
        RulerLeftCanvas.Width   = band;
        RulerCorner.Width = band;
        RulerCorner.Height = band;
        RulerTopCanvas.Visibility  = h ? Visibility.Visible : Visibility.Collapsed;
        RulerLeftCanvas.Visibility = v ? Visibility.Visible : Visibility.Collapsed;
        // The corner only shows where both bands cross.
        RulerCorner.Visibility = h && v ? Visibility.Visible : Visibility.Collapsed;
        DrawRulers();
    }

    // Document millimetres per one unit of the ruler ("px" handled separately).
    private static double RulerMmPerUnit(string unit) => unit switch
    {
        "cm" => 10.0, "in" => 25.4, _ => 1.0, // mm
    };

    // Redraws both ruler bands. Ticks are anchored on the label's top-left corner
    // (canvas 0,0) and scaled by the live zoom, so they always read the label's own
    // coordinates in the chosen unit. A "nice" major step keeps majors ≥ ~48 DIP
    // apart; each major is subdivided into RulerSubdivisions minor ticks.
    private void DrawRulers()
    {
        bool h = _settings.ShowRulerHorizontal, v = _settings.ShowRulerVertical;
        if (h) RulerTopCanvas.Children.Clear();
        if (v) RulerLeftCanvas.Children.Clear();
        if ((!h && !v) || _model is null) return;

        double zoom = PreviewScrollViewer.ZoomFactor;
        if (zoom <= 0) return;

        // Screen DIPs per ruler unit. px → 1 dot; physical → dpmm·mm/unit.
        string unit = _settings.RulerUnit;
        double pxPerUnit = unit == "px"
            ? zoom
            : zoom * SelectedDpmm * RulerMmPerUnit(unit);
        if (pxPerUnit <= 0.0001) return;

        // "Nice" major step (in units) so majors stay ≥ 48 DIP apart: 1,2,5,×10…
        const double MinMajorDip = 48;
        double raw = MinMajorDip / pxPerUnit;              // units needed for 48 DIP
        double majorUnits = NiceCeil(raw);
        int subs = Math.Clamp(_settings.RulerSubdivisions, 1, 20);
        double minorUnits = majorUnits / subs;
        double minorDip = minorUnits * pxPerUnit;
        if (minorDip < 3) { minorDip = majorUnits * pxPerUnit; subs = 1; } // too dense: majors only

        var tick = new SolidColorBrush(IsDarkTheme
            ? Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        var textCol = IsDarkTheme
            ? Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xC0, 0x00, 0x00, 0x00);
        double band = RulerBandDip;

        if (h)
        {
            double originX = PreviewCanvas
                .TransformToVisual(RulerTopCanvas)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            DrawRulerTicks(RulerTopCanvas, horizontal: true, RulerTopCanvas.ActualWidth,
                originX, minorDip, minorUnits, subs, band, tick, textCol);
        }
        if (v)
        {
            double originY = PreviewCanvas
                .TransformToVisual(RulerLeftCanvas)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            DrawRulerTicks(RulerLeftCanvas, horizontal: false, RulerLeftCanvas.ActualHeight,
                originY, minorDip, minorUnits, subs, band, tick, textCol);
        }
    }

    // Rounds up to the next "nice" number (1,2,5 × 10ⁿ) — classic ruler/axis steps.
    private static double NiceCeil(double x)
    {
        if (x <= 0) return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(x)));
        double f = x / pow;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nice * pow;
    }

    private void DrawRulerTicks(Canvas canvas, bool horizontal, double length, double origin,
        double minorDip, double minorUnits, int subs, double band,
        SolidColorBrush tick, Windows.UI.Color textColor)
    {
        if (length <= 0 || minorDip < 3) return;

        double majorLen = band * 0.62, minorLen = band * 0.32;
        int kMin = (int)Math.Ceiling((0 - origin) / minorDip);
        int kMax = (int)Math.Floor((length - origin) / minorDip);
        if (kMax - kMin > 4000) return; // runaway guard

        for (int k = kMin; k <= kMax; k++)
        {
            double pos = origin + k * minorDip;
            bool major = (k % subs) == 0;
            double len = major ? majorLen : minorLen;

            var line = new Microsoft.UI.Xaml.Shapes.Line { Stroke = tick, StrokeThickness = 1 };
            if (horizontal) { line.X1 = pos; line.X2 = pos; line.Y1 = band - len; line.Y2 = band; }
            else            { line.Y1 = pos; line.Y2 = pos; line.X1 = band - len; line.X2 = band; }
            canvas.Children.Add(line);

            if (!major) continue;
            double value = k * minorUnits;
            var label = new TextBlock
            {
                Text = value.ToString("0.##", CultureInfo.CurrentCulture),
                FontSize = 10,
                Foreground = new SolidColorBrush(textColor),
            };
            if (horizontal)
            {
                Canvas.SetLeft(label, pos + 2);
                Canvas.SetTop(label, 0);
            }
            else
            {
                // Rotate -90° so numbers read up the left band.
                label.RenderTransform = new RotateTransform { Angle = -90 };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, pos - 2);
            }
            canvas.Children.Add(label);
        }
    }

    // Grid pitch in screen DIPs. The spacing is stored in the chosen unit; convert
    // via the real-size scale (mm/cm/in) or 1:1 (px). Clamped so lines never collapse.
    private double GridStepDip =>
        Math.Max(4, _settings.PreviewGridSpacing * GridUnitPx(_settings.PreviewGridSpacingUnit));

    // Short symbol for the grid unit, for the caption's scale bar ("1 cm").
    private string GridUnitSymbol => _settings.PreviewGridSpacingUnit switch
    {
        "mm" => "mm", "cm" => "cm", "in" => "in", _ => "px",
    };

    // Screen DIPs per unit of grid spacing. mm/cm/in use the real-size scale
    // (_refPxPerMm = DIPs per mm at 100 %), px is 1:1.
    private double GridUnitPx(string unit) => unit switch
    {
        "mm" => _refPxPerMm,
        "cm" => 10 * _refPxPerMm,
        "in" => 25.4 * _refPxPerMm,
        _    => 1.0,
    };

    private static readonly string[] GridUnitCodes = { "px", "mm", "cm", "in" };

    private void FitPreviewToView()
    {
        var svW = PreviewScrollViewer.ViewportWidth;
        var svH = PreviewScrollViewer.ViewportHeight;
        var cvW = PreviewCanvas.Width  + 2; // +2 for 1px border on each side
        var cvH = PreviewCanvas.Height + 2;

        if (svW <= 0 || svH <= 0 || cvW <= 2 || cvH <= 2) return;

        const double margin = 24;
        var zoom = (float)Math.Max(0.01, Math.Min(
            (svW - margin) / cvW,
            (svH - margin) / cvH));

        ApplyZoomFactor(zoom);
    }

    // Effective screen pixels (DIPs) per millimetre at display-100%. Set so that
    // 100 % == real physical size: _refPxPerMm = physicalPxPerMm / rasterizationScale.
    // Falls back to 96 dpi / 25.4 (≈ real size when Windows uses recommended scaling).
    private double _refPxPerMm = 96.0 / 25.4;
    private string? _lastMonitorId;
    private double _lastScale = 1.0;

    private double DisplayZoomPercent => PreviewScrollViewer.ZoomFactor * 100.0 * SelectedDpmm / _refPxPerMm;

    // NOTE: the ScrollViewer hard-rejects zoom factors below 0.1 (setting a lower
    // MinZoomFactor crashes 0xc000027b), so the effective floor is
    // max(20 % display, factor 0.1) depending on density and monitor scale.
    private float ZoomFactorForDisplay(double percent)
        => (float)Math.Clamp(percent / 100.0 * _refPxPerMm / SelectedDpmm, 0.1, 20.0);

    // Spin-control bounds: 20 % floor, ceiling = the display % at the ScrollViewer's
    // MaxZoomFactor (the same limit Ctrl+wheel hits, e.g. 3974 % for a 203 dpi doc).
    private const double MinDisplayZoomPercent = 20.0;
    private double MaxDisplayZoomPercent
        => PreviewScrollViewer.MaxZoomFactor * 100.0 * SelectedDpmm / _refPxPerMm;

    private void ApplyZoomPercent(double percent)
    {
        percent = Math.Clamp(percent, MinDisplayZoomPercent, MaxDisplayZoomPercent);
        RememberTabZoom(percent); // the user chose it: this document keeps it
        ApplyZoomFactor(ZoomFactorForDisplay(percent));
        // Show the clamped value immediately, even while the box is focused
        // (UpdatePreviewCaption skips focused boxes to not fight the user's typing).
        ZoomValueBox.Text = $"{(int)Math.Round(percent)} %";
        if (ZoomValueBox.FocusState != FocusState.Unfocused) ZoomValueBox.SelectAll();
        UpdatePreviewCaption();
    }

    // ±1 step of 10 %, snapped to the nearest ten (104 % → 110 % / 100 %).
    private void StepZoom(int direction)
    {
        var cur = DisplayZoomPercent;
        var next = direction > 0
            ? Math.Floor(cur / 10.0 + 0.001) * 10.0 + 10.0
            : Math.Ceiling(cur / 10.0 - 0.001) * 10.0 - 10.0;
        ApplyZoomPercent(next);
    }

    // Press-and-hold on the +/- buttons: first step on press, then auto-repeat
    // after 400 ms at 150 ms/tick with a step that grows the longer the button
    // is held (10 % → up to 320 % per tick), always landing on multiples of 10.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomRepeatTimer;
    private int _zoomRepeatDir;
    private int _zoomRepeatTicks;

    private void AttachZoomRepeat(Button button, int direction)
    {
        // handledEventsToo: Button marks pointer events as handled internally.
        button.AddHandler(PointerPressedEvent,
            new PointerEventHandler((_, _) => StartZoomRepeat(direction)), true);
        button.AddHandler(PointerReleasedEvent,
            new PointerEventHandler((_, _) => StopZoomRepeat()), true);
        button.PointerExited      += (_, _) => StopZoomRepeat();
        button.PointerCaptureLost += (_, _) => StopZoomRepeat();
        // Keyboard activation (Space/Enter): single steps, no repeat.
        button.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Space or VirtualKey.Enter)
            {
                StepZoom(direction);
                e.Handled = true;
            }
        };
    }

    private void StartZoomRepeat(int direction)
    {
        StepZoom(direction);
        _zoomRepeatDir = direction;
        _zoomRepeatTicks = 0;
        if (_zoomRepeatTimer is null)
        {
            _zoomRepeatTimer = DispatcherQueue.CreateTimer();
            _zoomRepeatTimer.Tick += (_, _) =>
            {
                _zoomRepeatTimer!.Interval = TimeSpan.FromMilliseconds(150);
                _zoomRepeatTicks++;
                // 10 % per tick for ~1 s, then doubling every ~6 ticks, capped ×32.
                var step = 10.0 * Math.Min(32.0, Math.Pow(2.0, _zoomRepeatTicks / 6.0));
                var next = Math.Round((DisplayZoomPercent + _zoomRepeatDir * step) / 10.0) * 10.0;
                ApplyZoomPercent(next);
            };
        }
        _zoomRepeatTimer.Interval = TimeSpan.FromMilliseconds(400); // initial delay
        _zoomRepeatTimer.Start();
    }

    private void StopZoomRepeat() => _zoomRepeatTimer?.Stop();

    // Parses the manual entry ("150", "150 %", "150,5") and applies it.
    private void CommitZoomBox()
    {
        var raw = ZoomValueBox.Text.Replace("%", "").Replace(",", ".").Trim();
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && pct > 0)
        {
            ApplyZoomPercent(pct);
        }
        else
        {
            UpdatePreviewCaption(); // invalid input: restore the current value
        }
    }

    // Recomputes the real-size scale for the monitor the window is currently on.
    // preserve = keep the current display % (used when moving between monitors);
    // otherwise the default zoom is (re)applied.
    private MonitorInfo? _currentMonitor;
    private bool _currentMonitorNeedsManual;

    private async void UpdateRealSizeScaleAsync(bool preserve)
    {
        var hwnd = GetWindowHandle();
        if (hwnd == IntPtr.Zero) return;

        var id = DisplayMetrics.GetMonitorInterfaceId(hwnd);
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (id == _lastMonitorId && Math.Abs(scale - _lastScale) < 0.001) return; // no change
        _lastMonitorId = id;
        _lastScale = scale;

        var mon = await DisplayMetrics.GetCurrentMonitorAsync(hwnd);
        _currentMonitor = mon;

        // Prefer the EDID physical size; else a manual size the user configured;
        // else fall back and flag that this monitor needs manual configuration.
        double? pxPerMm = null;
        _currentMonitorNeedsManual = false;
        if (mon is not null)
        {
            if (mon.EdidDiagonalInches is double diag)
                pxPerMm = DisplayMetrics.PxPerMmFromDiagonal(diag, mon.ResW, mon.ResH);
            else if (_settings.ManualScreenSizesInches.TryGetValue(mon.InterfaceId, out var manualIn))
                pxPerMm = DisplayMetrics.PxPerMmFromDiagonal(manualIn, mon.ResW, mon.ResH);
            else
                _currentMonitorNeedsManual = true;
        }

        var newRef = pxPerMm is double p && p > 0 ? p / scale : 96.0 / 25.4;
        if (Math.Abs(newRef - _refPxPerMm) < 0.0001) { UpdatePreviewCaption(); return; }

        var displayBefore = DisplayZoomPercent;
        _refPxPerMm = newRef;
        if (_settings.PreviewGridSpacingUnit != "px") DrawPreviewGrid(); // physical spacing depends on the scale

        if (preserve)
            ApplyZoomFactor(ZoomFactorForDisplay(displayBefore));
        else
            ApplyDefaultZoom();
        UpdatePreviewCaption();
    }

    // Recomputes the real-size scale for the current monitor (e.g. after the user
    // enters a manual size in the settings), forcing a fresh evaluation.
    private void RefreshRealSizeScale(bool preserve = true)
    {
        _lastMonitorId = null; // force UpdateRealSizeScaleAsync to re-evaluate
        UpdateRealSizeScaleAsync(preserve);
    }

    // A one-shot zoom to apply on the next redraw (used to preserve the on-screen
    // physical size across a density change).
    private float? _pendingZoomOverride;

    // >0: redraws must leave the current zoom untouched (e.g. during a rotation).
    private int _suppressDefaultZoom;

    // The factor WE last pushed. Lets CaptureSettledZoom tell our own default/fit
    // application apart from a zoom the user performed (wheel, pinch, buttons).
    private float _selfAppliedZoomFactor = float.NaN;

    // The single gate for every programmatic zoom, so the "was it us?" test above
    // stays accurate.
    private void ApplyZoomFactor(float factor, bool disableAnimation = true)
    {
        factor = Math.Clamp(factor, PreviewScrollViewer.MinZoomFactor, PreviewScrollViewer.MaxZoomFactor);
        _selfAppliedZoomFactor = factor;
        PreviewScrollViewer.ChangeView(null, null, factor, disableAnimation);
    }

    // Records the level the user just picked as the active document's own zoom.
    // From here on it survives every relayout until the tab is closed.
    private void RememberTabZoom(double percent)
    {
        if (_activeTab is not null) _activeTab.ZoomPercent = percent;
    }

    // Stores the zoom the ScrollViewer actually settled on. Our own default/fit
    // pass must NOT pin a document that is still following the default (otherwise
    // "fit to window" would freeze at the first layout); everything else is a
    // level the user landed on and becomes the document's own.
    private void CaptureSettledZoom()
    {
        if (_activeTab is null) return;
        var z = PreviewScrollViewer.ZoomFactor;
        if (z <= 0) return;
        bool ours = !float.IsNaN(_selfAppliedZoomFactor)
                    && Math.Abs(z - _selfAppliedZoomFactor) <= 0.001f;
        if (ours && _activeTab.ZoomPercent is null) return;
        _activeTab.ZoomPercent = z * 100.0 * SelectedDpmm / _refPxPerMm;
    }

    // Restores the active document's own zoom, if it has one. Returns false when
    // the document still follows the default-zoom setting.
    private bool RestoreTabZoom()
    {
        if (_activeTab?.ZoomPercent is not double percent) return false;
        ApplyZoomFactor(ZoomFactorForDisplay(percent));
        return true;
    }

    // Applies the zoom after a redraw or a viewport change: a pending override,
    // else the document's own zoom once the user has set one, else the default
    // (fit to window at 0, or the fixed display percentage).
    private void ApplyDefaultZoom()
    {
        if (_suppressDefaultZoom > 0) return;
        if (_pendingZoomOverride is float pending)
        {
            _pendingZoomOverride = null;
            // The density changed: the override keeps the on-screen physical
            // size, and CaptureSettledZoom re-pins the document if it had a zoom.
            ApplyZoomFactor(pending);
            return;
        }
        if (RestoreTabZoom()) return;
        if (_settings.DefaultZoom <= 0)
        {
            FitPreviewToView();
            return;
        }
        ApplyZoomFactor(ZoomFactorForDisplay(_settings.DefaultZoom));
    }

    private void PreviewScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (!ctrl) return;

        e.Handled = true;
        var delta  = e.GetCurrentPoint(PreviewScrollViewer).Properties.MouseWheelDelta;
        var factor = delta > 0 ? 1.2f : 1f / 1.2f;
        // Same floor as the spin control: 20 % display, whatever the density.
        var newZoom = Math.Clamp(PreviewScrollViewer.ZoomFactor * factor,
                                 ZoomFactorForDisplay(MinDisplayZoomPercent), 20f);
        // Deliberately NOT routed through ApplyZoomFactor: this is the user
        // zooming, so CaptureSettledZoom must pin the level actually reached
        // (a requested factor drifts while the animation is still running).
        PreviewScrollViewer.ChangeView(null, null, newZoom);
    }

    // Open hand when the document overflows the preview viewport (pannable),
    // closed hand while actually panning, default arrow otherwise. The hand
    // cursors are custom Win32 cursor resources (ZplCursors.dll) since Windows
    // has no open/closed-hand system cursors; system cursors are the fallback
    // if the DLL cannot be loaded.
    private static readonly Microsoft.UI.Input.InputCursor PanHandCursor =
        CreatePanCursor(100, Microsoft.UI.Input.InputSystemCursorShape.Hand);
    private static readonly Microsoft.UI.Input.InputCursor PanGrabCursor =
        CreatePanCursor(101, Microsoft.UI.Input.InputSystemCursorShape.SizeAll);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string fileName);

    private static Microsoft.UI.Input.InputCursor CreatePanCursor(
        uint resourceId, Microsoft.UI.Input.InputSystemCursorShape fallback)
    {
        try
        {
            LoadLibraryW(Path.Combine(AppContext.BaseDirectory, "ZplCursors.dll"));
            return Microsoft.UI.Input.InputDesktopResourceCursor.CreateFromModule("ZplCursors.dll", resourceId);
        }
        catch
        {
            return Microsoft.UI.Input.InputSystemCursor.Create(fallback);
        }
    }

    private void UpdatePreviewCursor()
    {
        // Hand only when the document overflows the viewport; the grab cursor
        // needs both: panning a fully visible document keeps the normal arrow.
        bool pannable = PreviewScrollViewer.ScrollableWidth > 0.5 || PreviewScrollViewer.ScrollableHeight > 0.5;
        PreviewCursorHost.SetCursor(!pannable ? null! : _isPanning ? PanGrabCursor : PanHandCursor);
    }

    private void PreviewScrollViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewScrollViewer).Properties.IsLeftButtonPressed) return;
        _isPanning = true;
        _panStart  = e.GetCurrentPoint(PreviewScrollViewer).Position;
        _panStartH = PreviewScrollViewer.HorizontalOffset;
        _panStartV = PreviewScrollViewer.VerticalOffset;
        PreviewScrollViewer.CapturePointer(e.Pointer);
        UpdatePreviewCursor();
        e.Handled = true;
    }

    private void PreviewScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetCurrentPoint(PreviewScrollViewer).Position;
        PreviewScrollViewer.ChangeView(
            _panStartH - (pos.X - _panStart.X),
            _panStartV - (pos.Y - _panStart.Y),
            null, disableAnimation: true);
        e.Handled = true;
    }

    private void PreviewScrollViewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        PreviewScrollViewer.ReleasePointerCapture(e.Pointer);
        UpdatePreviewCursor();
        e.Handled = true;
    }

    private void PreviewScrollViewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPanning = false;
        UpdatePreviewCursor();
    }

    // ── Editor / preview splitter (horizontal resize only) ──────────────────

    private bool _isResizingEditor;
    private bool _splitterDragged;   // moved past the click threshold since press
    private double _resizeStartX;
    private double _resizeStartWidth;
    private const double SplitterClickSlop = 4; // px of movement still counted as a click

    private void EditorSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizingEditor = true;
        _splitterDragged  = false;
        _resizeStartX     = e.GetCurrentPoint(Root).Position.X;
        _resizeStartWidth = EditorColumnDef.ActualWidth;
        EditorSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void EditorSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingEditor) return;
        var x = e.GetCurrentPoint(Root).Position.X;
        var delta = x - _resizeStartX;
        if (Math.Abs(delta) > SplitterClickSlop) _splitterDragged = true;
        // Resizing only applies while the editor is shown; a hidden editor can only
        // be expanded by a click (handled on release).
        if (!_editorVisible || !_splitterDragged) return;
        // When the editor is on the right, dragging left grows it (invert).
        if (_settings.SwapEditorPreview) delta = -delta;
        // Clamp so both the editor and the preview always stay usable.
        var maxWidth = Math.Max(220, Root.ActualWidth - 320);
        _editorWidth = Math.Clamp(_resizeStartWidth + delta, 220, maxWidth);
        EditorColumnDef.Width = new GridLength(_editorWidth);
        e.Handled = true;
    }

    private void EditorSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingEditor) return;
        _isResizingEditor = false;
        EditorSplitter.ReleasePointerCapture(e.Pointer);
        // A press without a real drag is a click on the collapse handle.
        if (!_splitterDragged) ToggleEditor();
        e.Handled = true;
    }

    // ── Diagnostics panel splitter (vertical resize only) ───────────────────

    private bool _isResizingErrorPanel;
    private double _errorResizeStartY;
    private double _errorResizeStartHeight;

    private void ErrorPanelSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizingErrorPanel   = true;
        _errorResizeStartY      = e.GetCurrentPoint(Root).Position.Y;
        _errorResizeStartHeight = ErrorList.Height;
        ErrorPanelSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ErrorPanelSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingErrorPanel) return;
        var y = e.GetCurrentPoint(Root).Position.Y;
        // Dragging up (y decreases) grows the panel; keep the editor usable.
        var maxHeight = Math.Max(60, EditorHost.ActualHeight - 160);
        ErrorList.Height = Math.Clamp(_errorResizeStartHeight + (_errorResizeStartY - y), 60, maxHeight);
        e.Handled = true;
    }

    private void ErrorPanelSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingErrorPanel) return;
        _isResizingErrorPanel = false;
        ErrorPanelSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // ── Documentation panel splitter (vertical resize only) ─────────────────

    private bool _isResizingDocPanel;
    private double _docResizeStartY;
    private double _docResizeStartHeight;

    private void DocPanelSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isResizingDocPanel   = true;
        _docResizeStartY      = e.GetCurrentPoint(Root).Position.Y;
        _docResizeStartHeight = DocScroll.Height;
        DocPanelSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void DocPanelSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDocPanel) return;
        var y = e.GetCurrentPoint(Root).Position.Y;
        var maxHeight = Math.Max(80, EditorHost.ActualHeight - 160);
        DocScroll.Height = Math.Clamp(_docResizeStartHeight + (_docResizeStartY - y), 80, maxHeight);
        e.Handled = true;
    }

    private void DocPanelSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingDocPanel) return;
        _isResizingDocPanel = false;
        DocPanelSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // Greys out a size field when its value is dictated by the auto-size policy:
    //  - auto + ^PW/^LL only : each field locks only if its command is present
    //  - auto + elements mode: both fields always locked (command or computed)
    //  - auto disabled       : both fields always editable
    private void UpdateSizeBoxLocks()
    {
        bool auto  = _settings.AutoDocSize;
        bool elems = _settings.AutoDocSizeMode == 1;
        WidthBox.IsEnabled  = !auto || (!elems && _lastPw is null);
        HeightBox.IsEnabled = !auto || (!elems && _lastLl is null);
    }

    private void UpdateSizeBoxes(bool fillEmptyBoxes = true)
    {
        // The SIZE FIELDS show the requested (un-clamped) size — you can type any value.
        var reqSize = _requestedSize ?? _model.Size;
        var width = UnitConverter.FromMillimeters(reqSize.WidthMm(SelectedDpmm), _settings.Unit);
        var height = UnitConverter.FromMillimeters(reqSize.HeightMm(SelectedDpmm), _settings.Unit);
        var widthText = UnitConverter.FormatLength(width);
        var heightText = UnitConverter.FormatLength(height);

        // A field the user emptied stays empty: the effective value is shown as
        // a placeholder instead, until a document-level action refills the boxes.
        // A field being TYPED IN is never rewritten: reformatting "10," to "10"
        // mid-keystroke made decimals impossible to enter (re-synced on LostFocus).
        if (WidthBox.FocusState == FocusState.Unfocused)
        {
            if (WidthBox.Text.Length > 0 || fillEmptyBoxes) WidthBox.Text = widthText;
            else WidthBox.PlaceholderText = widthText;
        }
        if (HeightBox.FocusState == FocusState.Unfocused)
        {
            if (HeightBox.Text.Length > 0 || fillEmptyBoxes) HeightBox.Text = heightText;
            else HeightBox.PlaceholderText = heightText;
        }

        UnitLabel1.Text = UnitConverter.UnitLabel(_settings.Unit);
        UnitLabel2.Text = UnitConverter.UnitLabel(_settings.Unit);

        // The CAPTION shows the effective (clamped) rendered size, e.g. "50 cm × 50 cm".
        var capW = UnitConverter.FormatLength(UnitConverter.FromMillimeters(_model.Size.WidthMm(SelectedDpmm), _settings.Unit));
        var capH = UnitConverter.FormatLength(UnitConverter.FromMillimeters(_model.Size.HeightMm(SelectedDpmm), _settings.Unit));
        var unit = UnitConverter.UnitLabel(_settings.Unit);
        var dpi = (DensityComboBox.SelectedItem as DpmmOption)?.Dpi
                  ?? (int)Math.Round(SelectedDpmm * 25.4);
        _captionSizeDpi = $"{capW} {unit} × {capH} {unit} — {dpi} dpi";
        UpdatePreviewCaption();
    }

    // Requested (un-clamped) label size; the fields show this while the preview
    // and caption use the 50 cm-clamped _model.Size.
    private LabelSize? _requestedSize;

    private string _captionSizeDpi = "";

    // Appends the live (DPI-independent) zoom level to the size/dpi caption,
    // and keeps the toolbar zoom box in sync with the actual zoom.
    // Shows/hides the size·dpi·zoom caption at the bottom-right of the preview.
    private void ApplyPreviewCaptionVisibility() =>
        CaptionHost.Visibility = _settings.ShowPreviewCaption ? Visibility.Visible : Visibility.Collapsed;

    private void UpdatePreviewCaption()
    {
        var zoom = (int)Math.Round(DisplayZoomPercent);
        // With the grid on, the caption ends with the cell size then a double arrow
        // one grid cell wide: … — 100 % — 1 cm <->. The value is part of the text
        // (better rendering) and the arrow occupies the rightmost complete cell, on
        // the grid lines (see RenderCaption).
        var text = $"{_captionSizeDpi} — {zoom} %";
        double? arrowStep = null;
        if (_settings.ShowPreviewGrid)
        {
            text += $" — {_settings.PreviewGridSpacing.ToString("0.##", CultureInfo.CurrentCulture)} {GridUnitSymbol}";
            arrowStep = GridStepDip;
        }
        RenderCaption(text, arrowStep);
        // Don't overwrite the box while the user is typing in it.
        if (ZoomValueBox.FocusState == FocusState.Unfocused)
            ZoomValueBox.Text = $"{zoom} %";
        UpdatePreviewCursor(); // scrollability may have changed with the zoom
    }

    // The caption reads over both the white label and the dark canvas: each pixel
    // of the text takes the INVERTED colour of whatever is currently behind it
    // (live — scroll/zoom included). Implemented with Win2D + Composition: a
    // SpriteVisual whose brush is InvertEffect(backdrop) masked by the text alpha,
    // the text itself being rasterised into a CompositionDrawingSurface.
    private SpriteVisual? _captionSprite;
    private CompositionDrawingSurface? _captionSurface;
    private CompositionGraphicsDevice? _captionGfxDevice;
    private CompositionSurfaceBrush? _captionMaskBrush;
    private const float CaptionFontSize = 12f;

    private void BuildCaptionVisual()
    {
        if (_captionSprite is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(CaptionHost).Compositor;
        _captionGfxDevice = CanvasComposition.CreateCompositionGraphicsDevice(
            compositor, CanvasDevice.GetSharedDevice());
        _captionSurface = _captionGfxDevice.CreateDrawingSurface(
            new Windows.Foundation.Size(1, 1),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
            Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);

        // invert(what's behind the sprite), clipped to the text's alpha mask
        var invert = new InvertEffect { Source = new CompositionEffectSourceParameter("backdrop") };
        var effectBrush = compositor.CreateEffectFactory(invert).CreateBrush();
        effectBrush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());

        _captionMaskBrush = compositor.CreateSurfaceBrush(_captionSurface);
        _captionMaskBrush.Stretch = CompositionStretch.Fill;
        var mask = compositor.CreateMaskBrush();
        mask.Source = effectBrush;
        mask.Mask = _captionMaskBrush;

        _captionSprite = compositor.CreateSpriteVisual();
        _captionSprite.Brush = mask;
        ElementCompositionPreview.SetElementChildVisual(CaptionHost, _captionSprite);
    }

    // Renders the caption sprite. When arrowStepDip is set, a double-headed arrow
    // exactly one grid cell wide is drawn LAST (rightmost), after the text that
    // already ends with the cell size. The whole caption is then positioned so the
    // arrow occupies the rightmost COMPLETE grid cell — both arrow ends land on grid
    // lines — and the text grows leftwards from it.
    private void RenderCaption(string text, double? arrowStepDip)
    {
        BuildCaptionVisual();
        if (_captionSprite is null || _captionSurface is null) return;

        float scale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = CaptionFontSize * scale,
        };
        var device = CanvasDevice.GetSharedDevice();
        using var layout = new CanvasTextLayout(device, text, format, 4096, 128);

        float gap = 6f * scale;
        float mainW = (float)layout.LayoutBounds.Width;
        float textH = (float)layout.LayoutBounds.Height;
        float arrowW = arrowStepDip is double s ? (float)(s * scale) : 0f;

        float totalW = mainW + (arrowStepDip is null ? 0f : gap + arrowW);
        int pxW = Math.Max(1, (int)Math.Ceiling(totalW) + 2);
        int pxH = Math.Max(1, (int)Math.Ceiling(textH) + 2);

        CanvasComposition.Resize(_captionSurface, new Windows.Foundation.Size(pxW, pxH));
        using (var ds = CanvasComposition.CreateDrawingSession(_captionSurface))
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.DrawTextLayout(layout, 1, 1, Microsoft.UI.Colors.White);
            if (arrowStepDip is not null)
            {
                float x0 = 1 + mainW + gap;
                DrawScaleArrow(ds, x0, x0 + arrowW, 1 + textH / 2f, scale);
            }
        }

        // Sprite is sized in DIPs; the XAML root scale maps it back onto the
        // pixel-exact surface so the text stays crisp at any DPI.
        float dipW = pxW / scale, dipH = pxH / scale;
        _captionSprite.Size = new System.Numerics.Vector2(dipW, dipH);
        // Give the host the same DIP footprint so right/bottom alignment works.
        CaptionHost.Width = dipW;
        CaptionHost.Height = dipH;
        // Trailing pixels between the arrow's right end and the sprite's right edge.
        double tailDip = arrowStepDip is null ? CaptionDefaultRightMargin
                                              : (pxW - (1 + mainW + gap + arrowW)) / scale;
        CaptionHost.Margin = new Thickness(0, 0, CaptionRightMargin(arrowStepDip, tailDip), 10);
    }

    // Right margin so the scale arrow's right end lands on the rightmost grid line
    // that still fits. Grid lines sit at multiples of the step from the preview's
    // left edge. Falls back to the plain 14 DIP margin when there is no grid (or no
    // room for a full cell).
    private const double CaptionDefaultRightMargin = 14;
    private double CaptionRightMargin(double? arrowStepDip, double tailDip)
    {
        if (arrowStepDip is not double step) return CaptionDefaultRightMargin;
        double width = PreviewSurface.ActualWidth;
        if (width <= 0 || step <= 0) return CaptionDefaultRightMargin;

        // Rightmost visible grid line: k*step < width. The arrow's right end anchors
        // there (its left end then falls on (k-1)*step — the last complete cell).
        int k = (int)Math.Ceiling(width / step) - 1;
        if (k < 2) return CaptionDefaultRightMargin; // no complete cell to point at
        return Math.Max(0, width - k * step - tailDip);
    }

    // Double-headed arrow spanning [x0, x1] at height cy, with end ticks.
    private static void DrawScaleArrow(CanvasDrawingSession ds, float x0, float x1, float cy, float scale)
    {
        var white = Microsoft.UI.Colors.White;
        float w = Math.Max(1f, scale);          // 1 DIP stroke
        float head = 3.5f * scale;              // arrowhead half-height / length
        float tick = 3f * scale;                // vertical end ticks

        ds.DrawLine(x0, cy, x1, cy, white, w);
        ds.DrawLine(x0, cy - tick, x0, cy + tick, white, w);
        ds.DrawLine(x1, cy - tick, x1, cy + tick, white, w);
        // Heads pointing outwards at both ends.
        ds.DrawLine(x0, cy, x0 + head, cy - head, white, w);
        ds.DrawLine(x0, cy, x0 + head, cy + head, white, w);
        ds.DrawLine(x1, cy, x1 - head, cy - head, white, w);
        ds.DrawLine(x1, cy, x1 - head, cy + head, white, w);
    }

    private async void NewFileButton_Click(object sender, RoutedEventArgs e)
    {
        double widthMm, heightMm;

        if (_settings.NewDocSizeMode == 1)
        {
            // Use the default size from settings, no prompt.
            widthMm  = _settings.NewDocWidthMm;
            heightMm = _settings.NewDocHeightMm;
        }
        else
        {
            // Ask each time, pre-filled with the default size in the current unit.
            // Layout: label, then field with its unit right beside, per dimension.
            var defW = UnitConverter.FromMillimeters(_settings.NewDocWidthMm, _settings.Unit);
            var defH = UnitConverter.FromMillimeters(_settings.NewDocHeightMm, _settings.Unit);
            var unitLabel = UnitConverter.UnitLabel(_settings.Unit);
            var widthBox = new TextBox { Text = UnitConverter.FormatLength(defW), MinWidth = 180 };
            var heightBox = new TextBox { Text = UnitConverter.FormatLength(defH), MinWidth = 180 };

            static StackPanel FieldRow(TextBox box, string unit)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center });
                return row;
            }

            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = "Longueur" });
            panel.Children.Add(FieldRow(widthBox, unitLabel));
            panel.Children.Add(new TextBlock { Text = "Hauteur", Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(FieldRow(heightBox, unitLabel));

            var dialog = CreateDialog("Nouveau fichier", panel, "Ok", "Annuler");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (!UnitConverter.TryParseLength(widthBox.Text, out var width) || !UnitConverter.TryParseLength(heightBox.Text, out var height))
                return;
            widthMm  = UnitConverter.ToMillimeters(width, _settings.Unit);
            heightMm = UnitConverter.ToMillimeters(height, _settings.Unit);
        }

        var widthDots = (int)Math.Round(widthMm * SelectedDpmm);
        var heightDots = (int)Math.Round(heightMm * SelectedDpmm);
        AddTabAndActivate(null); // a new document opens in its own tab
        SetEditorText($"^XA\n^PW{widthDots}\n^LL{heightDots}\n^FO20,20^GB{Math.Max(1, widthDots - 40)},{Math.Max(1, heightDots - 40)},2^FS\n^XZ");
        _isDirty = true; // a new document has never been saved
        UpdateDocumentTitle();
    }

    private async void OpenFileButton_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.FileTypeFilter.Add(".zpl");
        picker.FileTypeFilter.Add(".txt");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenPathAsync(file.Path);
    }

    // Opens a file by path in its own tab, at the "open" default density, and
    // records it in the recent-files list. Shared by the picker and the recent menu.
    private async Task OpenPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            await ShowMessageAsync("Ouvrir un fichier", $"Fichier introuvable :\n{path}");
            RemoveRecentFile(path);
            _settings.Save();
            return;
        }

        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (Exception ex)
        {
            await ShowMessageAsync("Ouvrir un fichier", $"Lecture impossible :\n{ex.Message}");
            return;
        }

        _settings.LastFilePath = path;
        AddRecentFile(path);
        _settings.Save();
        ApplyOpenDensity();           // density-on-open (does not rewrite ^PW/^LL)
        AddTabAndActivate(path);      // an opened file gets its own tab
        SetEditorText(text);
        _isDirty = false;
        UpdateDocumentTitle();
    }

    // ── Recent files ──────────────────────────────────────────────────────────

    private const int RecentFilesMax = 10;

    private void AddRecentFile(string path)
    {
        var list = _settings.RecentFiles;
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > RecentFilesMax) list.RemoveRange(RecentFilesMax, list.Count - RecentFilesMax);
    }

    private void RemoveRecentFile(string path) =>
        _settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    // Rebuilds the "Ouvrir un fichier" dropdown with the recent files (newest first).
    private void PopulateRecentFilesMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        var recent = _settings.RecentFiles;
        if (recent.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = LocalizationService.Get("toolbar.recentEmpty"), IsEnabled = false });
            return;
        }
        foreach (var path in recent)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path) };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) => await OpenPathAsync(path);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = LocalizationService.Get("toolbar.recentClear") };
        clear.Click += (_, _) => { _settings.RecentFiles.Clear(); _settings.Save(); };
        flyout.Items.Add(clear);
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private void SaveButton_Click(SplitButton sender, SplitButtonClickEventArgs e) => _ = SaveAsync();

    private void SaveAsMenu_Click(object sender, RoutedEventArgs e) => _ = SaveAsAsync();

    private async Task SaveAsync()
    {
        if (_currentFilePath is null) { await SaveAsAsync(); return; }
        try
        {
            await File.WriteAllTextAsync(_currentFilePath, _currentText);
            _isDirty = false;
            UpdateDocumentTitle();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Enregistrement", $"L'enregistrement a échoué : {ex.Message}");
        }
    }

    private async Task SaveAsAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.SuggestedFileName = _currentFilePath is null ? "label" : Path.GetFileNameWithoutExtension(_currentFilePath);
        picker.FileTypeChoices.Add("ZPL", new List<string> { ".zpl" });
        picker.FileTypeChoices.Add("Texte", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await FileIO.WriteTextAsync(file, _currentText);
            _currentFilePath = file.Path;
            _isDirty = false;
            _settings.LastFilePath = file.Path;
            AddRecentFile(file.Path);
            _settings.Save();
            UpdateDocumentTitle();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Enregistrement", $"L'enregistrement a échoué : {ex.Message}");
        }
    }


    // Builds the window title from the current file + dirty state. With several
    // tabs open the names live in the tab bar and the title bar stays plain.
    private void UpdateDocumentTitle()
    {
        if (_activeTab is not null) RefreshTabHeader(_activeTab);
        var mw = AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow;
        if (DocTabs.TabItems.Count > 1)
        {
            mw?.SetDocumentTitle("Ultimate ZPL Viewer");
            return;
        }
        var name = _currentFilePath is null ? LocalizationService.Get("titlebar.untitled") : Path.GetFileName(_currentFilePath);
        var title = $"Ultimate ZPL Viewer - {name}";
        if (_isDirty || _currentFilePath is null) title += "*";
        if (_currentFilePath is not null && _settings.ShowFilePathInTitle)
            title += $" ({_currentFilePath})";
        mw?.SetDocumentTitle(title);
    }

    // ── Document tabs ─────────────────────────────────────────────────────────
    // One DocTab per open document. The ACTIVE tab's live state stays in the
    // existing page fields (_currentFilePath/_currentText/_isDirty); the DocTab
    // objects hold snapshots for the inactive tabs. Each tab has its own Monaco
    // model in the editor page, so undo history and scroll survive switches.

    private DocTab? _activeTab;
    private bool _suppressTabEvents;

    // Creates the tab for the document loaded at startup (tab bar stays hidden).
    private void InitFirstTab()
    {
        _activeTab = new DocTab { FilePath = _currentFilePath };
        _suppressTabEvents = true;
        DocTabs.TabItems.Add(MakeTabItem(_activeTab));
        DocTabs.SelectedIndex = 0;
        _suppressTabEvents = false;
        UpdateTabBar();
    }

    private TabViewItem MakeTabItem(DocTab tab)
    {
        var item = new TabViewItem
        {
            Tag = tab,
            Header = TabTitle(tab),
            Style = (Style)Resources["FloatingTabViewItemStyle"],
        };
        ToolTipService.SetToolTip(item, TabTooltip(tab));
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => BuildTabContextMenu(menu, item, tab);
        item.ContextFlyout = menu;
        return item;
    }

    // Tab hover tooltip: the file name, plus the full path in parentheses when
    // "afficher le chemin" is enabled (mirrors the window title bar).
    private string TabTooltip(DocTab tab)
    {
        bool active = ReferenceEquals(tab, _activeTab);
        var path = active ? _currentFilePath : tab.FilePath;
        var name = path is null ? LocalizationService.Get("titlebar.untitled") : Path.GetFileName(path);
        return path is not null && _settings.ShowPathInTabTooltip ? $"{name} ({path})" : name;
    }

    // Refreshes every tab's header and hover tooltip (e.g. after the
    // "afficher le chemin" setting is toggled).
    private void RefreshAllTabHeaders()
    {
        foreach (var o in DocTabs.TabItems)
            if (o is TabViewItem item && item.Tag is DocTab tab)
            {
                item.Header = TabTitle(tab);
                ToolTipService.SetToolTip(item, TabTooltip(tab));
            }
    }

    // Tab header: file name only (never the path), with the dirty star.
    private string TabTitle(DocTab tab)
    {
        bool active = ReferenceEquals(tab, _activeTab);
        var path = active ? _currentFilePath : tab.FilePath;
        bool dirty = active ? _isDirty : tab.IsDirty;
        var name = path is null ? LocalizationService.Get("titlebar.untitled") : Path.GetFileName(path);
        return dirty || path is null ? name + "*" : name;
    }

    private void RefreshTabHeader(DocTab tab)
    {
        foreach (var o in DocTabs.TabItems)
            if (o is TabViewItem item && ReferenceEquals(item.Tag, tab))
            {
                item.Header = TabTitle(tab);
                ToolTipService.SetToolTip(item, TabTooltip(tab));
                break;
            }
    }

    // Snapshots the live editor state into the active tab (before switching away).
    private void CaptureActiveTab()
    {
        if (_activeTab is null) return;
        _activeTab.FilePath = _currentFilePath;
        _activeTab.Text = _currentText;
        _activeTab.IsDirty = _isDirty;
    }

    // Opens a fresh tab (for a new or just-opened document) and makes it active.
    // The caller then fills it via SetEditorText and sets _isDirty.
    private void AddTabAndActivate(string? filePath)
    {
        CaptureActiveTab();
        var tab = new DocTab { FilePath = filePath };
        _activeTab = tab;
        _currentFilePath = filePath;
        _currentText = "";
        _isDirty = false;
        var item = MakeTabItem(tab);
        _suppressTabEvents = true;
        DocTabs.TabItems.Add(item);
        DocTabs.SelectedItem = item;
        _suppressTabEvents = false;
        if (_editorReady) PostToEditor(BuildSwitchDocMessage(tab.Id, ""));
        UpdateTabBar();
    }

    private void DocTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvents) return;
        if (DocTabs.SelectedItem is not TabViewItem item || item.Tag is not DocTab tab) return;
        if (ReferenceEquals(tab, _activeTab)) return;
        CaptureActiveTab();
        var previous = _activeTab;
        ActivateTab(tab);
        if (previous is not null) RefreshTabHeader(previous);
    }

    // Loads a tab's snapshot into the live fields and switches the Monaco model.
    private void ActivateTab(DocTab tab)
    {
        _activeTab = tab;
        _currentFilePath = tab.FilePath;
        _currentText = tab.Text;
        _isDirty = tab.IsDirty;
        if (_editorReady) PostToEditor(BuildSwitchDocMessage(tab.Id, tab.Text));
        // Bring this document back to its own zoom right away, so the incoming
        // tab never flashes at the outgoing tab's level (the redraw below
        // re-applies it once the canvas has been re-measured).
        RestoreTabZoom();
        RefreshPreview(SizeUpdate.DocumentLoaded);
        ScheduleHighlighting();
        UpdateDocumentTitle();
    }

    private void DocTabs_AddTabButtonClick(TabView sender, object args)
        => NewFileButton_Click(sender, new RoutedEventArgs());


    private async void DocTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is TabViewItem item && item.Tag is DocTab tab)
            await RequestCloseSingleAsync(item, tab);
    }

    // Close with the classic three-way question when the document has unsaved
    // changes: save (Save-As for never-saved documents) / discard / cancel.
    private async Task RequestCloseSingleAsync(TabViewItem item, DocTab tab)
    {
        bool dirty = ReferenceEquals(tab, _activeTab) ? _isDirty : tab.IsDirty;
        if (dirty)
        {
            // Make it the visible/active document so "Enregistrer" targets it.
            DocTabs.SelectedItem = item;
            var result = await ShowUnsavedDialogAsync("Fermer l'onglet");
            if (result == ContentDialogResult.None) return;               // Annuler
            if (result == ContentDialogResult.Primary)
            {
                await SaveAsync();                                        // Save-As when never saved
                if (_isDirty) return;                                     // picker cancelled / failed
            }
        }
        CloseTab(item, tab);
    }

    // "« doc » contient des modifications non enregistrées" with
    // Enregistrer / Ne pas enregistrer / Annuler, for the ACTIVE document.
    private async Task<ContentDialogResult> ShowUnsavedDialogAsync(string title)
    {
        var name = _currentFilePath is null ? LocalizationService.Get("titlebar.untitled") : Path.GetFileName(_currentFilePath);
        try
        {
            return await new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = _settings.ToElementTheme(),
                Title = title,
                Content = new TextBlock
                {
                    Text = $"« {name} » contient des modifications non enregistrées.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = _currentFilePath is null ? "Enregistrer sous…" : "Enregistrer",
                SecondaryButtonText = "Ne pas enregistrer",
                CloseButtonText = "Annuler",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
        }
        catch
        {
            // Another ContentDialog is already open (e.g. quitting while the
            // new-file or print dialog is up): behave like Annuler instead of
            // crashing — the user finishes the open dialog and retries.
            return ContentDialogResult.None;
        }
    }

    // ── Tab context menu ──────────────────────────────────────────────────────

    private const string GlyphTabClose = "\uE711";      // Dismiss (plain cross)
    private const string GlyphTabCloseOthers = "\uF78A"; // cross in a square
    private const string GlyphTabCloseRight = "\uE89F";  // arrow into the right pane
    private const string GlyphTabDuplicate = "\uF413";   // copy + add
    private const string GlyphTabCopyPath = "\uE8C8";    // copy

    // Rebuilt each time it opens: "Copier le chemin" only exists once the file
    // has been saved somewhere, and the close entries follow the tab layout.
    private void BuildTabContextMenu(MenuFlyout menu, TabViewItem item, DocTab tab)
    {
        menu.Items.Clear();

        MenuFlyoutItem Mk(string text, string glyph, Action action, bool enabled = true)
        {
            var mi = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = enabled,
            };
            mi.Click += (_, _) => action();
            return mi;
        }

        int index = DocTabs.TabItems.IndexOf(item);
        menu.Items.Add(Mk("Fermer l'onglet", GlyphTabClose,
            () => _ = RequestCloseSingleAsync(item, tab)));
        menu.Items.Add(Mk("Fermer les autres onglets", GlyphTabCloseOthers,
            () => _ = CloseManyAsync(TabsExcept(item), item),
            enabled: DocTabs.TabItems.Count > 1));
        menu.Items.Add(Mk("Fermer les onglets à droite", GlyphTabCloseRight,
            () => _ = CloseManyAsync(TabsRightOf(item), item),
            enabled: index >= 0 && index < DocTabs.TabItems.Count - 1));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Mk("Dupliquer l'onglet", GlyphTabDuplicate,
            () => DuplicateTab(tab)));

        var path = ReferenceEquals(tab, _activeTab) ? _currentFilePath : tab.FilePath;
        if (path is not null)
            menu.Items.Add(Mk("Copier le chemin du fichier", GlyphTabCopyPath,
                () => CopyTextToClipboard(path)));
    }

    private List<TabViewItem> TabsExcept(TabViewItem keep)
        => DocTabs.TabItems.OfType<TabViewItem>().Where(t => !ReferenceEquals(t, keep)).ToList();

    private List<TabViewItem> TabsRightOf(TabViewItem item)
    {
        int i = DocTabs.TabItems.IndexOf(item);
        return DocTabs.TabItems.OfType<TabViewItem>().Skip(i + 1).ToList();
    }

    // Mass close ("close others" / "close to the right"). Every dirty document is
    // resolved FIRST — Enregistrer / Ne pas enregistrer / Annuler — and Annuler
    // (or a cancelled Save-As) aborts the whole operation with nothing closed.
    private async Task CloseManyAsync(List<TabViewItem> items, TabViewItem clicked)
    {
        if (items.Count == 0) return;

        foreach (var item in items)
        {
            var tab = (DocTab)item.Tag;
            bool dirty = ReferenceEquals(tab, _activeTab) ? _isDirty : tab.IsDirty;
            if (!dirty) continue;

            // Show the document being decided on (also makes SaveAsync target it).
            DocTabs.SelectedItem = item;
            var result = await ShowUnsavedDialogAsync("Fermer les onglets");
            if (result == ContentDialogResult.None) return; // Annuler → close nothing
            if (result == ContentDialogResult.Primary)
            {
                await SaveAsync();
                if (_isDirty) return; // Save-As cancelled or failed → abort everything
            }
            // Secondary (Ne pas enregistrer): just proceed, the close below discards.
        }

        foreach (var item in items)
            CloseTab(item, (DocTab)item.Tag);

        // Land back on the tab the menu was opened on.
        if (DocTabs.TabItems.Contains(clicked))
            DocTabs.SelectedItem = clicked;
    }

    // Duplicates the document into a new never-saved tab (own undo history).
    private void DuplicateTab(DocTab tab)
    {
        var text = ReferenceEquals(tab, _activeTab) ? _currentText : tab.Text;
        AddTabAndActivate(null);
        SetEditorText(text);
        _isDirty = true;
        UpdateDocumentTitle();
    }

    private static void CopyTextToClipboard(string text)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    // ── File drag & drop (open in new tabs) ───────────────────────────────────

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        // Not over the settings screen, and only for files.
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Ouvrir";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var it in items)
        {
            if (it is not StorageFile file) continue;
            var ext = Path.GetExtension(file.Path).ToLowerInvariant();
            if (ext is not (".zpl" or ".txt")) continue;
            var content = await FileIO.ReadTextAsync(file);
            AddTabAndActivate(file.Path);
            SetEditorText(content);
            _isDirty = false;
            _settings.LastFilePath = file.Path;
        }
        _settings.Save();
        UpdateDocumentTitle();
    }

    // Called when the window is closing: resolves every unsaved document
    // (Enregistrer / Ne pas enregistrer / Annuler — Annuler keeps the app open),
    // then records the open saved documents so ReopenLastFile can restore them.
    // Returns false to abort the close.
    public async Task<bool> PrepareAppCloseAsync()
    {
        foreach (var item in DocTabs.TabItems.OfType<TabViewItem>().ToList())
        {
            var tab = (DocTab)item.Tag;
            bool dirty = ReferenceEquals(tab, _activeTab) ? _isDirty : tab.IsDirty;
            if (!dirty) continue;

            DocTabs.SelectedItem = item; // show the document being decided on
            var result = await ShowUnsavedDialogAsync("Quitter l'application");
            if (result == ContentDialogResult.None) return false;
            if (result == ContentDialogResult.Primary)
            {
                await SaveAsync();
                if (_isDirty) return false; // Save-As cancelled / failed
            }
        }

        CaptureActiveTab();
        _settings.OpenFiles = DocTabs.TabItems.OfType<TabViewItem>()
            .Select(t => ((DocTab)t.Tag).FilePath)
            .Where(p => p is not null)
            .Distinct()
            .ToList()!;
        _settings.Save();
        return true;
    }

    private void CloseTab(TabViewItem item, DocTab tab)
    {
        int idx = DocTabs.TabItems.IndexOf(item);
        bool wasActive = ReferenceEquals(tab, _activeTab);
        _suppressTabEvents = true;
        DocTabs.TabItems.Remove(item);
        if (wasActive && DocTabs.TabItems.Count > 0)
        {
            var next = (TabViewItem)DocTabs.TabItems[Math.Min(idx, DocTabs.TabItems.Count - 1)];
            DocTabs.SelectedItem = next;
            ActivateTab((DocTab)next.Tag);
        }
        _suppressTabEvents = false;
        // After the switch, so the editor page never disposes the model in use.
        if (_editorReady) PostToEditor($"{{\"type\":\"closeDoc\",\"id\":\"{tab.Id}\"}}");
        UpdateTabBar();
        UpdateDocumentTitle();
    }

    // The tab bar only exists with two documents or more; with a single one the
    // document name lives in the window title bar instead.
    private void UpdateTabBar()
        => DocTabs.Visibility = DocTabs.TabItems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    private static string BuildSwitchDocMessage(string id, string text)
        => $"{{\"type\":\"switchDoc\",\"id\":\"{id}\",\"text\":{JsonSerializer.Serialize(text)}}}";

    private double _lastDpmm;

    private void DensityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Density changes resolution, not physical size. Since ^PW/^LL are dot
        // values, they are rescaled in the ZPL code itself (ratio new/old
        // density) so that size-in-dots ÷ density stays constant; the other
        // elements keep their dot coordinates and appear smaller.
        var newDpmm = SelectedDpmm;
        var oldDpmm = _lastDpmm;
        // A density set as part of opening a document must NOT rewrite its ^PW/^LL —
        // that rescale is only for an interactive density change (see ApplyOpenDensity).
        bool changed = !_suppressDensityRescale && oldDpmm > 0 && Math.Abs(newDpmm - oldDpmm) > 0.001;

        // Keep the on-screen physical size constant: the canvas gains/loses dots,
        // so scale the zoom by the inverse ratio (elements appear smaller/larger,
        // the document keeps its size on screen).
        if (changed)
            _pendingZoomOverride = (float)(PreviewScrollViewer.ZoomFactor * oldDpmm / newDpmm);

        if (changed)
        {
            var rescaled = RescalePwLl(_currentText, newDpmm / oldDpmm);
            if (rescaled != _currentText)
            {
                _lastDpmm = newDpmm;
                SetEditorText(rescaled, SizeUpdate.KeepCurrent);
                return;
            }
        }

        _lastDpmm = newDpmm;
        if (!_suppressDensityRescale) RefreshPreview(SizeUpdate.KeepCurrent);
    }

    private bool _suppressDensityRescale;

    // Sets the density to the default without rescaling the document's ^PW/^LL
    // (used when opening/restoring a file, not when the user changes density).
    private void ApplyOpenDensity()
    {
        _suppressDensityRescale = true;
        SelectDensity(_settings.DefaultDpmm);
        _lastDpmm = SelectedDpmm;
        _suppressDensityRescale = false;
    }

    // Multiplies every ^PW / ^LL value by the given ratio (dot values follow
    // the density so the physical size never changes).
    private static string RescalePwLl(string text, double ratio)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text,
            @"(\^(?:PW|LL)\s*)(\d+(?:\.\d+)?)",
            m =>
            {
                var value = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                var scaled = Math.Max(1, (int)Math.Round(value * ratio));
                return m.Groups[1].Value + scaled;
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private void SizeBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        RefreshPreview(SizeUpdate.KeepCurrent);
    }

    private void RotateButton_Click(SplitButton sender, SplitButtonClickEventArgs e) => Rotate90();

    private void Rotate90()
    {
        _rotationDegrees = (_rotationDegrees + 90) % 360;
        // Rotating must keep the user's zoom: block the default-zoom re-application
        // that every redraw schedules (the zoom factor itself doesn't change).
        _suppressDefaultZoom++;
        if (RotationBox != null)
            RotationBox.Text = _rotationDegrees.ToString("0.##");
        RefreshPreview(SizeUpdate.KeepCurrent);
        // Lifted after the redraws' queued ApplyDefaultZoom calls have run (FIFO).
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _suppressDefaultZoom--);
    }

    private void RotationBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
    {
        if (_updating)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sender.Text))
        {
            // Field cleared (manually or via the ✕ button) → back to 0°.
            _rotationDegrees = 0;
            RefreshPreview(SizeUpdate.KeepCurrent);
        }
        else if (double.TryParse(sender.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var angle))
        {
            _rotationDegrees = Math.Clamp(angle, 0, 359.99);
            RefreshPreview(SizeUpdate.KeepCurrent);
        }
    }


    private async void PngButton_Click(object sender, RoutedEventArgs e)
    {
        // Resolution factor: "ask" pops the quality dialog, "default" uses the
        // saved step silently. 1=÷2, 2=÷1.5, 3=original, 4=×1.5, 5=×2.
        int step;
        if (_settings.PngExportMode == "default")
        {
            step = _settings.PngQualityStep;
        }
        else
        {
            var chosen = await AskPngQualityAsync();
            if (chosen is null) return; // cancelled
            step = chosen.Value;
        }

        var snapshot = await RenderSnapshotAsync();
        var bytes = await EncodePngAsync(snapshot, PngScaleForStep(step));
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.SuggestedFileName = _currentFilePath is null ? "label" : Path.GetFileNameWithoutExtension(_currentFilePath);
        picker.FileTypeChoices.Add("PNG", new List<string> { ".png" });
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await FileIO.WriteBytesAsync(file, bytes);
        }
    }

    // Linear resolution factor for a quality step (both dimensions).
    private static double PngScaleForStep(int step) => step switch
    {
        1 => 0.5,
        2 => 1.0 / 1.5,
        4 => 1.5,
        5 => 2.0,
        _ => 1.0, // 3 = original
    };

    // PNG export quality picker: a 5-notch slider (light↔heavy), middle = original.
    // Returns the chosen step, or null if cancelled.
    private async Task<int?> AskPngQualityAsync()
    {
        double sc = XamlRoot?.RasterizationScale ?? 1.0;
        double cw = double.IsNaN(PreviewCanvas.Width) ? 0 : PreviewCanvas.Width;
        double ch = double.IsNaN(PreviewCanvas.Height) ? 0 : PreviewCanvas.Height;
        int baseW = Math.Max(1, (int)Math.Round(cw * sc));
        int baseH = Math.Max(1, (int)Math.Round(ch * sc));

        var slider = new Slider
        {
            Minimum = 1, Maximum = 5, StepFrequency = 1, TickFrequency = 1,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside,
            Value = 3, IsThumbToolTipEnabled = false, Margin = new Thickness(0, 4, 0, 0),
        };
        var desc = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var dim  = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.65, FontSize = 12 };

        var ends = new Grid();
        ends.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ends.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left  = new TextBlock { Text = SL("png.quality.lighter"), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var right = new TextBlock { Text = SL("png.quality.heavier"), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(right, 1);
        ends.Children.Add(left); ends.Children.Add(right);

        void Upd()
        {
            int s = (int)Math.Round(slider.Value);
            desc.Text = SL($"png.quality.step{s}");
            double f = PngScaleForStep(s);
            dim.Text = $"≈ {(int)Math.Round(baseW * f)} × {(int)Math.Round(baseH * f)} px";
        }
        slider.ValueChanged += (_, _) => Upd();
        Upd();

        var content = new StackPanel { Spacing = 8, MinWidth = 400 };
        content.Children.Add(desc);
        content.Children.Add(dim);
        content.Children.Add(slider);
        content.Children.Add(ends);

        var dialog = CreateDialog(SL("png.quality.dlgTitle"), content, SL("png.quality.export"), SL("png.quality.cancel"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (int)Math.Round(slider.Value);
    }

    private async void PdfButton_Click(object sender, RoutedEventArgs e)
    {
        // Vector PDF built from the render model (crisp at any zoom), not a raster.
        var pdf = ZplRenderer.ToPdf(_model, SelectedDpmm, _rotationDegrees);
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        picker.SuggestedFileName = _currentFilePath is null ? "label" : Path.GetFileNameWithoutExtension(_currentFilePath);
        picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            await FileIO.WriteBytesAsync(file, pdf);
        }
    }

    private void PrinterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is string printer)
        {
            _settings.LastPrinter = printer;
            _settings.Save();
        }
    }

    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is not string printer || string.IsNullOrWhiteSpace(printer))
        {
            await ShowMessageAsync("Impression", "Sélectionnez d'abord une imprimante dans la liste.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentText))
        {
            await ShowMessageAsync("Impression", "Le document ZPL est vide : rien à imprimer.");
            return;
        }

        if (_settings.ConfirmBeforePrint)
        {
            var confirm = CreateDialog("Imprimer",
                new TextBlock
                {
                    Text = $"Envoyer l'étiquette à « {printer} » ?\n\n" +
                           "Le code ZPL est envoyé tel quel (données brutes) : l'imprimante doit être " +
                           "une imprimante d'étiquettes compatible ZPL (Zebra ou équivalent).",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                "Imprimer", "Annuler");
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        try
        {
            var zpl = _currentText;
            await Task.Run(() => RawPrinterService.SendRaw(printer, zpl, "Ultimate ZPL Viewer"));
            await ShowMessageAsync("Impression", $"Étiquette envoyée à « {printer} ».");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Erreur d'impression",
                $"L'envoi à « {printer} » a échoué : {ex.Message}");
        }
    }


    // ── Full-screen settings (PowerToys style) ──────────────────────────────

    private Dictionary<string, UIElement>? _settingsCategories;
    private DispatcherTimer? _accentApplyTimer;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings("general");

    // Opens the settings overlay on the given category.
    private void OpenSettings(string categoryTag)
    {
        LocalizeSettingsNav();
        BuildSettingsCategories();
        SettingsOverlay.Visibility = Visibility.Visible;
        var navItem = SettingsNav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == categoryTag) ?? (NavigationViewItem)SettingsNav.MenuItems[0];
        SettingsNav.SelectedItem = navItem;
        ShowSettingsCategory((navItem.Tag as string) ?? "doc");
        // Move the back arrow + title into the window title bar (Windows Settings style).
        (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.EnterSettingsMode(CloseSettings);
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.ExitSettingsMode();
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            ShowSettingsCategory(tag);
    }

    private string? _currentSettingsTag;

    private void ShowSettingsCategory(string tag)
    {
        if (_settingsCategories is null) return;
        _currentSettingsTag = tag;
        SettingsContentHost.Children.Clear();
        if (_settingsCategories.TryGetValue(tag, out var content))
            SettingsContentHost.Children.Add(content);
    }

    // Guards the language combo while we programmatically revert an invalid pick.
    private bool _revertingLanguage;

    // Tells the user their selected language file is not valid JSON (kept short —
    // no error dump) and offers to open it prefilled in JSONLint.
    private async Task ShowInvalidLanguageDialogAsync(string code)
    {
        if (XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = SL("appearance.invalidLang.title"),
            Content = new TextBlock { Text = SL("appearance.invalidLang.body"), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = SL("appearance.invalidLang.check"),
            CloseButtonText = SL("appearance.invalidLang.ok"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var raw = LocalizationService.GetLanguageRaw(code) ?? "";
        var url = "https://jsonlint.com/?json=" + Uri.EscapeDataString(raw);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no browser / launch blocked */ }
    }

    // Re-localizes everything after the active language file changed (switched in
    // the dropdown, or edited/replaced on disk). Rebuilds the settings pages so the
    // language list and every localized label refresh, and re-shows the category
    // currently in view.
    private void ApplyLanguageLive()
    {
        ApplyToolbarStrings();
        LocalizeSettingsNav();
        if (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) is MainWindow mw) mw.LocalizeTitleBar();
        UpdateDocumentTitle();      // refresh "Sans titre"/"Untitled" + tab headers
        ReloadEditorForLanguage();  // reload Monaco in the new UI language (no-op if unchanged)
        _settingsCategories = null; // force rebuild with the new strings / language list
        BuildSettingsCategories();
        var tag = _currentSettingsTag ?? "appearance";
        SettingsNav.SelectedItem = SettingsNav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == tag);
        ShowSettingsCategory(tag);
    }

    // Live reaction to files added/edited/renamed/deleted in the languages folder.
    // Marshalled to the UI thread; if the active language file was removed, falls
    // back to English (or the first available) before re-localizing.
    private void OnLanguagesChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var langs = LocalizationService.AvailableLanguages();
            if (langs.Count == 0) return;
            if (!langs.Any(l => string.Equals(l.Code, _settings.Language, StringComparison.OrdinalIgnoreCase)))
            {
                _settings.Language = langs.Any(l => l.Code == "en") ? "en" : langs[0].Code;
                _settings.Save();
            }
            LocalizationService.SetLanguage(_settings.Language); // reload (content may have changed)
            ApplyLanguageLive();
        });
    }

    // Builds the four category panels with live-applied, immediately-saved
    // controls (no OK button — settings take effect as you change them).
    private void BuildSettingsCategories()
    {
        _settingsCategories = new Dictionary<string, UIElement>
        {
            // Every category except Editor (its own 5-column grid) and Toolbar
            // (a designer canvas) forces each card to exactly 1/3 of the container
            // width, so every sub-category lines up identically.
            ["doc"]        = WithThirdWidthCards(BuildDocumentSettings()),
            ["print"]      = WithThirdWidthCards(BuildPrintSettings()),
            ["editor"]     = BuildEditorSettings(),
            ["appearance"] = WithThirdWidthCards(BuildAppearanceSettings()),
            ["toolbar"]    = BuildToolbarDesignerSettings(), // designer card: not applicable
            ["screen"]     = WithThirdWidthCards(BuildScreenSettings()),
            ["printer"]    = WithThirdWidthCards(BuildVirtualPrinterSettings()),
            ["general"]    = WithThirdWidthCards(BuildGeneralSettings()),
            ["about"]      = WithThirdWidthCards(BuildAboutSettings()),
        };
    }

    private static StackPanel SettingsPanel() => new() { Spacing = 4 };

    // Big category title + one-line description.
    // Section-key → settings.nav key (the nav Tags predate the language files).
    private static readonly Dictionary<string, string> SettingsNavKey = new()
    {
        ["general"] = "general", ["doc"] = "document", ["editor"] = "editor",
        ["print"] = "print", ["appearance"] = "appearance", ["toolbar"] = "toolbar",
        ["screen"] = "screen", ["printer"] = "virtualPrinter", ["about"] = "about",
    };

    private void LocalizeSettingsNav()
    {
        foreach (var item in SettingsNav.MenuItems.OfType<NavigationViewItem>())
            if (item.Tag is string tag && SettingsNavKey.TryGetValue(tag, out var k))
                item.Content = LocalizationService.Get($"settings.nav.{k}");
    }

    // Localized settings string / string-array shortcuts (settings.* keys).
    private static string SL(string key) => LocalizationService.Get("settings." + key);
    private static string[] SA(string key) => LocalizationService.GetArray("settings." + key);

    // Localized header: pulls settings.{section}.title / .subtitle from the language file.
    private static StackPanel LocalizedSettingsHeader(string section) =>
        SettingsHeader(LocalizationService.Get($"settings.{section}.title"),
                       LocalizationService.Get($"settings.{section}.subtitle"));

    private static StackPanel SettingsHeader(string title, string subtitle)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        p.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold });
        p.Children.Add(new TextBlock
        {
            Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });
        return p;
    }

    // A PowerToys-style settings card: icon | title + description | control,
    // with an optional expanded area shown below (e.g. sub-options).
    private static Border MakeCard(string glyph, string title, string? description,
        FrameworkElement? control, FrameworkElement? expanded = null)
    {
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = glyph, FontSize = 18, Opacity = 0.9,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(new TextBlock
            {
                Text = description, Opacity = 0.6, FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(control, 2);
            row.Children.Add(control);
        }

        FrameworkElement child = row;
        if (expanded is not null)
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(row);
            stack.Children.Add(expanded);
            child = stack;
        }

        return new Border
        {
            Tag = "settings-card", // marker used by WithThirdWidthCards
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 3, 0, 3),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = child,
        };
    }

    // Card floor width: below this a card can no longer fit its content cleanly.
    // Also the width a card fills at the window's minimum size (≈514 + 2×32 margins).
    private const double MinCardWidth = 514;

    // Forces every card of a settings category to one third of the container width,
    // but never below MinCardWidth, so all sub-categories share one homogeneous
    // card size. Cards are re-collected on each pass so those a category adds
    // asynchronously (e.g. the Screen category) are picked up too.
    private static UIElement WithThirdWidthCards(UIElement category)
    {
        if (category is not FrameworkElement root) return category;

        bool pending = false;
        double lastW = -1;
        int lastCount = -1;
        void Apply(bool force = false)
        {
            if (pending) return;
            pending = true;
            root.DispatcherQueue.TryEnqueue(() =>
            {
                pending = false;
                double w = root.ActualWidth;
                if (w <= 0) return;
                var cards = new List<FrameworkElement>();
                CollectSettingsCards(root, cards);
                // React only when the width or the card set changed — setting widths
                // alters card heights, which re-fires SizeChanged; without this the
                // two would feed back into each other.
                if (!force && Math.Abs(w - lastW) < 0.5 && cards.Count == lastCount) return;
                lastW = w; lastCount = cards.Count;
                double cw = Math.Max(MinCardWidth, Math.Floor(w / 3));
                foreach (var c in cards) { c.MaxWidth = double.PositiveInfinity; c.Width = cw; }
            });
        }
        root.Loaded += (_, _) => Apply(force: true);
        // SizeChanged also fires on height changes, so async-populated categories
        // (Screen) trigger a pass when their extra cards grow the panel; the card
        // count in that pass differs from lastCount, so the guard lets it through.
        root.SizeChanged += (_, _) => Apply();
        return category;
    }

    private static void CollectSettingsCards(DependencyObject node, List<FrameworkElement> cards)
    {
        if (node is FrameworkElement fe && (fe.Tag as string) == "settings-card")
        {
            cards.Add(fe);
            return;
        }
        if (node is Panel p)
            foreach (var child in p.Children) CollectSettingsCards(child, cards);
        else if (node is Border b && b.Child is not null)
            CollectSettingsCards(b.Child, cards);
        else if (node is ContentControl cc && cc.Content is DependencyObject d)
            CollectSettingsCards(d, cards);
    }


    // Small sub-section header inside a settings category.
    private static TextBlock SubHeader(string title) => new()
    {
        Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14,
        Margin = new Thickness(0, 14, 0, 2),
    };

    private static ToggleSwitch MakeToggle(bool isOn)
        => new() { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0 };

    // A compact color swatch button that opens a ColorPicker flyout.
    private static Button MakeColorButton(Windows.UI.Color initial, Action<Windows.UI.Color> onChanged, bool alpha = false)
    {
        var swatch = new Border
        {
            Width = 40, Height = 24, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(initial),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = alpha,
            IsMoreButtonVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Color = initial,
        };
        picker.ColorChanged += (_, args) =>
        {
            ((SolidColorBrush)swatch.Background).Color = args.NewColor;
            onChanged(args.NewColor);
        };
        return new Button
        {
            Padding = new Thickness(4),
            Content = swatch,
            Flyout = new Flyout { Content = picker },
        };
    }

    private UIElement BuildDocumentSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("document"));

        var unit = new ComboBox { MinWidth = 160,
            ItemsSource = SA("document.opt.unit"), SelectedIndex = (int)_settings.Unit };
        unit.SelectionChanged += (_, _) =>
        {
            _settings.Unit = (LengthUnit)Math.Max(0, unit.SelectedIndex);
            _settings.Save();
            UpdateSizeBoxes();
        };
        panel.Children.Add(MakeCard("\uE7C3", SL("document.cards.unit.title"),
            SL("document.cards.unit.desc"), unit));

        var dpi = new ComboBox { MinWidth = 180,
            ItemsSource = new[] { "6 dpmm (152 dpi)", "8 dpmm (203 dpi)", "12 dpmm (305 dpi)", "24 dpmm (610 dpi)" } };
        dpi.SelectedIndex = _settings.DefaultDpmm switch { 6 => 0, 12 => 2, 24 => 3, _ => 1 };
        dpi.SelectionChanged += (_, _) =>
        {
            _settings.DefaultDpmm = dpi.SelectedIndex switch { 0 => 6, 2 => 12, 3 => 24, _ => 8 };
            _settings.Save();
        };
        panel.Children.Add(MakeCard("\uE80A", SL("document.cards.density.title"),
            SL("document.cards.density.desc"), dpi));

        var autoToggle = MakeToggle(_settings.AutoDocSize);
        var autoSizePwLl = MakeInfoRadio(
            SL("document.radio.pwll.label"),
            SL("document.radio.pwll.desc"),
            _settings.AutoDocSizeMode == 0);
        var autoSizeElems = MakeInfoRadio(
            SL("document.radio.elems.label"),
            SL("document.radio.elems.desc"),
            _settings.AutoDocSizeMode == 1);
        var autoSizeModes = new StackPanel
        {
            Spacing = 6,
            Visibility = _settings.AutoDocSize ? Visibility.Visible : Visibility.Collapsed,
        };
        autoSizeModes.Children.Add(autoSizePwLl);
        autoSizeModes.Children.Add(autoSizeElems);
        autoToggle.Toggled += (_, _) =>
        {
            _settings.AutoDocSize = autoToggle.IsOn; _settings.Save();
            autoSizeModes.Visibility = autoToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            UpdateSizeBoxLocks();
            if (autoToggle.IsOn) RefreshPreview(SizeUpdate.DocumentLoaded);
        };
        void ApplyAutoMode(int mode)
        {
            _settings.AutoDocSizeMode = mode; _settings.Save();
            UpdateSizeBoxLocks(); RefreshPreview(SizeUpdate.DocumentLoaded);
        }
        autoSizePwLl.Checked  += (_, _) => ApplyAutoMode(0);
        autoSizeElems.Checked += (_, _) => ApplyAutoMode(1);
        panel.Children.Add(MakeCard("\uE740", SL("document.cards.autoSize.title"),
            SL("document.cards.autoSize.desc"), autoToggle, expanded: autoSizeModes));

        // New-document size: ask each time, or use a fixed default size.
        var newSizeMode = new ComboBox { MinWidth = 200,
            ItemsSource = SA("document.opt.newDocMode"),
            SelectedIndex = _settings.NewDocSizeMode == 1 ? 1 : 0 };
        var newSizeUnit = UnitConverter.UnitLabel(_settings.Unit);
        var ndWidth = new TextBox { MinWidth = 90,
            Text = UnitConverter.FormatLength(UnitConverter.FromMillimeters(_settings.NewDocWidthMm, _settings.Unit)) };
        var ndHeight = new TextBox { MinWidth = 90,
            Text = UnitConverter.FormatLength(UnitConverter.FromMillimeters(_settings.NewDocHeightMm, _settings.Unit)) };
        var ndRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Visibility = _settings.NewDocSizeMode == 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        ndRow.Children.Add(ndWidth);
        ndRow.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center });
        ndRow.Children.Add(ndHeight);
        ndRow.Children.Add(new TextBlock { Text = newSizeUnit, VerticalAlignment = VerticalAlignment.Center });
        newSizeMode.SelectionChanged += (_, _) =>
        {
            _settings.NewDocSizeMode = newSizeMode.SelectedIndex; _settings.Save();
            ndRow.Visibility = newSizeMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        void SaveNewDocSize()
        {
            if (UnitConverter.TryParseLength(ndWidth.Text, out var w))
                _settings.NewDocWidthMm = UnitConverter.ToMillimeters(w, _settings.Unit);
            if (UnitConverter.TryParseLength(ndHeight.Text, out var h))
                _settings.NewDocHeightMm = UnitConverter.ToMillimeters(h, _settings.Unit);
            _settings.Save();
        }
        ndWidth.LostFocus  += (_, _) => SaveNewDocSize();
        ndHeight.LostFocus += (_, _) => SaveNewDocSize();
        panel.Children.Add(MakeCard("\uE8A5", SL("document.cards.newDocSize.title"),
            SL("document.cards.newDocSize.desc"), newSizeMode, expanded: ndRow));

        return panel;
    }

    private UIElement BuildPrintSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("print"));

        var printer = new ComboBox { MinWidth = 240 };
        printer.Items.Add(SL("print.lbl.lastPrinter"));
        foreach (string item in PrinterComboBox.Items) printer.Items.Add(item);
        printer.SelectedIndex = _settings.DefaultPrinter == "last" ? 0 : Math.Max(0, printer.Items.IndexOf(_settings.DefaultPrinter));
        printer.SelectionChanged += (_, _) =>
        {
            _settings.DefaultPrinter = printer.SelectedIndex <= 0 ? "last" : printer.SelectedItem?.ToString() ?? "last";
            _settings.Save();
        };
        panel.Children.Add(MakeCard("\uE749", SL("print.cards.printer.title"),
            SL("print.cards.printer.desc"), printer));

        return panel;
    }

    private UIElement BuildEditorSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("editor"));

        // This category lays cards on a shared 5-column grid: every row fills the
        // container width, so cards line up across sub-sections. gridCards holds
        // each card with its column span (1, or 2 for the zoom card); rows holds
        // the wrap panels so heights can be equalised per row. Widths are resolved
        // on resize by LayoutEditorGrid (registered at the end of this method).
        const double gap = 10;
        var gridCards = new List<(FrameworkElement Card, int Span)>();
        var rows = new List<ToolbarWrapPanel>();

        ToolbarWrapPanel Row(params FrameworkElement[] cards)
        {
            var wrap = new ToolbarWrapPanel { HorizontalSpacing = gap, VerticalSpacing = 10 };
            foreach (var c in cards)
            {
                c.MaxWidth = double.PositiveInfinity;
                c.HorizontalAlignment = HorizontalAlignment.Left;
                gridCards.Add((c, 1));
                wrap.Children.Add(c);
            }
            rows.Add(wrap);
            return wrap;
        }

        // ── Éditeur ─────────────────────────────────────────────────────────
        var fontSize = new NumberBox
        {
            Value = _settings.EditorFontSize, Minimum = 8, Maximum = 40, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 96,
        };
        fontSize.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(fontSize.Value)) return;
            _settings.EditorFontSize = (int)Math.Clamp(fontSize.Value, 8, 40); _settings.Save();
            ApplyEditorOptions();
        };
        var wrapToggle = MakeToggle(_settings.EditorWordWrap);
        wrapToggle.Toggled += (_, _) => { _settings.EditorWordWrap = wrapToggle.IsOn; _settings.Save(); ApplyEditorOptions(); };
        var minimap = MakeToggle(_settings.EditorMinimap);
        minimap.Toggled += (_, _) => { _settings.EditorMinimap = minimap.IsOn; _settings.Save(); ApplyEditorOptions(); };
        var lineToggle = MakeToggle(_settings.ShowLineNumbers);
        lineToggle.Toggled += (_, _) => SetLineNumbers(lineToggle.IsOn);
        var lowWarn = MakeToggle(_settings.ShowLowWarnings);
        lowWarn.Toggled += (_, _) => { _settings.ShowLowWarnings = lowWarn.IsOn; _settings.Save(); RunStaticAnalysis(); };

        panel.Children.Add(SubHeader(SL("editor.sub.editor")));
        panel.Children.Add(Row(
            MakeCard("\uE8D2", SL("editor.cards.fontSize.title"), SL("editor.cards.fontSize.desc"), fontSize),
            MakeCard("\uE751", SL("editor.cards.wordWrap.title"), SL("editor.cards.wordWrap.desc"), wrapToggle),
            MakeCard("\uE890", SL("editor.cards.minimap.title"), SL("editor.cards.minimap.desc"), minimap),
            MakeCard("\uE7C3", SL("editor.cards.lineNumbers.title"), SL("editor.cards.lineNumbers.desc"), lineToggle),
            MakeCard("\uE7BA", SL("editor.cards.lowWarnings.title"), SL("editor.cards.lowWarnings.desc"), lowWarn)));

        // ── Coloration syntaxique ───────────────────────────────────────────
        void ApplyColors(Windows.UI.Color cmd, Windows.UI.Color prm, Windows.UI.Color txt)
        {
            ZplColorSchemeService.SetColors(Hex(cmd), Hex(prm), Hex(txt));
            ReapplyHighlightColors();
            ScheduleColorPersist();
        }
        var cmdColor = ZplHighlighter.CommandColor;
        var prmColor = ZplHighlighter.ParameterColor;
        var txtColor = ZplHighlighter.TextColor;
        var cmdBtn = MakeColorButton(cmdColor, c => { cmdColor = c; ApplyColors(cmdColor, prmColor, txtColor); });
        var prmBtn = MakeColorButton(prmColor, c => { prmColor = c; ApplyColors(cmdColor, prmColor, txtColor); });
        var txtBtn = MakeColorButton(txtColor, c => { txtColor = c; ApplyColors(cmdColor, prmColor, txtColor); });

        // Opens the user color-scheme JSON in the OS default editor for .json.
        // Icon-only button (open-in-new-window) to keep the card compact.
        var editSchemeBtn = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 16 },
        };
        ToolTipService.SetToolTip(editSchemeBtn, SL("editor.cards.editScheme.tooltip"));
        editSchemeBtn.Click += (_, _) => OpenUserColorSchemeFile();

        panel.Children.Add(SubHeader(SL("editor.sub.coloring")));
        panel.Children.Add(Row(
            MakeCard("\uE790", SL("editor.cards.colorCmd.title"), SL("editor.cards.colorCmd.desc"), cmdBtn),
            MakeCard("\uE790", SL("editor.cards.colorParam.title"), SL("editor.cards.colorParam.desc"), prmBtn),
            MakeCard("\uE790", SL("editor.cards.colorText.title"), SL("editor.cards.colorText.desc"), txtBtn),
            MakeCard("\uE8A5", SL("editor.cards.editScheme.title"), SL("editor.cards.editScheme.desc"), editSchemeBtn)));

        // ── Aperçu ──────────────────────────────────────────────────────────
        var gridToggle = MakeToggle(_settings.ShowPreviewGrid);
        gridToggle.Toggled += (_, _) => SetPreviewGrid(gridToggle.IsOn);
        var gridSpacing = new NumberBox
        {
            Value = _settings.PreviewGridSpacing, Minimum = 0.1, Maximum = 1000, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 96,
        };
        gridSpacing.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(gridSpacing.Value)) return;
            _settings.PreviewGridSpacing = Math.Clamp(gridSpacing.Value, 0.1, 1000); _settings.Save();
            DrawPreviewGrid();
        };
        // Unit selector (px/mm/cm/inches), stacked under the spin box so the card
        // stays narrow. Changing it keeps the physical spacing constant (converts
        // via the real-size scale).
        var gridUnit = new ComboBox { MinWidth = 96, ItemsSource = SA("editor.opt.gridUnit") };
        gridUnit.SelectedIndex = Math.Max(0, Array.IndexOf(GridUnitCodes, _settings.PreviewGridSpacingUnit));
        gridUnit.SelectionChanged += (_, _) =>
        {
            var oldUnit = _settings.PreviewGridSpacingUnit;
            var newUnit = GridUnitCodes[Math.Clamp(gridUnit.SelectedIndex, 0, GridUnitCodes.Length - 1)];
            if (newUnit == oldUnit) return;
            // Keep the same on-screen spacing: value_px / DIPs-per-new-unit.
            double px = gridSpacing.Value * GridUnitPx(oldUnit);
            _settings.PreviewGridSpacingUnit = newUnit; _settings.Save();
            gridSpacing.Value = Math.Round(px / GridUnitPx(newUnit), 2); // fires ValueChanged → save + redraw
        };
        var gridSpacingRow = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        gridSpacingRow.Children.Add(gridSpacing);
        gridSpacingRow.Children.Add(gridUnit);
        var rotation = new ComboBox { MinWidth = 90, ItemsSource = new[] { "0°", "90°", "180°", "270°" } };
        rotation.SelectedIndex = ((int)Math.Round(_settings.DefaultRotation / 90.0)) % 4;
        rotation.SelectionChanged += (_, _) => { _settings.DefaultRotation = rotation.SelectedIndex * 90; _settings.Save(); };

        var captionToggle = MakeToggle(_settings.ShowPreviewCaption);
        captionToggle.Toggled += (_, _) => { _settings.ShowPreviewCaption = captionToggle.IsOn; _settings.Save(); ApplyPreviewCaptionVisibility(); };

        // Grid colour: a compact swatch button (the card control) opens a FLYOUT
        // popup holding the Default/Custom choice and the colour picker \u2014 so the
        // card never grows. The swatch always shows the effective grid colour.
        var gridColorSwatch = new Border
        {
            Width = 40, Height = 24, CornerRadius = new CornerRadius(4),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(EffectiveGridColor()),
        };
        void RefreshGridSwatch() => ((SolidColorBrush)gridColorSwatch.Background).Color = EffectiveGridColor();

        var gridColorMode = new ComboBox { MinWidth = 150, ItemsSource = SA("editor.opt.gridColor") };
        gridColorMode.SelectedIndex = _settings.UseCustomGridColor ? 1 : 0;
        var gridColorPicker = new ColorPicker
        {
            IsAlphaEnabled = true,
            IsMoreButtonVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Color = ZplColorSchemeService.ParseHexColor(_settings.CustomGridColor, Windows.UI.Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            Visibility = _settings.UseCustomGridColor ? Visibility.Visible : Visibility.Collapsed,
        };
        gridColorMode.SelectionChanged += (_, _) =>
        {
            _settings.UseCustomGridColor = gridColorMode.SelectedIndex == 1; _settings.Save();
            gridColorPicker.Visibility = _settings.UseCustomGridColor ? Visibility.Visible : Visibility.Collapsed;
            DrawPreviewGrid(); RefreshGridSwatch();
        };
        gridColorPicker.ColorChanged += (_, args) =>
        {
            var c = args.NewColor;
            _settings.CustomGridColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"; _settings.Save();
            if (_settings.UseCustomGridColor) { DrawPreviewGrid(); RefreshGridSwatch(); }
        };
        var gridColorFlyoutPanel = new StackPanel { Spacing = 12, MinWidth = 260 };
        gridColorFlyoutPanel.Children.Add(gridColorMode);
        gridColorFlyoutPanel.Children.Add(gridColorPicker);
        var gridColorBtn = new Button
        {
            Padding = new Thickness(4),
            Content = gridColorSwatch,
            Flyout = new Flyout { Content = gridColorFlyoutPanel },
        };

        panel.Children.Add(SubHeader(SL("editor.sub.preview")));
        panel.Children.Add(Row(
            MakeCard("\uE80A", SL("editor.cards.grid.title"), SL("editor.cards.grid.desc"), gridToggle),
            MakeCard("\uE80A", SL("editor.cards.gridSpacing.title"), SL("editor.cards.gridSpacing.desc"), gridSpacingRow),
            MakeCard("\uE790", SL("editor.cards.gridColor.title"), SL("editor.cards.gridColor.desc"), gridColorBtn),
            MakeCard("\uE7AD", SL("editor.cards.rotation.title"), SL("editor.cards.rotation.desc"), rotation),
            MakeCard("\uE7B3", SL("editor.cards.previewCaption.title"), SL("editor.cards.previewCaption.desc"), captionToggle)));

        // \u2500\u2500 R\u00E8gles \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        var rulerH = MakeToggle(_settings.ShowRulerHorizontal);
        rulerH.Toggled += (_, _) => { _settings.ShowRulerHorizontal = rulerH.IsOn; _settings.Save(); ApplyRulerVisibility(); };
        var rulerV = MakeToggle(_settings.ShowRulerVertical);
        rulerV.Toggled += (_, _) => { _settings.ShowRulerVertical = rulerV.IsOn; _settings.Save(); ApplyRulerVisibility(); };

        var rulerUnit = new ComboBox { MinWidth = 96, ItemsSource = SA("editor.opt.gridUnit") };
        rulerUnit.SelectedIndex = Math.Max(0, Array.IndexOf(GridUnitCodes, _settings.RulerUnit));
        rulerUnit.SelectionChanged += (_, _) =>
        {
            _settings.RulerUnit = GridUnitCodes[Math.Clamp(rulerUnit.SelectedIndex, 0, GridUnitCodes.Length - 1)];
            _settings.Save(); DrawRulers();
        };

        var rulerSubs = new NumberBox
        {
            Value = _settings.RulerSubdivisions, Minimum = 1, Maximum = 20, SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 96,
        };
        rulerSubs.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(rulerSubs.Value)) return;
            _settings.RulerSubdivisions = (int)Math.Clamp(rulerSubs.Value, 1, 20); _settings.Save(); DrawRulers();
        };

        var rulerSize = new ComboBox { MinWidth = 96, ItemsSource = SA("editor.opt.rulerSize") };
        rulerSize.SelectedIndex = Math.Clamp(_settings.RulerBandSize, 0, 2);
        rulerSize.SelectionChanged += (_, _) =>
        {
            _settings.RulerBandSize = Math.Clamp(rulerSize.SelectedIndex, 0, 2); _settings.Save(); ApplyRulerVisibility();
        };

        panel.Children.Add(SubHeader(SL("editor.sub.rulers")));
        panel.Children.Add(Row(
            MakeCard("\uE9E9", SL("editor.cards.rulerH.title"), SL("editor.cards.rulerH.desc"), rulerH),
            MakeCard("\uE9E9", SL("editor.cards.rulerV.title"), SL("editor.cards.rulerV.desc"), rulerV),
            MakeCard("\uF000", SL("editor.cards.rulerUnit.title"), SL("editor.cards.rulerUnit.desc"), rulerUnit),
            MakeCard("\uE9E9", SL("editor.cards.rulerSubs.title"), SL("editor.cards.rulerSubs.desc"), rulerSubs),
            MakeCard("\uE799", SL("editor.cards.rulerSize.title"), SL("editor.cards.rulerSize.desc"), rulerSize)));

        // The slider fills the whole card width (card padding keeps the side
        // margins); the % label sits at its right.
        var zoomSlider = new Slider { Minimum = 0, Maximum = 400, StepFrequency = 5, Value = _settings.DefaultZoom, HorizontalAlignment = HorizontalAlignment.Stretch };
        var zoomLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 80, Margin = new Thickness(12, 0, 0, 0) };
        void UpdZoomLabel() => zoomLabel.Text = _settings.DefaultZoom <= 0 ? SL("editor.lbl.fitWindow") : $"{(int)_settings.DefaultZoom} %";
        UpdZoomLabel();
        zoomSlider.ValueChanged += (_, e) =>
        {
            _settings.DefaultZoom = e.NewValue; _settings.Save(); UpdZoomLabel();
            // Live preview of the new default: drop the current document's own
            // zoom so the slider actually shows its effect.
            if (_activeTab is not null) _activeTab.ZoomPercent = null;
            ApplyDefaultZoom();
        };
        var zoomRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        zoomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(zoomSlider, 0); zoomRow.Children.Add(zoomSlider);
        Grid.SetColumn(zoomLabel, 1);  zoomRow.Children.Add(zoomLabel);
        var zoomCard = MakeCard("\uE71E", SL("editor.cards.defaultZoom.title"),
            SL("editor.cards.defaultZoom.desc"), null, expanded: zoomRow);
        // MakeCard caps width at 620; clear it so the two-column span isn't clipped.
        zoomCard.MaxWidth = double.PositiveInfinity;
        zoomCard.HorizontalAlignment = HorizontalAlignment.Left;
        // Spans two grid columns (a card + the gap between two cards).
        var zoomWrap = new ToolbarWrapPanel();
        zoomWrap.Children.Add(zoomCard);
        gridCards.Add((zoomCard, 2));
        rows.Add(zoomWrap);
        panel.Children.Add(zoomWrap);

        // ── Disposition ─────────────────────────────────────────────────────
        var swap = MakeToggle(_settings.SwapEditorPreview);
        swap.Toggled += (_, _) =>
        {
            _settings.SwapEditorPreview = swap.IsOn; _settings.Save();
            ApplyEditorLayout();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyDefaultZoom);
        };
        panel.Children.Add(SubHeader(SL("editor.sub.layout")));
        panel.Children.Add(Row(MakeCard("\uE8AB", SL("editor.cards.swap.title"),
            SL("editor.cards.swap.desc"), swap)));

        // Resolve card widths on a shared grid that fills the container width.
        // Targets 5 columns, but drops to fewer when the window is too narrow to
        // keep a card readable (MinCol) \u2014 otherwise the labels would wrap down to
        // one glyph per line. A span-2 card takes two columns plus the gap between
        // them (clamped to the column count). Card heights are equalised per row.
        // Debounced so the UpdateLayout pass doesn't re-enter through SizeChanged.
        const double MinCol = 240;
        bool pending = false;
        double lastW = -1;
        void LayoutEditorGrid(bool force = false)
        {
            if (pending) return;
            pending = true;
            panel.DispatcherQueue.TryEnqueue(() =>
            {
                pending = false;
                double w = panel.ActualWidth;
                if (w <= 0) return;
                // Only react to WIDTH changes. Equalising MinHeight below changes the
                // panel's height, which re-fires SizeChanged; without this guard the
                // two feed back into each other and the view scrolls in a loop.
                if (!force && Math.Abs(w - lastW) < 0.5) return;
                lastW = w;
                int cols = Math.Clamp((int)Math.Floor((w + gap) / (MinCol + gap)), 1, 5);
                double col = Math.Floor((w - (cols - 1) * gap) / cols);
                if (col <= 1) return;
                foreach (var (card, span) in gridCards)
                {
                    int s = Math.Min(span, cols);
                    card.Width = s == 2 ? 2 * col + gap : col;
                }

                foreach (var row in rows)
                    foreach (FrameworkElement c in row.Children) c.MinHeight = 0;
                panel.UpdateLayout();
                foreach (var row in rows)
                {
                    double max = 0;
                    foreach (FrameworkElement c in row.Children) if (c.ActualHeight > max) max = c.ActualHeight;
                    if (max <= 0) continue;
                    foreach (FrameworkElement c in row.Children) c.MinHeight = max;
                }
            });
        }
        panel.Loaded += (_, _) => LayoutEditorGrid(force: true);
        panel.SizeChanged += (_, _) => LayoutEditorGrid();

        return panel;
    }

    private static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private DispatcherTimer? _colorPersistTimer;

    // Debounces the (large) color-scheme JSON write while dragging a color picker.
    private void ScheduleColorPersist()
    {
        if (_colorPersistTimer is null)
        {
            _colorPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _colorPersistTimer.Tick += (_, _) => { _colorPersistTimer!.Stop(); ZplColorSchemeService.PersistColors(); };
        }
        _colorPersistTimer.Stop();
        _colorPersistTimer.Start();
    }

    private void SetLineNumbers(bool on)
    {
        _settings.ShowLineNumbers = on; _settings.Save();
        PostToEditor("{\"type\":\"setLineNumbers\",\"show\":" + (on ? "true" : "false") + "}");
    }

    private void SetPreviewGrid(bool on)
    {
        _settings.ShowPreviewGrid = on; _settings.Save();
        DrawPreviewGrid();
    }

    private UIElement BuildAppearanceSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("appearance"));

        var theme = new ComboBox { MinWidth = 200,
            ItemsSource = SA("appearance.opt.theme"), SelectedIndex = (int)_settings.Theme };
        theme.SelectionChanged += (_, _) =>
        {
            _settings.Theme = (ThemePreference)Math.Max(0, theme.SelectedIndex); _settings.Save();
            Root.RequestedTheme = _settings.ToElementTheme();
            (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.SetTheme(_settings.ToElementTheme());
            // Dark ↔ Dark&LightPreview keep the same chrome theme, so ActualThemeChanged
            // may not fire — refresh the preview background explicitly.
            ApplyPreviewTheme();
        };
        panel.Children.Add(MakeCard("\uE706", SL("appearance.cards.theme.title"),
            SL("appearance.cards.theme.desc"), theme));

        var accentMode = new ComboBox { MinWidth = 160,
            ItemsSource = SA("appearance.opt.accent"), SelectedIndex = _settings.UseSystemAccent ? 0 : 1 };
        var accentPicker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = true,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Color = ZplColorSchemeService.ParseHexColor(_settings.CustomAccent, Microsoft.UI.Colors.DodgerBlue),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var accentContainer = new StackPanel
        {
            Visibility = _settings.UseSystemAccent ? Visibility.Collapsed : Visibility.Visible,
        };
        accentContainer.Children.Add(accentPicker);
        accentMode.SelectionChanged += (_, _) =>
        {
            bool custom = accentMode.SelectedIndex == 1;
            accentContainer.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            _settings.UseSystemAccent = !custom; _settings.Save();
            ApplyAccentFromSettings();
        };
        accentPicker.ColorChanged += (_, args) =>
        {
            _settings.CustomAccent = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            _settings.Save();
            if (!_settings.UseSystemAccent) ScheduleAccentApply();
        };
        panel.Children.Add(MakeCard("\uE790", SL("appearance.cards.accent.title"),
            SL("appearance.cards.accent.desc"), accentMode, expanded: accentContainer));

        // The language list is discovered from the languages folder (code +
        // displayName), so files dropped in later show up here too.
        var langs = LocalizationService.AvailableLanguages();
        var language = new ComboBox { MinWidth = 160, ItemsSource = langs.Select(l => l.DisplayName).ToList() };
        int curIdx = langs.FindIndex(l => string.Equals(l.Code, _settings.Language, StringComparison.OrdinalIgnoreCase));
        language.SelectedIndex = curIdx < 0 ? 0 : curIdx;
        language.SelectionChanged += (_, _) =>
        {
            if (_revertingLanguage || language.SelectedIndex < 0 || language.SelectedIndex >= langs.Count) return;
            var sel = langs[language.SelectedIndex];
            // Listed but its JSON is broken \u2192 refuse, revert, and offer JSONLint.
            if (!LocalizationService.IsValidLanguageFile(sel.Code))
            {
                int back = langs.FindIndex(l => string.Equals(l.Code, _settings.Language, StringComparison.OrdinalIgnoreCase));
                _revertingLanguage = true;
                language.SelectedIndex = back;
                _revertingLanguage = false;
                _ = ShowInvalidLanguageDialogAsync(sel.Code);
                return;
            }
            _settings.Language = sel.Code; _settings.Save();
            LocalizationService.SetLanguage(_settings.Language);
            ApplyLanguageLive();
        };

        // Info bubble: what a language file needs to appear + the basedOn override.
        var langInfo = new FontIcon
        {
            Glyph = "", FontSize = 16, Opacity = 0.7,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(langInfo, new ToolTip
        {
            Content = new TextBlock { Text = SL("appearance.languageInfo"), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 },
        });
        var langRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        langRow.Children.Add(langInfo);
        langRow.Children.Add(language);
        panel.Children.Add(MakeCard("\uE774", LocalizationService.Get("settings.appearance.languageLabel"),
            LocalizationService.Get("settings.appearance.languageDesc"), langRow));

        return panel;
    }

    // ── Toolbar designer (drag & drop) ───────────────────────────────────────

    // The designer lists hold the chip visuals directly (ToolbarChipView builds
    // its own content and carries its ToolbarChip): no template machinery, so
    // nothing can override what is displayed.
    private UIElement BuildToolbarDesignerSettings()
    {
        var panel = SettingsPanel();

        // Header row: the title/subtitle on the left, the "Réinitialiser" button
        // vertically centred on the right (no empty gap above the card).
        var designer = BuildToolbarDesigner(out var resetBtn);
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var header = LocalizedSettingsHeader("toolbar");
        header.Margin = new Thickness(0);
        header.VerticalAlignment = VerticalAlignment.Center;
        resetBtn.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(header, 0);
        Grid.SetColumn(resetBtn, 1);
        headerRow.Children.Add(header);
        headerRow.Children.Add(resetBtn);
        panel.Children.Add(headerRow);

        panel.Children.Add(designer);
        return panel;
    }

    // The toolbar designer: a 3-row drag-and-drop card bound to the persisted
    // toolbar layout. The "Réinitialiser" button is returned via resetBtn so the
    // caller can place it in the section header.
    private UIElement BuildToolbarDesigner(out Button resetBtn)
    {
        var section = new StackPanel();
        var rows = ToolbarItems.NormalizeRows(_settings.ToolbarRows);
        var designerRows = new System.Collections.ObjectModel.ObservableCollection<ToolbarChipView>[ToolbarItems.RowCount];

        // Cross-list drops fire several CollectionChanged events; coalesce into
        // one save that persists this designer's layout and rebuilds the live
        // toolbar (only when it is the one being displayed).
        bool saveQueued = false;
        void QueueSave()
        {
            if (saveQueued) return;
            saveQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                saveQueued = false;
                var outRows = new List<List<string>>();
                foreach (var col in designerRows)
                {
                    var row = new List<string>();
                    foreach (var view in col) row.Add(view.Chip.Id);
                    outRows.Add(row);
                }
                _settings.ToolbarRows = outRows;
                _settings.Save();
                RebuildToolbar();
            });
        }

        resetBtn = new Button
        {
            Content = SL("general.lbl.reset"),
            FontSize = 12,
            Padding = new Thickness(10, 5, 10, 5),
        };
        resetBtn.Click += (_, _) =>
        {
            foreach (var c in designerRows) c.Clear();
            foreach (var id in ToolbarItems.AllIds) designerRows[0].Add(new ToolbarChipView(MakeChip(id)));
            // CollectionChanged → QueueSave persists and rebuilds the toolbar.
        };

        // A single card holding the three rows, separated by thin dividers.
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

        for (int i = 0; i < ToolbarItems.RowCount; i++)
        {
            int rowIndex = i;
            var col = new System.Collections.ObjectModel.ObservableCollection<ToolbarChipView>(
                rows[i].ConvertAll(id => new ToolbarChipView(MakeChip(id))));
            col.CollectionChanged += (_, _) => QueueSave();
            designerRows[i] = col;

            var lv = new ChipListView
            {
                ItemsSource = col,
                SelectionMode = ListViewSelectionMode.None,
                CanReorderItems = false, // cross-row moves are handled manually below
                CanDragItems = true,
                AllowDrop = true,
                MinHeight = 52,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                ItemsPanel = HorizontalItemsPanel(),
                ItemContainerStyle = ChipContainerStyle(),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Padding = new Thickness(4),
            };
            ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollMode(lv, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);

            lv.DragItemsStarting += (_, e) =>
            {
                if (e.Items.Count > 0 && e.Items[0] is ToolbarChipView view)
                {
                    e.Data.SetText(view.Chip.Id);
                    e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                }
            };
            lv.DragOver += (_, e) =>
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            lv.Drop += (s, e) => OnDesignerDrop(designerRows, rowIndex, (ListView)s, e);

            // "Ligne N" label on the left + the row's list.
            var rowGrid = new Grid { MinHeight = 52 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = $"Ligne {i + 1}",
                FontSize = 12,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 8, 0),
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(lv, 1);
            rowGrid.Children.Add(label);
            rowGrid.Children.Add(lv);
            stack.Children.Add(rowGrid);

            if (i < ToolbarItems.RowCount - 1)
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
        }

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = stack,
        };
        section.Children.Add(card);
        return section;
    }

    // Manual drop: moves the dragged group into the target row at the position
    // under the pointer. Works within a row and across rows of the SAME designer
    private async void OnDesignerDrop(
        System.Collections.ObjectModel.ObservableCollection<ToolbarChipView>[] designerRows,
        int targetRow, ListView lv, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;

        var def = e.GetDeferral();
        try
        {
            var id = await e.DataView.GetTextAsync();
            if (string.IsNullOrEmpty(id)) return;

            var target = designerRows[targetRow];

            // Insertion index from the horizontal pointer position (before removal).
            var pos = e.GetPosition(lv);
            int insert = target.Count;
            for (int i = 0; i < target.Count; i++)
            {
                if (lv.ContainerFromIndex(i) is FrameworkElement c)
                {
                    var origin = c.TransformToVisual(lv).TransformPoint(new Windows.Foundation.Point(0, 0));
                    if (pos.X < origin.X + c.ActualWidth / 2) { insert = i; break; }
                }
            }

            // Remove the group from wherever it currently sits.
            int srcRow = -1, srcIdx = -1;
            for (int r = 0; r < designerRows.Length && srcRow < 0; r++)
                for (int i = 0; i < designerRows[r].Count; i++)
                    if (designerRows[r][i].Chip.Id == id) { srcRow = r; srcIdx = i; break; }
            if (srcRow < 0) return;
            designerRows[srcRow].RemoveAt(srcIdx);

            if (srcRow == targetRow && srcIdx < insert) insert--;
            insert = System.Math.Clamp(insert, 0, target.Count);
            target.Insert(insert, new ToolbarChipView(MakeChip(id)));
        }
        finally
        {
            def.Complete();
        }
    }

    private ToolbarChip MakeChip(string id)
    {
        foreach (var d in ToolbarItemDefs)
            if (d.Id == id) return new ToolbarChip { Id = id, Label = d.Label, Glyph = d.Glyph, Buttons = d.Buttons };
        return new ToolbarChip { Id = id, Label = id, Glyph = "" };
    }

    // Items flow left-to-right and hug their content (no uniform cell sizing).
    private static ItemsPanelTemplate HorizontalItemsPanel() =>
        (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<ItemsStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");

    // Strips the default ListViewItem padding/min-size so the chip border hugs
    // its content instead of every item sharing one fixed size.
    private static Style ChipContainerStyle() =>
        (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'>" +
            "<Setter Property='MinWidth' Value='0'/>" +
            "<Setter Property='MinHeight' Value='0'/>" +
            "<Setter Property='Padding' Value='0'/>" +
            "<Setter Property='Margin' Value='3'/>" +
            "<Setter Property='HorizontalContentAlignment' Value='Left'/>" +
            "<Setter Property='VerticalContentAlignment' Value='Center'/></Style>");



    private UIElement BuildScreenSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("screen"));
        var host = new StackPanel { Spacing = 4 };
        panel.Children.Add(host);
        _ = PopulateScreenSettingsAsync(host);
        return panel;
    }

    private const string ScreenInfoText =
        "La taille d'un ordinateur portable se situe souvent entre 13\" et 17\".\n" +
        "La taille d'un écran de bureau se situe souvent entre 21\" et 27\".\n" +
        "Vous pouvez mesurer la diagonale de la partie visible de votre écran (la zone qui affiche l'image, sans le cadre) afin de renseigner la taille de votre écran.";

    private async Task PopulateScreenSettingsAsync(StackPanel host)
    {
        var monitors = await DisplayMetrics.EnumerateMonitorsAsync();
        host.Children.Clear();
        if (monitors.Count == 0)
        {
            host.Children.Add(new TextBlock { Text = SL("screen.lbl.noMonitor"), Opacity = 0.7 });
            return;
        }

        var currentId = DisplayMetrics.GetMonitorInterfaceId(GetWindowHandle());
        var selector = new ComboBox { MinWidth = 240, ItemsSource = monitors.Select(m => m.FriendlyName).ToList() };
        int curIdx = monitors.FindIndex(m => m.InterfaceId == currentId);
        selector.SelectedIndex = curIdx < 0 ? 0 : curIdx;

        if (monitors.Count > 1)
        {
            var identify = new Button { Content = SL("screen.lbl.identify") };
            identify.Click += async (_, _) => await IdentifyMonitorAsync(monitors[selector.SelectedIndex]);
            var selRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            selRow.Children.Add(selector);
            selRow.Children.Add(identify);
            host.Children.Add(MakeCard("\uE7F4", SL("screen.cards.selector.title"), SL("screen.cards.selector.desc"), selRow));
        }

        var sizeHost = new StackPanel { Spacing = 4 };
        host.Children.Add(sizeHost);

        void BuildSizeCard()
        {
            sizeHost.Children.Clear();
            var mon = monitors[selector.SelectedIndex];
            bool edid = mon.EdidDiagonalInches is not null;

            var numberBox = new TextBox { MinWidth = 84, InputScope = MakeNumberScope() };
            var unit = new ComboBox { MinWidth = 90, ItemsSource = SA("screen.opt.unit"), SelectedIndex = 0 };

            double initial =
                edid ? mon.EdidDiagonalInches!.Value
                : _settings.ManualScreenSizesInches.TryGetValue(mon.InterfaceId, out var mi) ? mi : 0;
            numberBox.Text = initial > 0 ? initial.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) : "";

            if (edid)
            {
                numberBox.IsEnabled = false;
                unit.IsEnabled = false;
            }

            // Limit input to a number with at most 2 decimals.
            numberBox.TextChanging += (s, _) =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(s.Text, @"^\d*([.,]\d{0,2})?");
                if (m.Value != s.Text)
                {
                    var pos = s.SelectionStart;
                    s.Text = m.Value;
                    s.SelectionStart = Math.Min(pos, s.Text.Length);
                }
            };

            void Update()
            {
                if (edid) return; // read-only for auto-detected screens
                if (double.TryParse((numberBox.Text ?? "").Replace(',', '.'),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var val) && val > 0)
                {
                    // Trust the entered value (converted to inches) — no snapping.
                    var inches = unit.SelectedIndex == 1 ? val / 2.54 : val;
                    _settings.ManualScreenSizesInches[mon.InterfaceId] = inches;
                    _settings.Save();
                    if (mon.InterfaceId == currentId) RefreshRealSizeScale();
                }
            }
            numberBox.TextChanged += (_, _) => Update();
            unit.SelectionChanged += (_, _) => Update();

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(numberBox);
            row.Children.Add(unit);
            row.Children.Add(MakeInfoIcon(SL("screen.lbl.info")));
            sizeHost.Children.Add(MakeCard("\uE1D3", SL("screen.cards.size.title"),
                edid ? SL("screen.cards.size.descEdid") : SL("screen.cards.size.descManual"), row));
        }

        selector.SelectionChanged += (_, _) => BuildSizeCard();
        BuildSizeCard();
    }

    private static InputScope MakeNumberScope()
    {
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.Number));
        return scope;
    }

    // Small info (ⓘ) icon with an explanatory tooltip.
    private static FontIcon MakeInfoIcon(string tooltip)
    {
        var icon = new FontIcon { Glyph = "\uE946", FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(icon, new ToolTip
        {
            Content = new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 },
        });
        return icon;
    }

    // The single identify marker window currently on screen (if any), so repeated
    // "Identifier" clicks replace it instead of stacking several markers.
    private Window? _identifyWindow;

    // Flashes a large marker on the given monitor only, for a couple of seconds.
    // Any marker already showing is closed first, so there is only ever one.
    private async Task IdentifyMonitorAsync(MonitorInfo m)
    {
        // Replace an existing marker (from a previous click) before showing a new one.
        if (_identifyWindow is not null)
        {
            try { _identifyWindow.Close(); } catch { }
            _identifyWindow = null;
        }

        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var win = new Window();
        _identifyWindow = win;
        var grid = new Grid { Background = accent };
        grid.Children.Add(new FontIcon
        {
            Glyph = "\uE73E",   // check mark marker
            FontSize = 120,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        win.Content = grid;

        var aw = win.AppWindow;
        if (aw.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
        }
        aw.IsShownInSwitchers = false;
        const int w = 240, h = 180;
        aw.MoveAndResize(new Windows.Graphics.RectInt32(m.PosX + m.ResW / 2 - w / 2, m.PosY + m.ResH / 2 - h / 2, w, h));
        win.Activate();

        await Task.Delay(2000);
        // Only close if this call still owns the marker (a newer click may have
        // replaced it, and that newer window must keep its own 2 s lifetime).
        if (ReferenceEquals(_identifyWindow, win))
        {
            try { win.Close(); } catch { }
            _identifyWindow = null;
        }
    }


    private UIElement BuildVirtualPrinterSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("virtualPrinter"));

        var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        var installBtn = new Button();
        var uninstallBtn = new Button { Content = SL("virtualPrinter.lbl.uninstall") };
        void Refresh()
        {
            bool ok = VirtualPrinterService.IsInstalled();
            statusText.Text = ok ? SL("virtualPrinter.lbl.installed") : SL("virtualPrinter.lbl.notInstalled");
            installBtn.Content = ok ? SL("virtualPrinter.lbl.reinstall") : SL("virtualPrinter.lbl.install");
            uninstallBtn.IsEnabled = ok;
        }
        installBtn.Click += async (_, _) =>
        {
            installBtn.IsEnabled = false;
            // Reinstall does removal + install in one elevated script (single UAC);
            // a first-time install just installs.
            var r = VirtualPrinterService.IsInstalled()
                ? await Task.Run(VirtualPrinterService.Reinstall)
                : await Task.Run(VirtualPrinterService.EnsureInstalled);
            installBtn.IsEnabled = true;
            Refresh();
            if (!r.Ok) await ShowMessageAsync(SL("virtualPrinter.lbl.dlgTitle"), r.Error ?? SL("virtualPrinter.lbl.opFailed"));
        };
        uninstallBtn.Click += async (_, _) =>
        {
            uninstallBtn.IsEnabled = false;
            var r = await Task.Run(VirtualPrinterService.Uninstall);
            Refresh();
            if (!r.Ok) await ShowMessageAsync("Imprimante virtuelle", r.Error ?? "Opération échouée.");
        };
        Refresh();

        panel.Children.Add(MakeCard("\uE772", SL("virtualPrinter.cards.status.title"), SL("virtualPrinter.cards.status.desc"), statusText));
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        btnRow.Children.Add(installBtn);
        btnRow.Children.Add(uninstallBtn);
        panel.Children.Add(MakeCard("\uE710", SL("virtualPrinter.cards.manage.title"), SL("virtualPrinter.cards.manage.desc"), btnRow));
        return panel;
    }

    private UIElement BuildGeneralSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("general"));

        var confirm = MakeToggle(_settings.ConfirmBeforePrint);
        confirm.Toggled += (_, _) => { _settings.ConfirmBeforePrint = confirm.IsOn; _settings.Save(); };
        panel.Children.Add(MakeCard("\uE749", SL("general.cards.confirmPrint.title"),
            SL("general.cards.confirmPrint.desc"), confirm));

        var reopen = MakeToggle(_settings.ReopenLastFile);
        reopen.Toggled += (_, _) => { _settings.ReopenLastFile = reopen.IsOn; _settings.Save(); };
        panel.Children.Add(MakeCard("\uED25", SL("general.cards.reopen.title"),
            SL("general.cards.reopen.desc"), reopen));

        var showPath = MakeToggle(_settings.ShowFilePathInTitle);
        showPath.Toggled += (_, _) => { _settings.ShowFilePathInTitle = showPath.IsOn; _settings.Save(); UpdateDocumentTitle(); };
        panel.Children.Add(MakeCard("\uE7C3", SL("general.cards.showPath.title"),
            SL("general.cards.showPath.desc"), showPath));

        var tabPath = MakeToggle(_settings.ShowPathInTabTooltip);
        tabPath.Toggled += (_, _) => { _settings.ShowPathInTabTooltip = tabPath.IsOn; _settings.Save(); RefreshAllTabHeaders(); };
        panel.Children.Add(MakeCard("\uE8A1", SL("general.cards.tabPath.title"),
            SL("general.cards.tabPath.desc"), tabPath));

        // PNG export quality: ask each time, or use a fixed default (reveals a slider).
        var pngMode = new ComboBox { MinWidth = 200,
            ItemsSource = SA("general.opt.pngMode"),
            SelectedIndex = _settings.PngExportMode == "default" ? 1 : 0 };
        var pngSlider = new Slider
        {
            Minimum = 1, Maximum = 5, StepFrequency = 1, TickFrequency = 1,
            TickPlacement = Microsoft.UI.Xaml.Controls.Primitives.TickPlacement.Outside,
            Value = Math.Clamp(_settings.PngQualityStep, 1, 5),
            IsThumbToolTipEnabled = false, Width = 260,
        };
        var pngQualLabel = new TextBlock { Opacity = 0.7, FontSize = 12 };
        void UpdPngQualLabel() => pngQualLabel.Text = SL($"png.quality.step{(int)Math.Round(pngSlider.Value)}");
        var pngRow = new StackPanel
        {
            Spacing = 4,
            Visibility = _settings.PngExportMode == "default" ? Visibility.Visible : Visibility.Collapsed,
        };
        pngRow.Children.Add(pngSlider);
        pngRow.Children.Add(pngQualLabel);
        pngSlider.ValueChanged += (_, _) =>
        {
            _settings.PngQualityStep = (int)Math.Round(pngSlider.Value); _settings.Save(); UpdPngQualLabel();
        };
        pngMode.SelectionChanged += (_, _) =>
        {
            _settings.PngExportMode = pngMode.SelectedIndex == 1 ? "default" : "ask"; _settings.Save();
            pngRow.Visibility = pngMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        UpdPngQualLabel();
        panel.Children.Add(MakeCard("\uE91B", SL("general.cards.pngQuality.title"),
            SL("general.cards.pngQuality.desc"), pngMode, expanded: pngRow));

        // Default .zpl file association. Greyed out when already the default.
        var zplBtn = new Button();
        void RefreshZplBtn()
        {
            bool isDef = FileAssociationService.IsDefault();
            zplBtn.IsEnabled = !isDef;
            zplBtn.Content = isDef ? SL("general.lbl.alreadyDefault") : SL("general.lbl.setDefault");
        }
        RefreshZplBtn();
        zplBtn.Click += async (_, _) =>
        {
            // Fails only when a Windows "UserChoice" already claims .zpl (Win10/11
            // won't let an app override it silently) — send the user to the OS
            // Default-apps page to finish the change.
            if (!FileAssociationService.SetAsDefault())
            {
                await ShowMessageAsync(SL("general.cards.zplAssoc.title"), SL("general.lbl.setDefaultFailed"));
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
                catch { }
            }
            RefreshZplBtn();
        };
        panel.Children.Add(MakeCard("\uE8A5", SL("general.cards.zplAssoc.title"),
            SL("general.cards.zplAssoc.desc"), zplBtn));

        var resetBtn = new Button { Content = SL("general.lbl.reset") };
        resetBtn.Click += async (_, _) =>
        {
            var dlg = CreateDialog(SL("general.lbl.resetDlgTitle"),
                new TextBlock { Text = SL("general.lbl.resetDlgBody"), TextWrapping = TextWrapping.Wrap },
                SL("general.lbl.reset"), SL("general.lbl.cancel"));
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            ResetSettings();
        };
        panel.Children.Add(MakeCard("\uE777", SL("general.cards.reset.title"),
            SL("general.cards.reset.desc"), resetBtn));
        return panel;
    }

    private UIElement BuildAboutSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("about"));

        // Version from the executable's assembly (the app is unpackaged, so there
        // is no MSIX package identity to read).
        string version;
        try
        {
            var v = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(Environment.ProcessPath!).FileVersion;
            version = string.IsNullOrWhiteSpace(v) ? SL("about.lbl.unknownVersion") : v!;
        }
        catch { version = SL("about.lbl.unknownVersion"); }

        var copyVersion = new Button { Content = SL("about.lbl.copy") };
        copyVersion.Click += (_, _) => CopyTextToClipboard($"Ultimate ZPL Viewer {version}");
        panel.Children.Add(MakeCard("\uE946", SL("about.cards.version.title"),
            $"Ultimate ZPL Viewer {version}", copyVersion));

        panel.Children.Add(MakeCard("\uE77B", SL("about.cards.developer.title"),
            SL("about.cards.developer.desc"), null));

        panel.Children.Add(MakeCard("\uE943", SL("about.cards.tech.title"),
            SL("about.cards.tech.desc"), null));

        panel.Children.Add(MakeCard("\uE72E", SL("about.cards.privacy.title"),
            SL("about.cards.privacy.desc"), null));

        panel.Children.Add(MakeCard("\uE8F1", SL("about.cards.thirdParty.title"),
            SL("about.cards.thirdParty.desc"), null));

        panel.Children.Add(MakeCard("\uE8D0", SL("about.cards.copyright.title"),
            SL("about.lbl.copyrightText").Replace("{year}", DateTime.Now.Year.ToString()), null));

        return panel;
    }

    private void ResetSettings()
    {
        _settings.ResetToDefaults();
        Root.RequestedTheme = _settings.ToElementTheme();
        (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.SetTheme(_settings.ToElementTheme());
        ApplyAccentFromSettings();
        _editorWidth = 420;
        ApplyEditorLayout();
        ApplyEditorOptions();
        LoadDensityOptions(SelectedDpmm);
        UpdateSizeBoxes();
        UpdateSizeBoxLocks();
        DrawPreviewGrid();
        PostToEditor("{\"type\":\"setLineNumbers\",\"show\":" + (_settings.ShowLineNumbers ? "true" : "false") + "}");
        BuildSettingsCategories();
        ShowSettingsCategory("general");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyDefaultZoom);
    }

    // Debounces accent application so dragging the color picker stays smooth
    // (each apply reloads theme resources).
    private void ScheduleAccentApply()
    {
        if (_accentApplyTimer is null)
        {
            _accentApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _accentApplyTimer.Tick += (_, _) => { _accentApplyTimer!.Stop(); ApplyAccentFromSettings(); };
        }
        _accentApplyTimer.Stop();
        _accentApplyTimer.Start();
    }

    // RadioButton whose label ends with an ⓘ icon showing an explanatory tooltip.
    private static RadioButton MakeInfoRadio(string label, string tooltip, bool isChecked)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE946", // Info
            FontSize = 13,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(icon, new ToolTip
        {
            Content = new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
        });

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(icon);

        return new RadioButton
        {
            GroupName = "AutoSizeMode",
            Content   = content,
            IsChecked = isChecked,
        };
    }

    private ContentDialog CreateDialog(string title, object content, string primary, string close)
    {
        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Primary
        };
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await CreateDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "Ok", string.Empty).ShowAsync();
    }

    private static IEnumerable<string> GetInstalledPrinters()
    {
        using var printers = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        return printers?.GetSubKeyNames()
            // The app's own capture printer is not a real output target — hide it.
            .Where(name => !string.Equals(name, VirtualPrinterService.PrinterName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name).ToArray() ?? Array.Empty<string>();
    }

    private IntPtr GetWindowHandle()
    {
        var window = AppWindowLookup.MainWindowForXamlRoot(XamlRoot);
        return window is null ? IntPtr.Zero : WindowNative.GetWindowHandle(window);
    }

    private async Task<RenderSnapshot> RenderSnapshotAsync()
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(PreviewCanvas);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        int fullW = bitmap.PixelWidth, fullH = bitmap.PixelHeight;

        // RenderTargetBitmap sizes to the children's full extent, including any
        // that overflow the label. The document sits at the canvas top-left and
        // its content is already clipped to it, so crop the snapshot to the
        // document's bounding box — the PNG then contains only the label.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        double bw = PreviewCanvas.Width, bh = PreviewCanvas.Height;
        if (double.IsNaN(bw) || double.IsNaN(bh))
            return new RenderSnapshot(fullW, fullH, pixels);

        int docW = Math.Clamp((int)Math.Round(bw * scale), 1, fullW);
        int docH = Math.Clamp((int)Math.Round(bh * scale), 1, fullH);
        if (docW == fullW && docH == fullH)
            return new RenderSnapshot(fullW, fullH, pixels);

        var cropped = new byte[docW * docH * 4];
        for (int y = 0; y < docH; y++)
            Array.Copy(pixels, y * fullW * 4, cropped, y * docW * 4, docW * 4);
        return new RenderSnapshot(docW, docH, cropped);
    }

    private static async Task<byte[]> EncodePngAsync(RenderSnapshot snapshot, double scale = 1.0)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)snapshot.Width, (uint)snapshot.Height, 96, 96, snapshot.BgraPixels);
        // Quality scaling: resample the encoded frame to a lower/higher resolution.
        if (Math.Abs(scale - 1.0) > 0.001)
        {
            int w = Math.Max(1, (int)Math.Round(snapshot.Width * scale));
            int h = Math.Max(1, (int)Math.Round(snapshot.Height * scale));
            encoder.BitmapTransform.ScaledWidth = (uint)w;
            encoder.BitmapTransform.ScaledHeight = (uint)h;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant; // high quality
        }
        await encoder.FlushAsync();
        using var read = stream.AsStreamForRead();
        using var memory = new MemoryStream();
        await read.CopyToAsync(memory);
        return memory.ToArray();
    }

    // ── Print capture (Ultimate ZPL Viewer virtual printer) ──────────────────

    // Loads a ZPL job captured from the virtual printer into a new tab, so it
    // never overwrites the document being edited.
    public void LoadCapturedZpl(string zpl)
    {
        AddTabAndActivate(null); // captured from the printer, never saved
        SetEditorText(zpl);
        _isDirty = true;
        UpdateDocumentTitle();
    }

    // Reports that a non-ZPL document was sent to the virtual printer.
    public async void ShowUnsupportedPrintFormat()
    {
        await ShowMessageAsync("Format non pris en charge",
            "Le document envoyé à l'imprimante « Ultimate ZPL Viewer » n'est pas un fichier ZPL.\n\n" +
            "Cette imprimante n'accepte que des fichiers ZPL — les PDF et autres formats ne peuvent pas être ouverts.");
    }

    // ── Monaco Editor bridge ─────────────────────────────────────────────────

    private void EditorWebView_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var raw = args.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(raw)) return;
        using var doc = JsonDocument.Parse(raw);
        var type = doc.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "ready":
                OnEditorReady();
                break;
            case "textChanged":
                OnEditorTextChanged(doc.RootElement.GetProperty("text").GetString() ?? "");
                break;
            case "cursorChanged":
                _cursorOffset = doc.RootElement.GetProperty("offset").GetInt32();
                if (DocBadge.IsChecked == true) UpdateDocPanel();
                break;
            case "save":
                _ = SaveAsync();
                break;
        }
    }

    private bool IsDarkTheme => Root.ActualTheme == ElementTheme.Dark;

    // Whether the PREVIEW area (not the app chrome) uses the light background. The
    // "Dark & light preview" mode makes this true while the chrome stays dark.
    private bool PreviewIsLight => _settings.Theme switch
    {
        ThemePreference.Light => true,
        ThemePreference.Dark => false,
        ThemePreference.DarkLightPreview => true,
        _ => Root.ActualTheme != ElementTheme.Dark, // System → follow the app
    };

    // The grid colour actually used: the custom ARGB if enabled, else the faint
    // default keyed to whether the preview background is light or dark.
    private Windows.UI.Color EffectiveGridColor() => _settings.UseCustomGridColor
        ? ZplColorSchemeService.ParseHexColor(_settings.CustomGridColor, Windows.UI.Color.FromArgb(0x40, 0x80, 0x80, 0x80))
        : (PreviewIsLight ? Windows.UI.Color.FromArgb(0x12, 0x00, 0x00, 0x00)
                          : Windows.UI.Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

    // Applies the preview background per PreviewIsLight (independent of the app
    // chrome) and redraws the grid/rulers/caption, which key off it.
    private void ApplyPreviewTheme()
    {
        var bg = PreviewIsLight
            ? Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3)
            : Windows.UI.Color.FromArgb(0xFF, 0x15, 0x15, 0x15);
        PreviewSurface.Background = new SolidColorBrush(bg);
        DrawPreviewGrid();
        DrawRulers();
        UpdatePreviewCaption();
    }

    // Replaces the SystemAccentColor* resources and reloads the theme so the
    // whole application (buttons, hover states, checkboxes, …) re-resolves them.
    private void ApplyAccentFromSettings()
        => AccentColorService.Apply(_settings.UseSystemAccent
            ? null
            : ZplColorSchemeService.ParseHexColor(_settings.CustomAccent, Microsoft.UI.Colors.DodgerBlue),
            Root);

    private void ApplyEditorTheme()
    {
        PostToEditor($"{{\"type\":\"setTheme\",\"dark\":{(IsDarkTheme ? "true" : "false")}}}");
        PostToEditor(ZplHighlighter.GetColorsJson(IsDarkTheme));
    }

    // Reloads Monaco in the app's current language (its localized UI can only be
    // chosen at load time). The document text lives in C# (_currentText / DocTab),
    // so OnEditorReady re-pushes it after the reload; undo history is reset.
    private void ReloadEditorForLanguage()
    {
        var lang = _settings.Language == "en" ? "en" : "fr";
        if (lang == _editorLang || EditorWebView.CoreWebView2 is null) return;
        _editorLang = lang;
        _editorReady = false;
        EditorWebView.Source = new Uri($"https://zpl-editor.local/editor.html?lang={lang}");
    }

    private void OnEditorReady()
    {
        _editorReady = true;
        ApplyEditorTheme();
        PostToEditor("{\"type\":\"setLineNumbers\",\"show\":" + (_settings.ShowLineNumbers ? "true" : "false") + "}");
        ApplyEditorOptions();
        // Bind the initial document to its tab's Monaco model before filling it,
        // so later tab switches can save/restore it by id.
        if (_activeTab is not null) PostToEditor(BuildSwitchDocMessage(_activeTab.Id, ""));
        PostToEditor(BuildSetTextMessage(_currentText));
        if (!string.IsNullOrEmpty(_currentText))
            PostToEditor(ZplHighlighter.GetDecorationsJson(_currentText));
        RunStaticAnalysis();
    }

    // Pushes the editor display options (font size, word wrap, minimap).
    private void ApplyEditorOptions()
    {
        if (!_editorReady) return;
        PostToEditor(
            "{\"type\":\"setEditorOptions\"," +
            $"\"fontSize\":{_settings.EditorFontSize}," +
            $"\"wordWrap\":{(_settings.EditorWordWrap ? "true" : "false")}," +
            $"\"minimap\":{(_settings.EditorMinimap ? "true" : "false")}}}");
    }

    // Re-reads the highlighting colors and re-applies them to the editor.
    private void ReapplyHighlightColors()
    {
        if (!_editorReady) return;
        PostToEditor(ZplHighlighter.GetColorsJson(IsDarkTheme));
        if (!string.IsNullOrEmpty(_currentText))
            PostToEditor(ZplHighlighter.GetDecorationsJson(_currentText));
    }

    private void OnEditorTextChanged(string text)
    {
        // Equal → the change came from a programmatic SetEditorText call (already processed).
        if (text == _currentText) return;
        _currentText = text;
        if (!_isDirty) { _isDirty = true; UpdateDocumentTitle(); }
        RefreshPreview(SizeUpdate.TextEdited);
        ScheduleHighlighting();
    }

    private void SetEditorText(string text, SizeUpdate kind = SizeUpdate.DocumentLoaded)
    {
        // Normalise to LF so _currentText always matches Monaco's getValue() output.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        _currentText = text;
        RefreshPreview(kind);
        ScheduleHighlighting();
        // Guard: _currentText is set before posting so Monaco's textChanged reply is filtered.
        if (_editorReady)
            PostToEditor(BuildSetTextMessage(text));
    }

    private void ScheduleHighlighting()
    {
        if (_highlightTimer is null)
        {
            _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _highlightTimer.Tick += (_, _) =>
            {
                _highlightTimer!.Stop();
                if (_editorReady && !string.IsNullOrEmpty(_currentText))
                    PostToEditor(ZplHighlighter.GetDecorationsJson(_currentText));
                RunStaticAnalysis();
            };
        }
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    private List<ZplDiagnostic> _diagnostics = new();

    private void RunStaticAnalysis()
    {
        if (!_editorReady) return;

        _diagnostics = ZplStaticAnalyzer.Analyze(_currentText, _settings.ShowLowWarnings);
        PostToEditor(ZplStaticAnalyzer.GetMarkersJson(_diagnostics));
        UpdateDiagnosticsUi();
    }

    private void UpdateDiagnosticsUi()
    {
        int errors   = _diagnostics.Count(d => d.Severity == ZplStaticAnalyzer.Error);
        int warnings = _diagnostics.Count(d => d.Severity == ZplStaticAnalyzer.Warning);
        int lows     = _diagnostics.Count(d => d.Severity == ZplStaticAnalyzer.LowWarning);

        ErrorBadgeCount.Text      = errors.ToString();
        WarningBadgeCount.Text    = warnings.ToString();
        LowWarningBadgeCount.Text = lows.ToString();
        ErrorBadge.Visibility      = errors   > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningBadge.Visibility    = warnings > 0 ? Visibility.Visible : Visibility.Collapsed;
        LowWarningBadge.Visibility = lows     > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The panel lists only the categories whose badge is toggled on.
        bool showErrors   = errors   > 0 && ErrorBadge.IsChecked      == true;
        bool showWarnings = warnings > 0 && WarningBadge.IsChecked    == true;
        bool showLows     = lows     > 0 && LowWarningBadge.IsChecked == true;
        var filtered = _diagnostics.Where(d => d.Severity switch
        {
            ZplStaticAnalyzer.Error => showErrors,
            ZplStaticAnalyzer.Warning => showWarnings,
            _ => showLows,
        }).ToList();

        ErrorList.ItemsSource = filtered;
        ErrorPanel.Visibility = filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DiagBadge_Click(object sender, RoutedEventArgs e)
        => UpdateDiagnosticsUi();

    // ── Command documentation panel ─────────────────────────────────────────

    private int _cursorOffset;

    private void DocBadge_Click(object sender, RoutedEventArgs e)
    {
        DocPanel.Visibility = DocBadge.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (DocBadge.IsChecked == true) UpdateDocPanel();
    }

    private void UpdateDocPanel()
    {
        DocContent.Children.Clear();

        var def = ZplStaticAnalyzer.FindCommandAt(_currentText, _cursorOffset, out var matched);
        if (def is null)
        {
            DocContent.Children.Add(new TextBlock
            {
                Text = "Placez le curseur sur une commande ZPL pour afficher sa documentation.",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        // Descriptions may be dynamic "lang.editor.*" references → localize them.
        var shortDesc = LocalizationService.Resolve(def.ShortDescription);
        var longDesc  = LocalizationService.Resolve(def.Description);

        // Title: command (+ alternative form) — short description
        var alt = string.IsNullOrEmpty(def.AlternativeCommand) ? "" : $"  ·  {def.AlternativeCommand}";
        DocContent.Children.Add(new TextBlock
        {
            Text = $"{def.Command}{alt} — {shortDesc}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // Signature in monospace: ^GBwidth,height,thickness,color,rounding
        var parameters = def.Parameters ?? new List<ZplParamDef>();
        if (parameters.Count > 0)
        {
            DocContent.Children.Add(new TextBlock
            {
                Text = matched + string.Join(",", parameters.Select(p => p.Name)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // Long description (or a notice when the command is undocumented).
        DocContent.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(longDesc)
                ? "Aucune documentation disponible pour cette commande."
                : longDesc,
            Opacity = string.IsNullOrWhiteSpace(longDesc) ? 0.6 : 1.0,
            TextWrapping = TextWrapping.Wrap,
        });

        // Parameters table: name | description | type | required
        if (parameters.Count > 0)
        {
            var table = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            table.Children.Add(MakeDocRow("Paramètre", "Description", "Type", null, header: true));
            foreach (var p in parameters)
            {
                var pDesc = LocalizationService.Resolve(p.Description);
                table.Children.Add(MakeDocRow(
                    p.Name,
                    string.IsNullOrWhiteSpace(pDesc) ? "—" : pDesc,
                    p.IsNumber ? "Nombre" : "Texte",
                    p.Required,
                    header: false));
            }
            DocContent.Children.Add(table);
        }
    }

    private static Grid MakeDocRow(string name, string description, string type, bool? required, bool header)
    {
        var row = new Grid { Padding = new Thickness(6, 4, 6, 4), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var weight = header ? FontWeights.SemiBold : FontWeights.Normal;
        var nameBlock = new TextBlock
        {
            Text = name, FontSize = 12, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
            FontFamily = header ? FontFamily.XamlAutoFontFamily : new FontFamily("Consolas"),
        };
        var descBlock = new TextBlock { Text = description, FontSize = 12, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        var typeBlock = new TextBlock { Text = type, FontSize = 12, FontWeight = weight };
        Grid.SetColumn(descBlock, 1);
        Grid.SetColumn(typeBlock, 2);
        row.Children.Add(nameBlock);
        row.Children.Add(descBlock);
        row.Children.Add(typeBlock);

        if (header)
        {
            var reqHeader = new TextBlock { Text = "Requis", FontSize = 12, FontWeight = weight };
            Grid.SetColumn(reqHeader, 3);
            row.Children.Add(reqHeader);
            row.BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];
            row.BorderThickness = new Thickness(0, 0, 0, 1);
        }
        else
        {
            var check = new CheckBox
            {
                IsChecked = required == true,
                IsEnabled = false,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
            };
            Grid.SetColumn(check, 3);
            row.Children.Add(check);
        }

        return row;
    }

    private void ErrorList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ZplDiagnostic d)
            PostToEditor($"{{\"type\":\"revealLine\",\"line\":{d.Line}}}");
    }

    private void PostToEditor(string json)
        => EditorWebView.CoreWebView2?.PostWebMessageAsString(json);

    private static string BuildSetTextMessage(string text)
    {
        var escaped = JsonSerializer.Serialize(text); // produces a quoted, escaped JSON string
        return $"{{\"type\":\"setText\",\"text\":{escaped}}}";
    }
}

public sealed record RenderSnapshot(int Width, int Height, byte[] BgraPixels);

// A document open in a tab. The active tab's live state is held by the page
// fields; DocTab stores the snapshot used while the tab is inactive. Id keys
// the per-document Monaco model in the editor page.
public sealed class DocTab
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? FilePath { get; set; }
    public string Text { get; set; } = "";
    public bool IsDirty { get; set; }

    // Display zoom the user picked for THIS document, in percent. Null while the
    // document still follows the default-zoom setting. Lives only as long as the
    // tab (never persisted): once set, every relayout — window resize, fullscreen,
    // toolbar/editor toggles, redraws — restores it instead of the default, and
    // switching tabs brings each document back to its own level.
    public double? ZoomPercent { get; set; }
}

// A draggable toolbar item in the toolbar designer.
public sealed class ToolbarChip
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Glyph { get; set; } = "";
    // When set, the chip shows one mini-button per entry (single shared grip)
    // instead of a single icon + label.
    public (string Glyph, string Label)[]? Buttons { get; set; }
}

public static class AppWindowLookup
{
    public static Window? MainWindowForXamlRoot(XamlRoot root)
    {
        return (Application.Current as App)?.MainWindow;
    }
}

// Grid subclass that exposes the protected cursor API, so the editor/preview
// splitter can show a horizontal-resize cursor.
public sealed partial class CursorGrid : Grid
{
    public void SetCursor(Microsoft.UI.Input.InputCursor cursor) => ProtectedCursor = cursor;
}

// ListView whose item containers show a hand cursor on hover and a move (grab)
// cursor while the pointer is pressed — used by the toolbar designer chips.
public sealed partial class ChipListView : ListView
{
    protected override DependencyObject GetContainerForItemOverride() => new ChipListViewItem();
}

// A designer chip: builds its own visual from the ToolbarChip it represents.
// Used directly as ListView item, so no template machinery is involved.
public sealed partial class ToolbarChipView : Grid
{
    public ToolbarChip Chip { get; }

    public ToolbarChipView(ToolbarChip chip)
    {
        Chip = chip;
        Children.Add(BuildChipVisual(chip));
    }

    // Chip: 6-dot grip | icon + label, or grip | mini-button per real button.
    private static UIElement BuildChipVisual(ToolbarChip chip)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(BuildGrip());

        if (chip.Buttons is { Length: > 0 })
        {
            foreach (var (glyph, label) in chip.Buttons)
                row.Children.Add(BuildMiniButton(glyph, label));
        }
        else
        {
            if (!string.IsNullOrEmpty(chip.Glyph))
                row.Children.Add(new FontIcon { Glyph = chip.Glyph, FontSize = 14 });
            row.Children.Add(new TextBlock
            {
                Text = chip.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Child = row,
        };
    }

    // Non-interactive button look-alike (the chip itself is the drag target).
    private static Border BuildMiniButton(string glyph, string label)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(glyph))
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        content.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Child = content,
        };
    }

    private static Grid BuildGrip()
    {
        var grip = new Grid { Width = 7, Height = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 };
        for (int r = 0; r < 3; r++) grip.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < 2; c++) grip.ColumnDefinitions.Add(new ColumnDefinition());
        var fill = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 2; c++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 2.4, Height = 2.4, Fill = fill,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetRow(dot, r);
                Grid.SetColumn(dot, c);
                grip.Children.Add(dot);
            }
        return grip;
    }
}

// (Windows has no built-in "closed hand" cursor; SizeAll is the conventional
// "moving" cursor.)
public sealed partial class ChipListViewItem : ListViewItem
{
    private static readonly Microsoft.UI.Input.InputCursor Hand =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
    private static readonly Microsoft.UI.Input.InputCursor Grab =
        Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);

    public ChipListViewItem()
    {
        ProtectedCursor = Hand;
        PointerPressed += (_, _) => ProtectedCursor = Grab;
        PointerReleased += (_, _) => ProtectedCursor = Hand;
        PointerCaptureLost += (_, _) => ProtectedCursor = Hand;
        PointerExited += (_, _) => ProtectedCursor = Hand;
    }
}

// Horizontal wrap panel for the toolbar: children flow left to right and wrap
// to a new line when the window is too narrow, so no control is ever clipped.
// Each line centers its children vertically. Separators (Rectangle children)
// are hidden when they end up at the start or the end of a line — a separator
// with nothing on one of its sides is visual noise. (WinUI 3 has no WrapPanel.)
public sealed partial class ToolbarWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 10;
    public double VerticalSpacing { get; set; } = 8;

    private static bool IsSeparator(UIElement e) => e is Microsoft.UI.Xaml.Shapes.Rectangle;

    // Splits the visible children into lines for the given width, dropping the
    // separators that would sit at a line boundary. Used by measure AND arrange
    // so both passes agree.
    private (List<List<UIElement>> Lines, List<UIElement> Hidden) BuildLines(double maxWidth)
    {
        var lines  = new List<List<UIElement>>();
        var hidden = new List<UIElement>();
        var line   = new List<UIElement>();
        double lineWidth = 0;

        void EndLine()
        {
            while (line.Count > 0 && IsSeparator(line[^1]))
            {
                hidden.Add(line[^1]);
                line.RemoveAt(line.Count - 1);
            }
            if (line.Count > 0) lines.Add(line);
            line = new List<UIElement>();
            lineWidth = 0;
        }

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            // Drop separators that would be orphaned: at the start of a line
            // (nothing to their left — e.g. the group before them is hidden in
            // lite mode) or right after another separator.
            if (IsSeparator(child) && (line.Count == 0 || IsSeparator(line[^1])))
            {
                hidden.Add(child);
                continue;
            }

            var width  = child.DesiredSize.Width;
            var needed = (line.Count > 0 ? HorizontalSpacing : 0) + width;
            if (line.Count > 0 && lineWidth + needed > maxWidth)
            {
                EndLine();
                if (IsSeparator(child)) { hidden.Add(child); continue; } // never start a line with one
                line.Add(child);
                lineWidth = width;
            }
            else
            {
                line.Add(child);
                lineWidth += needed;
            }
        }

        EndLine();
        return (lines, hidden);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var (lines, _) = BuildLines(availableSize.Width);
        double maxWidth = 0, totalHeight = 0;
        foreach (var line in lines)
        {
            maxWidth = Math.Max(maxWidth,
                line.Sum(c => c.DesiredSize.Width) + HorizontalSpacing * (line.Count - 1));
            totalHeight += line.Max(c => c.DesiredSize.Height);
        }
        if (lines.Count > 1) totalHeight += VerticalSpacing * (lines.Count - 1);

        var width = double.IsInfinity(availableSize.Width) ? maxWidth : Math.Min(maxWidth, availableSize.Width);
        return new Windows.Foundation.Size(width, totalHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var (lines, hidden) = BuildLines(finalSize.Width);

        double y = 0;
        foreach (var line in lines)
        {
            double lineHeight = line.Max(c => c.DesiredSize.Height);
            double x = 0;
            foreach (var item in line)
            {
                var s = item.DesiredSize;
                item.Arrange(new Windows.Foundation.Rect(x, y + (lineHeight - s.Height) / 2, s.Width, s.Height));
                x += s.Width + HorizontalSpacing;
            }
            y += lineHeight + VerticalSpacing;
        }

        // XAML requires every child to be arranged: zero-size for the rest.
        foreach (var item in hidden)
            item.Arrange(new Windows.Foundation.Rect(0, 0, 0, 0));
        foreach (var child in Children)
            if (child.Visibility == Visibility.Collapsed)
                child.Arrange(new Windows.Foundation.Rect(0, 0, 0, 0));

        return finalSize;
    }
}
