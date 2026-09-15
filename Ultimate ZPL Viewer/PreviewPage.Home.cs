using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ultimate_ZPL_Viewer;

// The home page: what a window shows when it holds no document.
//
// It is not a document with the furniture turned off — the toolbar, the tabs, the
// editor and the preview are all COLLAPSED while it is up, so the window's own
// acrylic is what lies behind it and there is no grid, no rail, no chrome to see
// through. The page itself only tints that acrylic: a wash of the accent colour
// in the top corner fading to nothing, so the material stays visible.
//
// What it offers is the short list of things there is to do with no document
// open: start one, open one, look at the example — and the recent files, which
// are the fastest way back to yesterday's work.
public sealed partial class PreviewPage
{
    private bool _homeVisible;

    /// <summary>True while this window is showing the home page instead of a document.</summary>
    public bool IsHome => _homeVisible;

    // Shows or hides the home page. Everything a document needs is collapsed
    // rather than merely covered: a half-transparent page over a live preview
    // would show the grid through it, and the point of this page is that there is
    // nothing behind it.
    private void SetHomeVisible(bool on)
    {
        _homeVisible = on;
        if (on) BuildHomePage();

        HomeOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ContentArea.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        DocTabs.Visibility = on || DocTabs.TabItems.Count == 0
            ? Visibility.Collapsed : Visibility.Visible;
        ApplyToolbarVisibility();

        ApplyHomeChrome();
    }

    // The window's own furniture. Split out because it cannot be done while the
    // page is still being navigated into: XamlRoot is null then, and asking for
    // "the window of this root" would answer with the application's first window.
    private void ApplyHomeChrome()
    {
        if (XamlRoot is null) return;
        var window = AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow;
        window?.SetHomeMode(_homeVisible);
        if (_homeVisible) window?.SetDocumentTitle("Ultimate ZPL Viewer");
        else UpdateDocumentTitle();
    }

    // ── The page ─────────────────────────────────────────────────────────────

    private void BuildHomePage()
    {
        HomeOverlay.Children.Clear();
        HomeOverlay.Background = HomeWash();

        var scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(40, 34, 40, 40),
        };

        var column = new StackPanel
        {
            MaxWidth = 940,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 26,
        };

        column.Children.Add(HomeHero());
        column.Children.Add(HomeActions());

        var lower = new Grid { ColumnSpacing = 20 };
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var recent = HomeRecent();
        var tips = HomeTips();
        Grid.SetColumn(recent, 0);
        Grid.SetColumn(tips, 1);
        lower.Children.Add(recent);
        lower.Children.Add(tips);
        column.Children.Add(lower);

        column.Children.Add(HomeShortcutStrip());

        scroll.Content = column;
        HomeOverlay.Children.Add(scroll);
    }

    // A tint of the accent in the top-left corner, gone by the middle of the page.
    // Half-transparent throughout, so what shows through is the window's acrylic
    // rather than a flat colour painted over it.
    private static Brush HomeWash()
    {
        var accent = Application.Current.Resources["SystemAccentColor"] is Color c ? c : Colors.DodgerBlue;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0.85, 1),
        };
        gradient.GradientStops.Add(new GradientStop { Offset = 0.0, Color = Color.FromArgb(0x4A, accent.R, accent.G, accent.B) });
        gradient.GradientStops.Add(new GradientStop { Offset = 0.45, Color = Color.FromArgb(0x16, accent.R, accent.G, accent.B) });
        gradient.GradientStops.Add(new GradientStop { Offset = 1.0, Color = Color.FromArgb(0x00, accent.R, accent.G, accent.B) });
        return gradient;
    }

    private static string HL(string key) => LocalizationService.Get("home." + key);

    private StackPanel HomeHero()
    {
        var hero = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        titleRow.Children.Add(new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "", FontSize = 22,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "Ultimate ZPL Viewer",
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        hero.Children.Add(titleRow);

        hero.Children.Add(new TextBlock
        {
            Text = HL("tagline"),
            FontSize = 15,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(60, 0, 0, 0),
        });
        return hero;
    }

    // The three ways to get a document on screen, as cards wide enough to read.
    private Grid HomeActions()
    {
        var row = new Grid { ColumnSpacing = 14 };
        for (int i = 0; i < 3; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cards = new[]
        {
            HomeActionCard("", HL("actions.new.title"), HL("actions.new.desc"), true,
                () => _ = NewDocumentAsync()),
            HomeActionCard("", HL("actions.open.title"), HL("actions.open.desc"), false,
                () => _ = OpenFileFromPickerAsync()),
            HomeActionCard("", HL("actions.sample.title"), HL("actions.sample.desc"), false,
                OpenSampleLabel),
        };
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetColumn(cards[i], i);
            row.Children.Add(cards[i]);
        }
        return row;
    }

    private static Button HomeActionCard(string glyph, string title, string description,
                                         bool accent, Action invoke)
    {
        var content = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,   // the words stay at the top of a taller card
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Left });
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock
        {
            Text = description, FontSize = 12, Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(16, 14, 16, 16),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            // All three fill the row, so the one whose description fits on a single
            // line is not left a size smaller than its neighbours.
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => invoke();
        return button;
    }

    // The files this user actually works on. Missing ones are not listed: a home
    // page that offers a file it cannot open is worse than a shorter list.
    private FrameworkElement HomeRecent()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(HomeSectionTitle(HL("recent.title")));

        var files = _settings.RecentFiles
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(File.Exists)
            .Take(6)
            .ToList();

        if (files.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = HL("recent.empty"),
                Opacity = 0.6, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 0, 0),
            });
            return panel;
        }

        var list = new StackPanel { Spacing = 2 };
        foreach (var path in files) list.Children.Add(HomeRecentRow(path));
        panel.Children.Add(list);
        return panel;
    }

    private Button HomeRecentRow(string path)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { Glyph = "", FontSize = 14, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var names = new StackPanel();
        names.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path), FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        names.Children.Add(new TextBlock
        {
            Text = Path.GetDirectoryName(path) ?? "", FontSize = 11, Opacity = 0.55,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(names, 1);
        row.Children.Add(names);

        var button = new Button
        {
            Content = row,
            Padding = new Thickness(10, 7, 10, 7),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, path);
        button.Click += (_, _) => _ = OpenPathFromHomeAsync(path);
        return button;
    }

    // What the application is for, in four lines. Someone opening it for the first
    // time should not have to go hunting through the toolbar to find out.
    private FrameworkElement HomeTips()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(HomeSectionTitle(HL("tips.title")));

        var list = new StackPanel { Spacing = 9, Margin = new Thickness(2, 2, 0, 0) };
        foreach (var (glyph, key) in new[]
                 {
                     ("", "edit"), ("", "transform"),
                     ("", "print"), ("", "export"),
                 })
            list.Children.Add(HomeTipRow(glyph, HL("tips." + key)));
        panel.Children.Add(list);
        return panel;
    }

    private static Grid HomeTipRow(string glyph, string text)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);
        var label = new TextBlock { Text = text, FontSize = 13, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private static TextBlock HomeSectionTitle(string text) => new()
    {
        Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Opacity = 0.6,
    };

    // The line that teaches the one shortcut worth knowing before any other: the
    // one that lists all the others.
    private UIElement HomeShortcutStrip()
    {
        var strip = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new FontIcon { Glyph = "", FontSize = 15, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(KeyCap("Ctrl"));
        row.Children.Add(new TextBlock { Text = "+", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(KeyCap("/"));
        row.Children.Add(new TextBlock
        {
            Text = HL("shortcuts.hint"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13, Opacity = 0.85, TextWrapping = TextWrapping.Wrap,
        });

        var open = new HyperlinkButton
        {
            Content = HL("shortcuts.open"),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        open.Click += (_, _) => _ = ShowShortcutsHelpAsync();

        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(row, 0);
        Grid.SetColumn(open, 1);
        host.Children.Add(row);
        host.Children.Add(open);
        strip.Child = host;
        return strip;
    }

    private static Border KeyCap(string text) => new()
    {
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 1, 7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold },
    };

    // ── What the buttons do ──────────────────────────────────────────────────

    /// <summary>Opens the example label in a tab of its own, leaving the home page.</summary>
    private void OpenSampleLabel()
    {
        AddTabAndActivate(null);
        SetEditorText(_settings.SampleLabelText());
        _isDirty = true;            // never saved, like any new document
        UpdateDocumentTitle();
    }

    // ── The settings behind it ───────────────────────────────────────────────

    // Two questions, both about this page: which label the example button opens,
    // and whether closing the last document comes back here or ends the window.
    private static string Crlf(string text) =>
        text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private UIElement BuildHomeSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("home"));

        // The custom ZPL is only asked for when it is going to be used, but the box
        // keeps whatever was typed when the choice goes back to the default: nobody
        // should lose a label by changing their mind twice.
        var code = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 190,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Visibility = _settings.SampleLabelMode == 1 ? Visibility.Visible : Visibility.Collapsed,
        };
        // AFTER AcceptsReturn, and in CRLF. A TextBox that does not yet accept
        // returns keeps only what comes before the first line break, so a label
        // assigned in the initializer above arrived here as "^XA" and nothing else.
        code.Text = Crlf(string.IsNullOrEmpty(_settings.SampleLabelZpl)
            ? AppSettings.DefaultSampleZpl : _settings.SampleLabelZpl);
        ScrollViewer.SetHorizontalScrollBarVisibility(code, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(code, ScrollBarVisibility.Auto);
        code.TextChanged += (_, _) =>
        {
            _settings.SampleLabelZpl = code.Text;
            _settings.Save();
        };

        var which = new ComboBox
        {
            MinWidth = 220,
            ItemsSource = SA("home.opt.sample"),
            SelectedIndex = Math.Clamp(_settings.SampleLabelMode, 0, 1),
        };
        which.SelectionChanged += (_, _) =>
        {
            _settings.SampleLabelMode = which.SelectedIndex;
            if (which.SelectedIndex == 1 && string.IsNullOrWhiteSpace(_settings.SampleLabelZpl))
                _settings.SampleLabelZpl = code.Text;
            _settings.Save();
            code.Visibility = which.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };

        var sampleCard = MakeCard("", SL("home.cards.sample.title"),
            SL("home.cards.sample.desc"), which, code);
        sampleCard.MaxWidth = 900;
        panel.Children.Add(sampleCard);

        var lastTab = new ComboBox
        {
            MinWidth = 260,
            ItemsSource = SA("home.opt.lastTab"),
            SelectedIndex = Math.Clamp(_settings.LastTabClosed, 0, 1),
        };
        lastTab.SelectionChanged += (_, _) =>
        {
            _settings.LastTabClosed = lastTab.SelectedIndex;
            _settings.Save();
        };
        var lastCard = MakeCard("", SL("home.cards.lastTab.title"),
            SL("home.cards.lastTab.desc"), lastTab);
        lastCard.MaxWidth = 900;
        panel.Children.Add(lastCard);

        return panel;
    }

    private async System.Threading.Tasks.Task OpenPathFromHomeAsync(string path)
    {
        await OpenPathAsync(path);
        // A path that has gone missing is dropped from the list by OpenPathAsync;
        // the page is redrawn so it stops offering it.
        if (_homeVisible) BuildHomePage();
    }
}
