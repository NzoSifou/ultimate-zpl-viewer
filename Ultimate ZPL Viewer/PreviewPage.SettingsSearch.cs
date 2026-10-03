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
// A box at the top of the settings' left pane. What it searches is what is on
// the cards themselves — titles, descriptions, the words of their options — so
// it stays right in every language and never needs a list kept by hand. Picking
// a result opens the category and brings the card into view, lit up for a
// moment so the eye finds it.
public sealed partial class PreviewPage
{
    private sealed record SettingsHit(string Tag, FrameworkElement Target, string Title, string Category, int Rank);

    private bool _settingsSearchReady;

    private void InitSettingsSearch()
    {
        if (_settingsSearchReady) return;
        _settingsSearchReady = true;
        var box = SettingsSearchBox;
        box.UpdateTextOnSelect = false;
        box.TextChanged += (s, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            s.ItemsSource = SettingsSuggestions(s.Text);
        };
        // Going down the list with the arrows only points; a click or Enter goes.
        box.QuerySubmitted += (s, e) =>
        {
            // Enter without picking: the best match.
            var hit = e.ChosenSuggestion is FrameworkElement { Tag: SettingsHit chosen }
                ? chosen
                : SearchSettings(s.Text).FirstOrDefault();
            if (hit is not null) GoToSettingsHit(hit);
        };

        // Ctrl+F in front of the settings goes to the box.
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.F,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        find.Invoked += (_, e) => { box.Focus(FocusState.Keyboard); e.Handled = true; };
        SettingsOverlay.KeyboardAccelerators.Add(find);
    }

    private void LocalizeSettingsSearch()
    {
        SettingsSearchBox.PlaceholderText = SL("search.placeholder");
    }

    private List<object> SettingsSuggestions(string query)
    {
        var items = new List<object>();
        if (string.IsNullOrWhiteSpace(query)) return items;
        foreach (var hit in SearchSettings(query).Take(12))
        {
            var row = new StackPanel { Tag = hit, Padding = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = hit.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock
            {
                Text = hit.Category, FontSize = 12, Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            items.Add(row);
        }
        if (items.Count == 0)
            items.Add(new TextBlock { Text = SL("search.none"), Opacity = 0.6, Padding = new Thickness(0, 4, 0, 4) });
        return items;
    }

    // Every word typed must be found (case and accents ignored). A word in the
    // card's title ranks it before one only in its description, and that before
    // one met in the card's options.
    private List<SettingsHit> SearchSettings(string query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<SettingsHit>();
        if (words.Length == 0 || _settingsCategories is null) return hits;

        foreach (var item in SettingsNav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is not string tag || !_settingsCategories.TryGetValue(tag, out var root)) continue;
            var category = item.Content as string ?? tag;
            var foldedCategory = Fold(category);

            // The category itself: "impression" finds the Printing page.
            if (words.All(foldedCategory.Contains) && root is FrameworkElement top)
                hits.Add(new SettingsHit(tag, top, category, category, 0));

            var targets = new List<(FrameworkElement Target, List<string> Texts)>();
            CollectSearchTargets(root, targets, null);
            foreach (var (target, texts) in targets)
            {
                if (texts.Count == 0) continue;
                var title = Fold(texts[0]);
                var description = texts.Count > 1 ? Fold(texts[1]) : "";
                var all = Fold(string.Join(" ", texts)) + " " + foldedCategory;
                if (!words.All(all.Contains)) continue;
                int rank = words.All(title.Contains) ? 1
                         : words.All(w => title.Contains(w) || description.Contains(w)) ? 2 : 3;
                hits.Add(new SettingsHit(tag, target, texts[0], category, rank));
            }
        }
        // Stable: same rank keeps the order of the pages and of the cards in them.
        return hits.Select((h, i) => (h, i)).OrderBy(x => x.h.Rank).ThenBy(x => x.i).Select(x => x.h).ToList();
    }

    // The things a search can land on: every settings card, and every
    // sub-section title (some pages, like the editor's, are laid out in sections
    // rather than cards). Their texts are gathered title first.
    private static void CollectSearchTargets(DependencyObject node,
        List<(FrameworkElement, List<string>)> targets, List<string>? into)
    {
        if (node is UIElement { Visibility: Visibility.Collapsed }) return;

        if (node is FrameworkElement card && (card.Tag as string) == "settings-card")
        {
            var texts = new List<string>();
            targets.Add((card, texts));
            into = texts;
        }
        else if (into is null && node is TextBlock { FontWeight.Weight: >= 600 } heading
                 && !string.IsNullOrWhiteSpace(heading.Text) && heading.FontSize < 20)
        {
            targets.Add((heading, new List<string> { heading.Text }));
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
                if (cc.Content is DependencyObject content) CollectSearchTargets(content, targets, into);
                return;
            case Panel p:
                foreach (var child in p.Children) CollectSearchTargets(child, targets, into);
                return;
            case Border b when b.Child is not null:
                CollectSearchTargets(b.Child, targets, into);
                return;
            case Viewbox v when v.Child is not null:
                CollectSearchTargets(v.Child, targets, into);
                return;
        }
    }

    private static string Fold(string text)
    {
        var d = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString();
    }

    private void GoToSettingsHit(SettingsHit hit)
    {
        var nav = SettingsNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == hit.Tag);
        if (nav is not null && !ReferenceEquals(SettingsNav.SelectedItem, nav)) SettingsNav.SelectedItem = nav;
        else ShowSettingsCategory(hit.Tag);

        // The page has just been put in: wait for it to be laid out before
        // scrolling to the card.
        var target = hit.Target;
        void Reveal(object? s, object e)
        {
            target.LayoutUpdated -= Reveal;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                target.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.25,
                });
                FlashSettingsTarget(target);
            });
        }
        if (target.ActualHeight > 0) Reveal(null, EventArgs.Empty);
        else target.LayoutUpdated += Reveal;
    }

    // Lights the card's frame in the accent colour for a moment.
    private readonly HashSet<Border> _flashing = new();

    private void FlashSettingsTarget(FrameworkElement target)
    {
        if (target is not Border card || !_flashing.Add(card)) return;
        var oldBrush = card.BorderBrush;
        var oldThickness = card.BorderThickness;
        var oldPadding = card.Padding;
        // Two pixels of frame instead of one, taken off the padding so nothing moves.
        card.BorderBrush = new SolidColorBrush(AccentColorService.Current);
        card.BorderThickness = new Thickness(2);
        card.Padding = new Thickness(
            Math.Max(0, oldPadding.Left - 1), Math.Max(0, oldPadding.Top - 1),
            Math.Max(0, oldPadding.Right - 1), Math.Max(0, oldPadding.Bottom - 1));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            card.BorderBrush = oldBrush;
            card.BorderThickness = oldThickness;
            card.Padding = oldPadding;
            _flashing.Remove(card);
        };
        timer.Start();
    }
}
