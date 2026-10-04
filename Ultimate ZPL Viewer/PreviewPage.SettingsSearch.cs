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

        // A fresh set of controls, the matching cards taken out of it.
        var fresh = CreateSettingsCategories();
        var page = SettingsPanel();
        page.Children.Add(SettingsHeader(
            string.Format(SL("search.results"), _settingsQuery.Trim()), SL("search.resultsHint")));

        int found = 0;
        foreach (var item in SettingsNav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is not string tag || !fresh.TryGetValue(tag, out var root)) continue;
            var cards = new List<(FrameworkElement Card, List<string> Texts, string? Section, DependencyObject? Container)>();
            string? section = null;
            CollectSettingsCards(root, cards, null, ref section);

            var matches = cards
                .Where(c => words.All(Fold(string.Join(" ", c.Texts) + " " + c.Section).Contains))
                .ToList();
            if (matches.Count == 0) continue;

            page.Children.Add(SubHeader(item.Content as string ?? tag));
            var wrap = new ToolbarWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 0 };
            int shownHere = 0;
            foreach (var (card, _, _, container) in matches)
            {
                if (!DetachCard(card, container)) continue;
                card.MaxWidth = double.PositiveInfinity;
                card.HorizontalAlignment = HorizontalAlignment.Left;
                wrap.Children.Add(card);
                shownHere++;
            }
            if (shownHere == 0) { page.Children.RemoveAt(page.Children.Count - 1); continue; }
            page.Children.Add(wrap);
            found += shownHere;
        }
        if (found == 0)
            page.Children.Add(new TextBlock
            {
                Text = SL("search.none"), Opacity = 0.7, Margin = new Thickness(0, 12, 0, 0),
            });

        SettingsContentHost.Children.Clear();
        SettingsContentHost.Children.Add(WithThirdWidthCards(page));
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
        // Rebuilt: the category must show what was changed in the results.
        _settingsCategories = null;
        BuildSettingsCategories();
    }

    /// <summary>In full screen, the settings sit under the bar that stays on top of them.</summary>
    internal void SetSettingsTopInset(double inset) => SettingsOverlay.Margin = new Thickness(0, inset, 0, 0);

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
