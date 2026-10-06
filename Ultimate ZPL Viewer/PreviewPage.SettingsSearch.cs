using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// ── Searching the settings ──────────────────────────────────────────────────
// A box in the middle of the title bar while the settings are open, the way a
// browser's settings work: as soon as something is typed, no category is
// selected any more, and the page shows every setting that matches, from every
// category, as the cards themselves — set them right there, several in a row,
// without searching again. Choosing a category, or emptying the box, ends it.
//
// What is searched is what the cards say: titles, descriptions, the words of
// their options, and the title of the section they sit in — so it stays right
// in every language and never needs a list kept by hand.
public sealed partial class PreviewPage
{
    private bool _settingsSearchReady;
    private bool _searchShown;            // the results are on screen
    private string _settingsQuery = "";
    private string? _categoryBeforeSearch;
    private DispatcherTimer? _searchDebounce;
    private NavigationViewItem? _searchNavItem;
    private const string SearchNavTag = "__search";

    private void InitSettingsSearch()
    {
        if (_settingsSearchReady) return;
        _settingsSearchReady = true;

        // Ctrl+F in front of the settings goes to the box.
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        find.Invoked += (_, e) =>
        {
            (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.FocusSettingsSearch();
            e.Handled = true;
        };
        SettingsOverlay.KeyboardAccelerators.Add(find);
    }

    /// <summary>The title bar's box changed. Searched once typing pauses.</summary>
    private void OnSettingsSearchChanged(string text)
    {
        _settingsQuery = text;
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ShowSearchResults(); };
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ShowSearchResults()
    {
        var words = Fold(_settingsQuery).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { EndSettingsSearch(); return; }

        if (!_searchShown)
        {
            _categoryBeforeSearch = _currentSettingsTag;
            _searchShown = true;
        }
        // No category is the one on screen any more.
        // NavigationView will not let go of its selection: an invisible item takes it.
        if (_searchNavItem is null)
        {
            _searchNavItem = new NavigationViewItem { Tag = SearchNavTag, Visibility = Visibility.Collapsed };
            SettingsNav.MenuItems.Add(_searchNavItem);
        }
        if (!ReferenceEquals(SettingsNav.SelectedItem, _searchNavItem)) SettingsNav.SelectedItem = _searchNavItem;

        // The cards of a search are built once, when it starts, and every word typed
        // after that only picks among them: building every category again for each
        // one froze the box for ~180 ms. The cards stay live, so a value changed in
        // the results is still changed when the query changes.
        _searchPool ??= BuildSearchPool();
        if (_searchPage is not null)
            foreach (var old in _searchPage.Children.OfType<Grid>()) old.Children.Clear();
        var page = SettingsPanel();
        _searchPage = page;
        page.Children.Add(SettingsHeader(
            string.Format(SL("search.results"), _settingsQuery.Trim()), SL("search.resultsHint")));

        int found = 0;
        foreach (var item in SettingsNav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is not string tag) continue;
            var shownHere = _searchPool
                .Where(p => p.Tag == tag && words.All(p.Words.Contains))
                .Select(p => p.Card)
                .ToList();
            if (shownHere.Count == 0) continue;
            page.Children.Add(SubHeader(item.Content as string ?? tag));
            var grid = new Grid { ColumnSpacing = 8, Tag = shownHere };
            page.Children.Add(grid);
            found += shownHere.Count;
        }
        if (found == 0)
            page.Children.Add(new TextBlock
            {
                Text = SL("search.none"), Opacity = 0.7, Margin = new Thickness(0, 12, 0, 0),
            });

        // Cards in rows of up to three, every card of a row as tall as the tallest:
        // the columns follow the page's width, like the categories' cards.
        int columns = -1;
        void LayoutGrids()
        {
            double w = page.ActualWidth;
            if (w <= 0) return;
            int c = Math.Clamp((int)(w / MinCardWidth), 1, 3);
            if (c == columns) return;
            columns = c;
            foreach (var grid in page.Children.OfType<Grid>())
                if (grid.Tag is List<FrameworkElement> cards) FillResultGrid(grid, cards, c);
        }
        page.SizeChanged += (_, _) => LayoutGrids();
        page.Loaded += (_, _) => LayoutGrids();

        SettingsContentHost.Children.Clear();
        SettingsContentHost.Children.Add(page);
    }

    private List<(string Tag, FrameworkElement Card, string Words)>? _searchPool;
    private StackPanel? _searchPage;

    /// <summary>Every card of every category, out of its page and ready for a grid.</summary>
    private List<(string Tag, FrameworkElement Card, string Words)> BuildSearchPool()
    {
        var pool = new List<(string, FrameworkElement, string)>();
        foreach (var (tag, root) in CreateSettingsCategories())
        {
            var cards = new List<(FrameworkElement Card, List<string> Texts, string? Section, DependencyObject? Container)>();
            string? section = null;
            CollectSettingsCards(root, cards, null, ref section);
            foreach (var (card, texts, cardSection, container) in cards)
            {
                if (!DetachCard(card, container)) continue;
                // The grid sizes them: as wide as their column, as tall as their row.
                card.Width = double.NaN;
                card.MaxWidth = double.PositiveInfinity;
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                card.VerticalAlignment = VerticalAlignment.Stretch;
                pool.Add((tag, card, Fold(string.Join(" ", texts) + " " + cardSection)));
            }
        }
        return pool;
    }

    private static void FillResultGrid(Grid grid, List<FrameworkElement> cards, int columns)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < cards.Count; i++)
        {
            if (i % columns == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cards[i], i / columns);
            Grid.SetColumn(cards[i], i % columns);
            grid.Children.Add(cards[i]);
        }
    }

    // Takes a card out of the container it was found in (its Parent is not always
    // set: a tree that was never shown). False when it cannot be.
    private static bool DetachCard(FrameworkElement card, DependencyObject? container)
    {
        switch (container)
        {
            case Panel p: return p.Children.Remove(card);
            case Border b: b.Child = null; return true;
            case Viewbox v: v.Child = null; return true;
            case ContentControl c: c.Content = null; return true;
            default: return card.Parent is null;
        }
    }

    /// <summary>
    /// The box was emptied: back to the category that was on screen before the
    /// search, with controls built again so they show what was just changed.
    /// </summary>
    private void EndSettingsSearch()
    {
        if (!_searchShown) return;
        _searchShown = false;
        _searchPool = null; _searchPage = null;
        _settingsCategories = null;
        BuildSettingsCategories();
        var tag = _categoryBeforeSearch ?? "general";
        var nav = SettingsNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag)
                  ?? (NavigationViewItem)SettingsNav.MenuItems[0];
        if (ReferenceEquals(SettingsNav.SelectedItem, nav)) ShowSettingsCategory(tag);
        else SettingsNav.SelectedItem = nav;
    }

    /// <summary>A category was chosen while searching: the search ends, the box empties.</summary>
    private void LeaveSettingsSearch()
    {
        if (!_searchShown) return;
        _searchShown = false;
        _searchDebounce?.Stop();
        _settingsQuery = "";
        (AppWindowLookup.MainWindowForXamlRoot(XamlRoot) as MainWindow)?.ClearSettingsSearch();
        SetPaneSearchText("");
        // Rebuilt: the category must show what was changed in the results.
        _searchPool = null; _searchPage = null;
        _settingsCategories = null;
        BuildSettingsCategories();
    }

    // ── Full screen: the search over the settings ────────────────────────────
    // There is no title bar in full screen, and the bar that stands in for it
    // only shows under the pointer: the box moves into the page instead, centred
    // over the settings themselves.

    private Grid? _paneSearchHost;
    private TextBox? _paneSearchBox;
    private bool _settingPaneText;

    internal string SettingsQuery => _settingsQuery;

    internal void SetFullScreenSettings(bool on)
    {
        if (on && _paneSearchHost is null)
        {
            (_paneSearchHost, _paneSearchBox) = SettingsSearchBox.Create(480);
            SettingsSearchSlot.Child = _paneSearchHost;
            _paneSearchBox.TextChanged += (_, _) =>
            {
                if (!_settingPaneText) OnSettingsSearchChanged(_paneSearchBox.Text);
            };
        }
        if (on && _paneSearchBox is not null) SetPaneSearchText(_settingsQuery);
        SettingsSearchSlot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetPaneSearchText(string text)
    {
        if (_paneSearchBox is null) return;
        _settingPaneText = true;
        _paneSearchBox.Text = text;
        _settingPaneText = false;
    }

    internal void FocusPaneSearch() => _paneSearchBox?.Focus(FocusState.Keyboard);

    // Every settings card of a category, with the words it shows and the title of
    // the section it sits in (a search for a section's name finds its cards).
    private static void CollectSettingsCards(DependencyObject node,
        List<(FrameworkElement, List<string>, string?, DependencyObject?)> cards, List<string>? into, ref string? section, DependencyObject? container = null)
    {
        if (node is UIElement { Visibility: Visibility.Collapsed }) return;

        if (into is null && node is FrameworkElement card && (card.Tag as string) == "settings-card")
        {
            var texts = new List<string>();
            cards.Add((card, texts, section, container));
            into = texts;
        }
        else if (into is null && node is TextBlock { FontWeight.Weight: >= 600 } heading
                 && !string.IsNullOrWhiteSpace(heading.Text) && heading.FontSize < 20)
        {
            section = heading.Text;
            return;
        }

        switch (node)
        {
            case TextBlock tb:
                if (into is not null && !string.IsNullOrWhiteSpace(tb.Text)) into.Add(tb.Text);
                return;
            case ToggleSwitch ts:
                if (into is not null && ts.Header is string th) into.Add(th);
                return;
            case ComboBox cb:
                if (into is not null)
                    foreach (var it in cb.Items)
                    {
                        var s = (it as ComboBoxItem)?.Content as string ?? it as string;
                        if (!string.IsNullOrEmpty(s)) into.Add(s);
                    }
                return;
            case ContentControl cc:
                if (into is not null && cc.Content is string text) into.Add(text);
                if (cc.Content is DependencyObject content) CollectSettingsCards(content, cards, into, ref section, cc);
                return;
            case Panel p:
                foreach (var child in p.Children) CollectSettingsCards(child, cards, into, ref section, p);
                return;
            case Border b when b.Child is not null:
                CollectSettingsCards(b.Child, cards, into, ref section, b);
                return;
            case Viewbox v when v.Child is not null:
                CollectSettingsCards(v.Child, cards, into, ref section, v);
                return;
        }
    }

    // Case and accents ignored, punctuation as spaces.
    private static string Fold(string text)
    {
        var d = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString();
    }
}
