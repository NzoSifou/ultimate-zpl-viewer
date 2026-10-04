using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Ultimate_ZPL_Viewer;

// ── Splitting the preview ───────────────────────────────────────────────────
// Several documents side by side, one above the other, or both: up to four
// previews at once, never more — a third column or row would leave each label
// too small to read. The code editor stays one: it shows the document of the
// pane that has the focus, and so does the toolbar. The focused pane is the
// live preview (zoom, rulers, edit mode, everything); the others are drawn from
// their document as it stands, and a click on one makes it the focused one.
//
// The arrangement is a list of groups — columns, or rows — of one or two panes
// each: two columns of which one is cut in two, two rows of which one is cut in
// two, a plain 2 × 2. That is every layout of up to four panes on a 2 × 2 grid,
// and none that would need a third column or row. Where a document can go is
// therefore one of the grid's halves or quarters still free, and both the tab
// menu and a dragged tab offer exactly those.
//
// A document is in one pane at most. Choosing a tab that is not on screen puts
// it in the focused pane, as an editor group would.
public sealed partial class PreviewPage
{
    private const int MaxSplitPanes = 4;

    // False: the groups are columns (left to right), their panes stacked top to
    // bottom. True: the groups are rows (top to bottom), their panes side by side.
    private bool _splitRows;
    // Empty while the preview is not split.
    private List<List<DocTab>> _splitGroups = new();
    private readonly Dictionary<DocTab, SplitPane> _splitPanes = new();
    // The document the live surface showed last: the pane a newly chosen tab replaces.
    private DocTab? _splitFocus;
    private Border? _splitDropHint;

    private sealed class SplitPane
    {
        public required Border Header { get; init; }
        public required TextBlock Title { get; init; }
        public required Border Underline { get; init; }
        public required Border Body { get; init; }
        public required Canvas Canvas { get; init; }
        public string? RenderedKey { get; set; }
    }

    /// <summary>
    /// One place a document can be shown: the arrangement it leads to, and the part
    /// of the preview (as fractions of it) the document will then occupy.
    /// </summary>
    private sealed record SplitOption(string Key, bool Rows, List<List<DocTab>> Groups, Rect Cell);

    private static string SpL(string key) => LocalizationService.Get("split." + key);

    private int SplitCount => _splitGroups.Sum(g => g.Count);
    private bool IsSplit => SplitCount > 1;

    private void InitSplit()
    {
        SplitHost.AllowDrop = true;
        SplitHost.DragOver += SplitHost_DragOver;
        SplitHost.DragLeave += (_, _) => HideSplitDropHint();
        SplitHost.Drop += SplitHost_Drop;
    }

    private static (int Group, int Index)? FindSlot(List<List<DocTab>> groups, DocTab tab)
    {
        for (int g = 0; g < groups.Count; g++)
            for (int i = 0; i < groups[g].Count; i++)
                if (ReferenceEquals(groups[g][i], tab)) return (g, i);
        return null;
    }

    private static List<List<DocTab>> CopyGroups(List<List<DocTab>> groups)
        => groups.Select(g => g.ToList()).ToList();

    /// <summary>
    /// One way of writing each arrangement: no empty group, and a lone group of two
    /// written the other way round (one column of two panes IS two rows of one).
    /// </summary>
    private static (bool Rows, List<List<DocTab>> Groups) Canonical(bool rows, List<List<DocTab>> groups)
    {
        groups = groups.Where(g => g.Count > 0).Select(g => g.ToList()).ToList();
        if (groups.Count == 1 && groups[0].Count == 2)
            return (!rows, new List<List<DocTab>> { new() { groups[0][0] }, new() { groups[0][1] } });
        if (groups.Count <= 1) return (false, groups);
        return (rows, groups);
    }

    private IEnumerable<DocTab> OpenTabs()
        => DocTabs.TabItems.OfType<TabViewItem>().Select(i => i.Tag).OfType<DocTab>();

    private TabViewItem? ItemOf(DocTab tab)
        => DocTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, tab));

    // ── Where a document can go ──────────────────────────────────────────────

    /// <summary>
    /// The places <paramref name="tab"/> can be shown, given what is on screen now.
    /// One pane: left or right of it, above or below. Two: any of the four
    /// quarters, the pane on that side giving up half its room. Three: the two
    /// quarters of the pane that still has a half to itself. Four: none.
    /// A document already on screen is counted as leaving its pane first.
    /// </summary>
    private List<SplitOption> SplitOptions(DocTab tab)
    {
        var options = new List<SplitOption>();
        if (_activeTab is null) return options;

        var groups = _splitGroups.Count > 0
            ? CopyGroups(_splitGroups)
            : new List<List<DocTab>> { new() { _activeTab } };
        if (FindSlot(groups, tab) is { } from) groups[from.Group].RemoveAt(from.Index);
        var (rows, layout) = Canonical(_splitGroups.Count > 0 && _splitRows, groups);

        int total = layout.Sum(g => g.Count);
        if (total == 0)
        {
            // It was the only document on screen: another one stays beside it.
            var other = OpenTabs().FirstOrDefault(t => !ReferenceEquals(t, tab));
            if (other is null) return options;
            layout = new List<List<DocTab>> { new() { other } };
            rows = false;
            total = 1;
        }
        if (total >= MaxSplitPanes) return options;

        if (total == 1)
        {
            var a = layout[0][0];
            options.Add(new("left",   false, new() { new() { tab }, new() { a } }, new Rect(0, 0, 0.5, 1)));
            options.Add(new("right",  false, new() { new() { a }, new() { tab } }, new Rect(0.5, 0, 0.5, 1)));
            options.Add(new("top",    true,  new() { new() { tab }, new() { a } }, new Rect(0, 0, 1, 0.5)));
            options.Add(new("bottom", true,  new() { new() { a }, new() { tab } }, new Rect(0, 0.5, 1, 0.5)));
            return options;
        }

        // Two or three panes: a pane that has a half to itself shares it.
        for (int g = 0; g < layout.Count; g++)
        {
            if (layout[g].Count != 1) continue;
            for (int i = 0; i < 2; i++)
            {
                var plan = CopyGroups(layout);
                plan[g].Insert(i, tab);
                int h = rows ? i : g;      // 0 = left, 1 = right
                int v = rows ? g : i;      // 0 = top, 1 = bottom
                var key = (v == 0 ? "top" : "bottom") + (h == 0 ? "Left" : "Right");
                options.Add(new(key, rows, plan, new Rect(h * 0.5, v * 0.5, 0.5, 0.5)));
            }
        }
        // Reading order: top left, top right, bottom left, bottom right.
        return options.OrderBy(o => o.Cell.Y).ThenBy(o => o.Cell.X).ToList();
    }

    private void ApplySplit(bool rows, List<List<DocTab>> groups, DocTab focus)
    {
        (_splitRows, _splitGroups) = Canonical(rows, groups);
        if (SplitCount <= 1) _splitGroups.Clear();
        if (ReferenceEquals(focus, _activeTab)) SyncSplit();
        else if (ItemOf(focus) is { } item) DocTabs.SelectedItem = item;   // → ActivateTab → SyncSplit
    }

    private void ApplySplitOption(SplitOption option, DocTab tab) => ApplySplit(option.Rows, option.Groups, tab);

    /// <summary>The arrangement with <paramref name="tab"/> in the pane of
    /// <paramref name="anchor"/> (the two swap when both are on screen).</summary>
    private List<List<DocTab>>? PlanReplace(DocTab tab, DocTab anchor)
    {
        if (ReferenceEquals(tab, anchor) || _splitGroups.Count == 0) return null;
        var groups = CopyGroups(_splitGroups);
        if (FindSlot(groups, anchor) is not { } a) return null;
        if (FindSlot(groups, tab) is { } t) groups[t.Group][t.Index] = anchor;
        groups[a.Group][a.Index] = tab;
        return groups;
    }

    private void Unsplit()
    {
        _splitGroups.Clear();
        LayoutSplit();
    }

    /// <summary>Takes a document out of the split; it stays open in its tab.</summary>
    private void ClosePane(DocTab tab)
    {
        if (FindSlot(_splitGroups, tab) is not { } slot) return;
        _splitGroups[slot.Group].RemoveAt(slot.Index);
        (_splitRows, _splitGroups) = Canonical(_splitRows, _splitGroups);
        if (SplitCount <= 1)
        {
            var left = _splitGroups.SelectMany(g => g).FirstOrDefault();
            _splitGroups.Clear();
            if (left is not null && !ReferenceEquals(left, _activeTab) && ItemOf(left) is { } keep)
            {
                DocTabs.SelectedItem = keep;
                return;
            }
            LayoutSplit();
            return;
        }
        // The focused pane went: the focus moves to the pane nearest to it.
        if (ReferenceEquals(tab, _activeTab))
        {
            int g = Math.Min(slot.Group, _splitGroups.Count - 1);
            int i = Math.Min(slot.Index, _splitGroups[g].Count - 1);
            _splitFocus = _splitGroups[g][i];
            if (ItemOf(_splitGroups[g][i]) is { } next) { DocTabs.SelectedItem = next; return; }
        }
        LayoutSplit();
    }

    private void FocusPane(DocTab tab)
    {
        if (ReferenceEquals(tab, _activeTab)) return;
        if (ItemOf(tab) is { } item) DocTabs.SelectedItem = item;
    }

    // ── Keeping the arrangement in step with the tabs ────────────────────────

    /// <summary>
    /// Called whenever the active document or the set of tabs changes: a closed
    /// document leaves its pane (or hands it to the document now active), and a
    /// document chosen in the strip that is not on screen takes the focused pane.
    /// </summary>
    private void SyncSplit()
    {
        if (_splitGroups.Count == 0)
        {
            _splitFocus = _activeTab;
            if (_splitPanes.Count > 0) LayoutSplit();
            return;
        }

        var open = OpenTabs().ToHashSet();
        bool activeShown = _activeTab is not null && FindSlot(_splitGroups, _activeTab) is not null;
        for (int g = 0; g < _splitGroups.Count; g++)
            for (int i = 0; i < _splitGroups[g].Count; i++)
            {
                if (open.Contains(_splitGroups[g][i])) continue;
                if (!activeShown && _activeTab is not null)
                {
                    _splitGroups[g][i] = _activeTab;   // the one that replaced it takes its place
                    activeShown = true;
                }
                else _splitGroups[g].RemoveAt(i--);
            }
        (_splitRows, _splitGroups) = Canonical(_splitRows, _splitGroups);

        if (_activeTab is not null && !activeShown && _splitGroups.Count > 0)
        {
            var slot = _splitFocus is not null ? FindSlot(_splitGroups, _splitFocus) : null;
            var (g, i) = slot ?? (0, 0);
            _splitGroups[g][i] = _activeTab;
        }
        if (SplitCount <= 1) _splitGroups.Clear();
        _splitFocus = _activeTab;
        LayoutSplit();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private void LayoutSplit()
    {
        // The live preview's frame changes size: once laid out, it fits again.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => { ApplyDefaultZoom(); DrawRulers(); });
        foreach (var child in SplitHost.Children.Where(c => !ReferenceEquals(c, PreviewSurface)).ToList())
            SplitHost.Children.Remove(child);
        SplitHost.ColumnDefinitions.Clear();
        SplitHost.RowDefinitions.Clear();

        if (!IsSplit)
        {
            _splitPanes.Clear();
            SplitHost.ColumnSpacing = SplitHost.RowSpacing = 0;
            PlaceInSplit(PreviewSurface, 0, 0, 1, 1);
            return;
        }

        // One-pixel gaps through which the host's own colour draws the dividers.
        SplitHost.ColumnSpacing = 1;
        SplitHost.RowSpacing = 0;
        int inner = _splitGroups.Max(g => g.Count);
        int columns = _splitRows ? inner : _splitGroups.Count;
        int rowSlots = _splitRows ? _splitGroups.Count : inner;
        for (int c = 0; c < columns; c++)
            SplitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rowSlots; r++)
        {
            SplitHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SplitHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        var shown = new HashSet<DocTab>();
        for (int g = 0; g < _splitGroups.Count; g++)
        {
            var group = _splitGroups[g];
            for (int i = 0; i < group.Count; i++)
            {
                var tab = group[i];
                shown.Add(tab);
                var pane = PaneFor(tab);
                bool focused = ReferenceEquals(tab, _activeTab);
                int col = _splitRows ? i : g;
                int rowSlot = _splitRows ? g : i;
                // A pane alone in its group takes the group's whole length.
                bool lone = group.Count == 1;
                int colSpan = _splitRows && lone ? columns : 1;
                int bodyRowSpan = !_splitRows && lone ? rowSlots * 2 - 1 : 1;

                pane.Title.Text = TabTitle(tab);
                pane.Title.Opacity = focused ? 1 : 0.7;
                pane.Underline.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
                // A divider above a pane of the second row.
                pane.Header.BorderThickness = new Thickness(0, rowSlot > 0 ? 1 : 0, 0, 1);
                PlaceInSplit(pane.Header, col, rowSlot * 2, 1, colSpan);

                if (focused)
                {
                    PlaceInSplit(PreviewSurface, col, rowSlot * 2 + 1, bodyRowSpan, colSpan);
                }
                else
                {
                    pane.Body.Background = PreviewSurface.Background;
                    PlaceInSplit(pane.Body, col, rowSlot * 2 + 1, bodyRowSpan, colSpan);
                    RenderPane(pane, tab);
                }
            }
        }
        foreach (var gone in _splitPanes.Keys.Where(t => !shown.Contains(t)).ToList())
            _splitPanes.Remove(gone);
    }

    private void PlaceInSplit(FrameworkElement element, int col, int row, int rowSpan, int colSpan)
    {
        Grid.SetColumn(element, col);
        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, colSpan);
        if (!SplitHost.Children.Contains(element)) SplitHost.Children.Add(element);
    }

    private SplitPane PaneFor(DocTab tab)
    {
        if (_splitPanes.TryGetValue(tab, out var existing)) return existing;

        var title = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Width = 26, Height = 22, Padding = new Thickness(0), MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "", FontSize = 10 },
        };
        ToolTipService.SetToolTip(close, TipBlock(SpL("closePane")));
        close.Click += (_, _) => ClosePane(tab);

        var row = new Grid { Height = 30, Padding = new Thickness(12, 0, 4, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(title);
        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        var underline = new Border
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(AccentColorService.Current),
        };
        var headerGrid = new Grid();
        headerGrid.Children.Add(row);
        headerGrid.Children.Add(underline);

        var header = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Child = headerGrid,
        };
        header.Tapped += (_, _) => FocusPane(tab);

        var canvas = new Canvas();
        var body = new Border
        {
            Child = new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(24),
            },
        };
        body.Tapped += (_, _) => FocusPane(tab);
        ToolTipService.SetToolTip(body, TipBlock(SpL("focusTip")));

        var pane = new SplitPane { Header = header, Title = title, Underline = underline, Body = body, Canvas = canvas };
        _splitPanes[tab] = pane;
        return pane;
    }

    // A pane out of focus shows its document as it was left: drawn again only
    // when something it depends on has changed.
    private void RenderPane(SplitPane pane, DocTab tab)
    {
        double dpmm = tab.Dpmm > 0 ? tab.Dpmm : _settings.DefaultDpmm;
        // The text by identity: a document out of focus only gets a new string when
        // it is left, so this costs nothing per keystroke in the focused one.
        var key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tab.Text)}:{tab.Text.Length}|{dpmm}|{tab.LabelIndex}|{_rotationDegrees}";
        if (pane.RenderedKey == key) return;
        pane.RenderedKey = key;
        try
        {
            var model = ZplRenderer.Parse(tab.Text, dpmm, tab.LabelIndex);
            ZplRenderer.Draw(pane.Canvas, model, dpmm, _rotationDegrees);
        }
        catch
        {
            pane.Canvas.Children.Clear();   // a document the renderer cannot read stays blank
        }
    }

    /// <summary>After every redraw of the live preview: the rotation or the preview's
    /// colour may have changed for the other panes too.</summary>
    private void RefreshSplitPanes()
    {
        if (!IsSplit) return;
        foreach (var (tab, pane) in _splitPanes)
        {
            if (ReferenceEquals(tab, _activeTab)) continue;
            pane.Body.Background = PreviewSurface.Background;
            RenderPane(pane, tab);
        }
    }

    // ── Dragging a tab onto the preview ──────────────────────────────────────
    // The places offered are the menu's: over the half or quarter a document can
    // still take, the tinted area shows it and a drop puts it there. In the
    // middle of a pane — or anywhere over a pane that cannot be shared any more —
    // the document takes that pane instead.

    private sealed record SplitDrop(DocTab Tab, SplitOption? Option, DocTab? Replace, Rect Hint);

    private SplitDrop? DropTarget(DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragState.Key)) return null;
        if (!ReferenceEquals(TabDragState.SourcePage, this) || TabDragState.Tab is not { } tab) return null;
        if (_activeTab is null || DocTabs.TabItems.Count < 2) return null;

        var p = e.GetPosition(SplitHost);
        double w = SplitHost.ActualWidth, h = SplitHost.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        // The pane under the pointer, as it is on screen now.
        DocTab? hovered = null;
        var hoveredRect = Rect.Empty;
        var panes = !IsSplit
            ? new List<(DocTab Tab, FrameworkElement Element)> { (_activeTab, PreviewSurface) }
            : _splitGroups.SelectMany(g => g)
                .Select(t => (t, ReferenceEquals(t, _activeTab) ? (FrameworkElement)PreviewSurface : PaneFor(t).Body))
                .ToList();
        foreach (var (paneTab, element) in panes)
        {
            var origin = element.TransformToVisual(SplitHost).TransformPoint(new Point(0, 0));
            var r = new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
            if (r.Width > 0 && r.Height > 0 && r.Contains(p)) { hovered = paneTab; hoveredRect = r; break; }
        }
        if (hovered is null) return null;

        // The middle third of a pane: take it.
        double fx = (p.X - hoveredRect.X) / hoveredRect.Width, fy = (p.Y - hoveredRect.Y) / hoveredRect.Height;
        bool middle = fx > 0.33 && fx < 0.67 && fy > 0.33 && fy < 0.67;
        if (!middle)
        {
            // The free half or quarter under the pointer; where two overlap (the
            // corner of a lone pane is in its left half AND its top half), the one
            // whose centre is nearer.
            var best = SplitOptions(tab)
                .Select(o => (Option: o, Area: new Rect(o.Cell.X * w, o.Cell.Y * h, o.Cell.Width * w, o.Cell.Height * h)))
                .Where(x => x.Area.Contains(p))
                .OrderBy(x => Math.Pow(x.Area.X + x.Area.Width / 2 - p.X, 2) / (x.Area.Width * x.Area.Width)
                            + Math.Pow(x.Area.Y + x.Area.Height / 2 - p.Y, 2) / (x.Area.Height * x.Area.Height))
                .FirstOrDefault();
            if (best.Option is not null) return new SplitDrop(tab, best.Option, null, best.Area);
        }
        if (ReferenceEquals(hovered, tab) || !IsSplit) return null;
        return new SplitDrop(tab, null, hovered, hoveredRect);
    }

    private void SplitHost_DragOver(object sender, DragEventArgs e)
    {
        if (DropTarget(e) is not { } target) { HideSplitDropHint(); return; }
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.Caption = target.Option is null ? SpL("dropHere") : SpL("dropSplit");
        e.Handled = true;
        ShowSplitDropHint(target.Hint);
    }

    private void SplitHost_Drop(object sender, DragEventArgs e)
    {
        HideSplitDropHint();
        if (DropTarget(e) is not { } target) return;
        e.Handled = true;
        TabDragState.Clear();
        if (target.Option is { } option) ApplySplitOption(option, target.Tab);
        else if (target.Replace is { } anchor && PlanReplace(target.Tab, anchor) is { } plan)
            ApplySplit(_splitRows, plan, target.Tab);
    }

    private void ShowSplitDropHint(Rect r)
    {
        if (_splitDropHint is null)
        {
            var accent = AccentColorService.Current;
            _splitDropHint = new Border
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(accent),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(48, accent.R, accent.G, accent.B)),
            };
            Canvas.SetZIndex(_splitDropHint, 100);
        }
        if (!SplitHost.Children.Contains(_splitDropHint)) SplitHost.Children.Add(_splitDropHint);
        Grid.SetRow(_splitDropHint, 0);
        Grid.SetColumn(_splitDropHint, 0);
        Grid.SetRowSpan(_splitDropHint, Math.Max(1, SplitHost.RowDefinitions.Count));
        Grid.SetColumnSpan(_splitDropHint, Math.Max(1, SplitHost.ColumnDefinitions.Count));
        _splitDropHint.Margin = new Thickness(r.X + 4, r.Y + 4, 0, 0);
        _splitDropHint.Width = Math.Max(0, r.Width - 8);
        _splitDropHint.Height = Math.Max(0, r.Height - 8);
        _splitDropHint.Visibility = Visibility.Visible;
    }

    private void HideSplitDropHint()
    {
        if (_splitDropHint is not null) SplitHost.Children.Remove(_splitDropHint);
    }

    // ── Tab menu entries ─────────────────────────────────────────────────────
    // Only the places that exist right now, named after where the document will be.

    private static readonly Dictionary<string, string> SplitGlyphs = new()
    {
        ["left"] = "", ["right"] = "", ["top"] = "", ["bottom"] = "",
        ["topLeft"] = "", ["topRight"] = "", ["bottomLeft"] = "", ["bottomRight"] = "",
    };

    private void AddSplitMenuItems(MenuFlyout menu, DocTab tab)
    {
        var options = SplitOptions(tab);
        if (options.Count == 0 && !IsSplit) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var option in options)
        {
            var mi = new MenuFlyoutItem
            {
                Text = SpL(option.Key),
                Icon = new FontIcon { Glyph = SplitGlyphs.GetValueOrDefault(option.Key, "") },
            };
            mi.Click += (_, _) => ApplySplitOption(option, tab);
            menu.Items.Add(mi);
        }
        if (IsSplit)
        {
            var un = new MenuFlyoutItem { Text = SpL("unsplit"), Icon = new FontIcon { Glyph = "" } };
            un.Click += (_, _) => Unsplit();
            menu.Items.Add(un);
        }
    }
}
