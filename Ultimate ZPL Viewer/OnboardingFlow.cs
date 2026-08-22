using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ultimate_ZPL_Viewer;

// The first-run wizard: one full-window page instead of the stack of dialogs that
// used to fire one after another before the application could be used at all.
//
// The chrome is fixed — a stepper across the top, the step's content in the middle,
// navigation at the bottom — and each step only fills in the middle. Steps that are
// already satisfied (fonts present, printer installed, screen size read from the
// EDID) are still SHOWN, marked as done, so the user sees what was checked instead
// of wondering what happened.
internal sealed class OnboardingFlow
{
    // Fonts are not optional: the preview is wrong without them, so that step has no
    // "Passer" and its Suivant stays shut until nothing is missing.
    private enum Step { Welcome = 0, Fonts = 1, Printer = 2, Association = 3, Screens = 4, Summary = 5 }

    private const int FirstNumberedStep = (int)Step.Fonts;
    private const int LastStep = (int)Step.Summary;

    private readonly Grid _host;
    private readonly AppSettings _settings;
    private readonly OnboardingState _state;
    private readonly Action _openApp;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private readonly Action _restart;

    private Step _step;

    // What the primary and the secondary buttons do RIGHT NOW. Each step sets these
    // in place of subscribing to Click: a per-step subscription would pile up, since
    // the local functions that carry the step's behaviour are a different delegate
    // every time the step is built and so can never be unsubscribed reliably.
    private Action? _onPrimary;
    private Action? _onSecondary;

    private readonly StackPanel _stepperHost = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ContentControl _contentHost = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private readonly Button _backButton = new();
    private readonly Button _secondaryButton = new();
    private readonly Button _primaryButton = new();
    private readonly Grid _footer = new();
    private readonly Grid _header = new();

    public OnboardingFlow(Grid host, AppSettings settings, OnboardingState state,
                          Action openApp, Action openSettings, Action quit, Action restart)
    {
        _host = host;
        _settings = settings;
        _state = state;
        _openApp = openApp;
        _openSettings = openSettings;
        _quit = quit;
        _restart = restart;
        _step = (Step)Math.Clamp(state.Step, 0, LastStep);
    }

    private static string L(string key) => LocalizationService.Get("onboarding." + key);

    // ── Shell ────────────────────────────────────────────────────────────────

    public void Show()
    {
        BuildShell();
        _host.Visibility = Visibility.Visible;
        GoTo(_step);
    }

    private void BuildShell()
    {
        _host.Children.Clear();
        _host.RowDefinitions.Clear();
        _host.Background = BackdropBrush();

        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _header.Padding = new Thickness(32, 28, 32, 10);
        _header.Children.Add(_stepperHost);
        Grid.SetRow(_header, 0);
        _host.Children.Add(_header);

        var scroller = new ScrollViewer
        {
            Content = _contentHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(32, 8, 32, 8),
        };
        Grid.SetRow(scroller, 1);
        _host.Children.Add(scroller);

        _footer.Padding = new Thickness(32, 12, 32, 28);
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _backButton.Content = L("nav.back");
        _backButton.Click += (_, _) => GoTo((Step)((int)_step - 1));
        Grid.SetColumn(_backButton, 0);

        _secondaryButton.Margin = new Thickness(0, 0, 8, 0);
        _secondaryButton.Click += (_, _) => _onSecondary?.Invoke();
        Grid.SetColumn(_secondaryButton, 2);

        _primaryButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _primaryButton.MinWidth = 130;
        _primaryButton.Click += (_, _) => _onPrimary?.Invoke();
        Grid.SetColumn(_primaryButton, 3);

        _footer.Children.Add(_backButton);
        _footer.Children.Add(_secondaryButton);
        _footer.Children.Add(_primaryButton);
        Grid.SetRow(_footer, 2);
        _host.Children.Add(_footer);
    }

    // A flat grey would be a dreary first thing to see, so the page sits on a soft
    // diagonal wash tinted with the application's accent — enough to read as
    // deliberate, faint enough to stay out of the way of the text.
    private static Brush BackdropBrush()
    {
        var accent = Application.Current.Resources["SystemAccentColor"] is Color c
            ? c : Microsoft.UI.Colors.DodgerBlue;
        var bg = (Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] as SolidColorBrush)?.Color
                 ?? Microsoft.UI.Colors.Black;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
        };
        gradient.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Blend(bg, accent, 0.17) });
        gradient.GradientStops.Add(new GradientStop { Offset = 0.5, Color = Blend(bg, accent, 0.05) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1.0, Color = bg });
        return gradient;
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    // ── Stepper ──────────────────────────────────────────────────────────────

    private static readonly string[] StepKeys = { "fonts", "printer", "association", "screens", "summary" };

    private void RebuildStepper()
    {
        _stepperHost.Children.Clear();
        // The welcome page is a cover, not a step: no stepper on it.
        _header.Visibility = _step == Step.Welcome ? Visibility.Collapsed : Visibility.Visible;
        if (_step == Step.Welcome) return;

        int current = (int)_step - FirstNumberedStep;
        for (int i = 0; i < StepKeys.Length; i++)
        {
            if (i > 0) _stepperHost.Children.Add(Connector(i <= current));
            _stepperHost.Children.Add(Bead(i + 1, StepKeys[i], i, current, PassedWithoutDoing(i)));
        }
    }

    // A step the user chose to skip (or that failed) must NOT come back as a green
    // check: that would tell them something was done when it was not.
    private bool PassedWithoutDoing(int index)
    {
        var outcome = index switch
        {
            0 => _state.Fonts,
            1 => _state.Printer,
            2 => _state.Association,
            _ => OnboardingState.Outcome.Done,   // screens and summary have nothing to skip
        };
        return outcome is OnboardingState.Outcome.Skipped or OnboardingState.Outcome.Failed;
    }

    // The bar between two beads, filled up to the step in progress.
    private static Border Connector(bool filled) => new()
    {
        Width = 56,
        Height = 2,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0, 4, 22),   // 22 lifts it onto the beads, above their labels
        CornerRadius = new CornerRadius(1),
        Background = filled
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
        Opacity = filled ? 1.0 : 0.35,
    };

    private static UIElement Bead(int number, string key, int index, int current, bool passedOver)
    {
        bool visited = index < current;
        bool done = visited && !passedOver;
        bool active = index == current;

        var circle = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = done || active
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(done || active ? 0 : 1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
        };
        circle.Child = done || (visited && passedOver)
            ? new FontIcon
            {
                Glyph = done ? GlyphCheck : GlyphDash,
                FontSize = 13,
                Foreground = done
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = active
                    ? new SolidColorBrush(Microsoft.UI.Colors.White)
                    : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Width = 92 };
        stack.Children.Add(circle);
        stack.Children.Add(new TextBlock
        {
            Text = L("step." + key),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = active ? 1.0 : 0.6,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
        });
        return stack;
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private void GoTo(Step step)
    {
        _step = step;
        _state.Step = (int)step;
        _state.Save();

        RebuildStepper();

        // Reset the chrome to its default shape; the step then adjusts what it needs.
        _backButton.Visibility = step is Step.Welcome or Step.Summary
            ? Visibility.Collapsed : Visibility.Visible;
        _backButton.IsEnabled = true;
        _secondaryButton.Visibility = Visibility.Collapsed;
        _secondaryButton.ClearValue(Button.StyleProperty);
        _secondaryButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _secondaryButton.BorderThickness = new Thickness(0);
        _secondaryButton.Foreground = Muted;
        _primaryButton.Content = L("nav.next");
        _primaryButton.IsEnabled = true;
        _footer.Visibility = Visibility.Visible;
        _onSecondary = null;
        _onPrimary = () => GoTo((Step)((int)_step + 1));

        switch (step)
        {
            case Step.Welcome: BuildWelcome(); break;
            case Step.Fonts: BuildFonts(); break;
            case Step.Printer: BuildPrinter(); break;
            case Step.Association: BuildAssociation(); break;
            case Step.Screens: BuildScreens(); break;
            case Step.Summary: BuildSummary(); break;
        }
    }

    // Said the same way by every step the user has to complete before moving on,
    // so a required step always announces itself identically.
    private static Grid RequiredNotice() => StatusLine(GlyphInfo, Accent, L("nav.required"), null);

    // A quiet "Details ⌄" that unfolds the raw error underneath. A failure can then
    // explain itself without putting a wall of text in front of everyone.
    private static StackPanel Disclosure(string label, string detail)
    {
        var body = new TextBlock
        {
            Text = detail,
            Visibility = Visibility.Collapsed,
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var chevron = new FontIcon
        {
            Glyph = GlyphExpand,
            FontSize = 10,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(chevron);
        header.Children.Add(new TextBlock { Text = label, FontSize = 12 });

        var toggle = new HyperlinkButton { Content = header, Padding = new Thickness(0) };
        toggle.Click += (_, _) =>
        {
            bool show = body.Visibility == Visibility.Collapsed;
            body.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = show ? GlyphCollapse : GlyphExpand;
        };

        var panel = new StackPanel();
        panel.Children.Add(toggle);
        panel.Children.Add(body);
        return panel;
    }

    // The quiet "Passer" shown by the steps the user is allowed to leave alone.
    private void OfferSkip(Action onSkip)
    {
        _secondaryButton.Content = L("nav.skip");
        _secondaryButton.Visibility = Visibility.Visible;
        _onSecondary = onSkip;
    }

    private void Finish(Action then)
    {
        _state.Completed = true;
        _state.Save();
        _host.Visibility = Visibility.Collapsed;
        _host.Children.Clear();
        then();
    }

    // ── Shared building blocks ───────────────────────────────────────────────

    private static StackPanel Page(string titleKey, string bodyKey)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = L(titleKey),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = L(bodyKey),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        return panel;
    }

    // A bordered panel matching the settings cards, holding a step's detail.
    private static Border Card(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(18),
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };

    // One line of status: an icon saying at a glance whether this is done, still to
    // do, or merely informational, then the text.
    private static Grid StatusLine(string glyph, Brush brush, string text, string? detail)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 15,
            Foreground = brush,
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            stack.Children.Add(new TextBlock
            {
                Text = detail,
                Opacity = 0.6,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        return grid;
    }

    private const string GlyphCheck = "";
    private const string GlyphInfo = "";
    private const string GlyphWarn = "";
    private const string GlyphDash = "";
    private const string GlyphError = "";
    private const string GlyphExpand = "";
    private const string GlyphCollapse = "";

    private static Brush Ok => new SolidColorBrush(Color.FromArgb(255, 0x2E, 0xA0, 0x43));
    private static Brush Warn => new SolidColorBrush(Color.FromArgb(255, 0xE0, 0xA0, 0x30));
    private static Brush Err => new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
    private static Brush Muted => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static Brush Accent => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    // ── Step 0 — welcome ─────────────────────────────────────────────────────

    private void BuildWelcome()
    {
        _footer.Visibility = Visibility.Collapsed;

        var panel = new StackPanel
        {
            Spacing = 14,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
            if (System.IO.File.Exists(iconPath))
                panel.Children.Add(new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)),
                    Width = 76,
                    Height = 76,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 6),
                });
        }
        catch { /* decoration only — never hold the welcome page on it */ }

        panel.Children.Add(new TextBlock
        {
            Text = L("welcome.title"),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = L("welcome.body"),
            FontSize = 15,
            Opacity = 0.75,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var start = new Button
        {
            Content = L("welcome.start"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 190,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        start.Click += (_, _) => GoTo(Step.Fonts);
        panel.Children.Add(start);

        _contentHost.Content = panel;
    }

    // ── Step 1 — fonts (required) ────────────────────────────────────────────

    private void BuildFonts()
    {
        var missing = FontService.GetMissingFonts();
        var auto = missing.Where(f => f.CanAutoInstall).ToList();

        var page = Page("fonts.title", "fonts.body");
        page.Children.Add(RequiredNotice());
        var list = new StackPanel { Spacing = 12 };

        foreach (var font in FontService.RequiredFonts)
        {
            bool present = missing.All(m => m.DisplayName != font.DisplayName);
            if (present)
                list.Children.Add(StatusLine(GlyphCheck, Ok, font.DisplayName, L("fonts.installed")));
            else if (font.CanAutoInstall)
                list.Children.Add(StatusLine(GlyphInfo, Accent, font.DisplayName, L("fonts.willInstall")));
            else
                list.Children.Add(StatusLine(GlyphWarn, Warn, font.DisplayName, L("fonts.commercial")));
        }

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 14, 0, 4),
        };
        var progressText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Opacity = 0.75,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        list.Children.Add(progress);
        list.Children.Add(progressText);
        page.Children.Add(Card(list));

        // Never skippable — but a user who cannot install the commercial font must
        // still be able to leave, or the wizard is a locked door.
        if (missing.Count > 0)
        {
            var quit = new HyperlinkButton { Content = L("fonts.quit"), Margin = new Thickness(0, 10, 0, 0) };
            quit.Click += (_, _) => _quit();
            page.Children.Add(quit);
        }

        _contentHost.Content = page;

        if (missing.Count == 0)
        {
            if (_state.Fonts != OnboardingState.Outcome.Done)
                _state.Fonts = OnboardingState.Outcome.Already;
            _state.Save();
            return;   // the default primary action (go to the next step) is right
        }

        if (auto.Count > 0)
        {
            _primaryButton.Content = L("fonts.install");
            _onPrimary = () => _ = InstallFontsAsync(auto, progress, progressText);
        }
        else
        {
            // Only the commercial font is missing: there is nothing we can install
            // for them, so offer to look again once they have done it themselves.
            _primaryButton.Content = L("fonts.recheck");
            _onPrimary = () => GoTo(Step.Fonts);
        }
    }

    private async Task InstallFontsAsync(List<FontInfo> fonts, ProgressBar progress, TextBlock progressText)
    {
        _onPrimary = null;
        _primaryButton.IsEnabled = false;
        _backButton.IsEnabled = false;
        progress.Visibility = Visibility.Visible;
        progressText.Visibility = Visibility.Visible;

        var report = new Progress<(double Value, string Status)>(r =>
        {
            progress.Value = r.Value;
            progressText.Text = r.Status;
        });

        IReadOnlyList<FontInstallResult> results = Array.Empty<FontInstallResult>();
        try { results = await FontService.InstallAsync(fonts, report, CancellationToken.None); }
        catch (Exception ex) { progressText.Text = ex.Message; }

        bool ok = results.Count > 0 && results.All(r => r.Success);
        _state.Fonts = ok ? OnboardingState.Outcome.Done : OnboardingState.Outcome.Failed;
        // A font installed into a running WinUI process is not picked up by it, so
        // the application restarts — and comes back on the step after this one.
        _state.Step = ok ? (int)Step.Printer : (int)Step.Fonts;
        _state.Save();

        if (ok) { _restart(); return; }

        progress.Visibility = Visibility.Collapsed;
        progressText.Foreground = Warn;
        var failed = results.Where(r => !r.Success).Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrEmpty(e));
        progressText.Text = failed ?? L("fonts.failed");
        _backButton.IsEnabled = true;
        _primaryButton.IsEnabled = true;
        _primaryButton.Content = L("fonts.retry");
        _onPrimary = () => GoTo(Step.Fonts);
    }

    // ── Step 2 — virtual printer (optional) ──────────────────────────────────

    private void BuildPrinter()
    {
        bool installed = VirtualPrinterService.IsInstalled();
        var page = Page("printer.title", "printer.body");

        var card = new StackPanel { Spacing = 10 };
        card.Children.Add(installed
            ? StatusLine(GlyphCheck, Ok, L("printer.already"), null)
            : StatusLine(GlyphInfo, Accent, L("printer.what"), L("printer.elevation")));

        // Where "installing", "installed" and "failed" are each rendered in turn,
        // so the outcome is stated on the page instead of only being navigated past.
        var resultHost = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        card.Children.Add(resultHost);
        page.Children.Add(Card(card));
        _contentHost.Content = page;

        if (installed)
        {
            if (_state.Printer != OnboardingState.Outcome.Done)
                _state.Printer = OnboardingState.Outcome.Already;
            _state.Save();
            return;
        }

        OfferSkip(() =>
        {
            _state.Printer = OnboardingState.Outcome.Skipped;
            _state.Save();
            GoTo(Step.Association);
        });
        _primaryButton.Content = L("printer.install");
        _onPrimary = () => _ = InstallPrinterAsync(resultHost);
    }

    private async Task InstallPrinterAsync(StackPanel resultHost)
    {
        _onPrimary = null;
        _primaryButton.IsEnabled = false;
        _secondaryButton.IsEnabled = false;

        resultHost.Children.Clear();
        resultHost.Children.Add(new TextBlock
        {
            Text = L("printer.installing"),
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        var result = await Task.Run(VirtualPrinterService.EnsureInstalled);

        resultHost.Children.Clear();
        _primaryButton.IsEnabled = true;
        _secondaryButton.IsEnabled = true;

        if (result.Ok)
        {
            _state.Printer = OnboardingState.Outcome.Done;
            _state.Save();
            resultHost.Children.Add(StatusLine(GlyphCheck, Ok, L("printer.success"), null));
            // Nothing left to skip once it is installed.
            _secondaryButton.Visibility = Visibility.Collapsed;
            _onSecondary = null;
            _primaryButton.Content = L("nav.next");
            _onPrimary = () => GoTo(Step.Association);
            return;
        }

        _state.Printer = OnboardingState.Outcome.Failed;
        _state.Save();
        resultHost.Children.Add(StatusLine(GlyphError, Err, L("printer.failedShort"), null));
        if (!string.IsNullOrWhiteSpace(result.Error))
            resultHost.Children.Add(Disclosure(L("printer.details"), result.Error));
        _primaryButton.Content = L("printer.retry");
        _onPrimary = () => _ = InstallPrinterAsync(resultHost);
    }

    // ── Step 3 — .zpl association (optional) ─────────────────────────────────

    private void BuildAssociation()
    {
        bool isDefault = FileAssociationService.IsDefault();
        var page = Page("assoc.title", "assoc.body");

        var card = new StackPanel { Spacing = 10 };
        card.Children.Add(isDefault
            ? StatusLine(GlyphCheck, Ok, L("assoc.already"), null)
            : StatusLine(GlyphInfo, Muted, L("assoc.what"), null));
        var status = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };
        card.Children.Add(status);
        page.Children.Add(Card(card));
        _contentHost.Content = page;

        if (isDefault)
        {
            if (_state.Association != OnboardingState.Outcome.Done)
                _state.Association = OnboardingState.Outcome.Already;
            _state.Save();
            return;
        }

        OfferSkip(() =>
        {
            _state.Association = OnboardingState.Outcome.Skipped;
            _state.Save();
            GoTo(Step.Screens);
        });
        _primaryButton.Content = L("assoc.set");
        _onPrimary = () =>
        {
            if (FileAssociationService.SetAsDefault())
            {
                _state.Association = OnboardingState.Outcome.Done;
                _state.Save();
                GoTo(Step.Screens);
                return;
            }
            // Windows refused (an existing UserChoice): say so and move on rather
            // than leaving a button that will never work.
            _state.Association = OnboardingState.Outcome.Failed;
            _state.Save();
            _settings.AskZplAssociation = false;
            _settings.Save();
            status.Visibility = Visibility.Visible;
            status.Foreground = Warn;
            status.Text = L("assoc.manual");
            _primaryButton.Content = L("nav.next");
            _onPrimary = () => GoTo(Step.Screens);
        };
    }

    // ── Step 4 — screens ─────────────────────────────────────────────────────

    // Every connected monitor is listed, whether or not its size came from the EDID:
    // a detected size shows in a disabled field, so the user can SEE what the
    // application will use instead of having to take it on trust.
    private void BuildScreens()
    {
        var page = Page("screens.title", "screens.body");
        var host = new StackPanel { Spacing = 10 };
        page.Children.Add(host);
        _contentHost.Content = page;
        _ = PopulateScreensAsync(host);
    }

    private async Task PopulateScreensAsync(StackPanel host)
    {
        List<MonitorInfo> monitors;
        try { monitors = await DisplayMetrics.EnumerateMonitorsAsync(); }
        catch { monitors = new List<MonitorInfo>(); }

        host.Children.Clear();
        if (monitors.Count == 0)
        {
            host.Children.Add(Card(StatusLine(GlyphWarn, Warn, L("screens.none"), null)));
            return;
        }
        foreach (var mon in monitors) host.Children.Add(ScreenCard(mon));
    }

    private Border ScreenCard(MonitorInfo mon)
    {
        bool auto = mon.EdidDiagonalInches is not null;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Ticked as soon as this screen has a usable size, so the ones still to fill
        // in stand out. Faded rather than collapsed, to keep every card aligned.
        var check = new FontIcon
        {
            Glyph = GlyphCheck,
            FontSize = 15,
            Foreground = Ok,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        grid.Children.Add(check);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = mon.FriendlyName,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        left.Children.Add(new TextBlock
        {
            Text = string.Format(L(auto ? "screens.detected" : "screens.manual"), mon.ResW, mon.ResH),
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 12, 0),
        });
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);

        var box = new TextBox { MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
        double initial = auto
            ? mon.EdidDiagonalInches!.Value
            : _settings.ManualScreenSizesInches.TryGetValue(mon.InterfaceId, out var mi) ? mi : 0;
        box.Text = initial > 0
            ? initial.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)
            : "";
        box.IsEnabled = !auto;   // a detected size is shown, not editable
        check.Opacity = auto || initial > 0 ? 1 : 0;

        box.TextChanging += (s, _) =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(s.Text, @"^\d*([.,]\d{0,2})?");
            if (m.Value == s.Text) return;
            var pos = s.SelectionStart;
            s.Text = m.Value;
            s.SelectionStart = Math.Min(pos, s.Text.Length);
        };
        box.TextChanged += (_, _) =>
        {
            if (auto) return;
            bool valid = double.TryParse((box.Text ?? "").Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var val) && val > 0;
            check.Opacity = valid ? 1 : 0;
            if (valid)
            {
                _settings.ManualScreenSizesInches[mon.InterfaceId] = val;
                _settings.Save();
            }
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(box);
        row.Children.Add(new TextBlock
        {
            Text = L("screens.inches"),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
        });
        Grid.SetColumn(row, 2);
        grid.Children.Add(row);

        return Card(grid);
    }

    // ── Step 5 — summary ─────────────────────────────────────────────────────

    private void BuildSummary()
    {
        var page = Page("summary.title", "summary.body");
        var list = new StackPanel { Spacing = 12 };
        list.Children.Add(OutcomeLine("summary.fonts", _state.Fonts));
        list.Children.Add(OutcomeLine("summary.printer", _state.Printer));
        list.Children.Add(OutcomeLine("summary.assoc", _state.Association));
        list.Children.Add(StatusLine(GlyphCheck, Ok, L("summary.screens"), L("summary.screensDetail")));
        page.Children.Add(Card(list));
        page.Children.Add(new TextBlock
        {
            Text = L("summary.later"),
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
        _contentHost.Content = page;

        // Two ways out: straight into the application, or into the settings for a
        // user who would rather keep configuring while they are at it. The secondary
        // slot is reused, so nothing accumulates if this step is built twice.
        _secondaryButton.Content = L("summary.openSettings");
        _secondaryButton.ClearValue(Button.BackgroundProperty);
        _secondaryButton.ClearValue(Button.BorderThicknessProperty);
        _secondaryButton.ClearValue(Button.ForegroundProperty);
        _secondaryButton.Visibility = Visibility.Visible;
        _onSecondary = () => Finish(_openSettings);

        _primaryButton.Content = L("summary.openApp");
        _onPrimary = () => Finish(_openApp);
    }

    private static Grid OutcomeLine(string labelKey, string outcome) => outcome switch
    {
        OnboardingState.Outcome.Done => StatusLine(GlyphCheck, Ok, L(labelKey), L("summary.done")),
        OnboardingState.Outcome.Already => StatusLine(GlyphCheck, Ok, L(labelKey), L("summary.already")),
        OnboardingState.Outcome.Failed => StatusLine(GlyphWarn, Warn, L(labelKey), L("summary.failed")),
        _ => StatusLine(GlyphDash, Muted, L(labelKey), L("summary.skipped")),
    };
}
