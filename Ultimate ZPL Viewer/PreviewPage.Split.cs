using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// ── Splitting the preview ───────────────────────────────────────────────────
// Several documents side by side, one above the other, or both: up to four
// previews at once, never more — a third column or row would leave each label
// too small to read. The code editor stays one: it shows the document of the
// pane that has the focus, and so does the toolbar. The focused pane is the
// live preview (zoom, rulers, edit mode, everything); the others are drawn from
// their document as it stands, and a click on one makes it the focused one.
//
// The panes are columns, left to right, each holding one or two documents top
// to bottom. A document is in one pane at most. Choosing a tab that is not on
// screen puts it in the focused pane, as an editor group would.
public sealed partial class PreviewPage
{
    private enum SplitSide { Right, Down, Left, Up, Center }

    private const int MaxSplitPanes = 4;

    // Empty while the preview is not split.
    private List<List<DocTab>> _splitCols = new();
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

    private static string SpL(string key) => LocalizationService.Get("split." + key);

    private int SplitCount => _splitCols.Sum(c => c.Count);
    private bool IsSplit => SplitCount > 1;

    private void InitSplit()
    {
        SplitHost.AllowDrop = true;
        SplitHost.DragOver += SplitHost_DragOver;
        SplitHost.DragLeave += (_, _) => HideSplitDropHint();
        SplitHost.Drop += SplitHost_Drop;
    }

    private static (int Col, int Row)? FindSlot(List<List<DocTab>> cols, DocTab tab)
    {
        for (int c = 0; c < cols.Count; c++)
            for (int r = 0; r < cols[c].Count; r++)
                if (ReferenceEquals(cols[c][r], tab)) return (c, r);
        return null;
    }

    private IEnumerable<DocTab> OpenTabs()
        => DocTabs.TabItems.OfType<TabViewItem>().Select(i => i.Tag).OfType<DocTab>();

    private TabViewItem? ItemOf(DocTab tab)
        => DocTabs.TabItems.OfType<TabViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, tab));

    // ── Planning a split ─────────────────────────────────────────────────────

    /// <summary>
    /// The arrangement after putting <paramref name="tab"/> on the given side of the
    /// pane showing <paramref name="anchor"/> (the focused one by default), or null
    /// when it cannot be done: no room left, or nothing else to show.
    /// </summary>
    private List<List<DocTab>>? PlanSplit(DocTab tab, SplitSide side, DocTab? anchor = null)
    {
        if (_activeTab is null) return null;
        anchor ??= _activeTab;
        var cols = _splitCols.Count > 0
            ? _splitCols.Select(c => c.ToList()).ToList()
            : new List<List<DocTab>> { new() { _activeTab } };
        if (FindSlot(cols, anchor) is null) return null;

        if (side == SplitSide.Center)
        {
            if (ReferenceEquals(tab, anchor)) return null;
            var a = FindSlot(cols, anchor)!.Value;
            if (FindSlot(cols, tab) is { } t) cols[t.Col][t.Row] = anchor;   // the two swap
            cols[a.Col][a.Row] = tab;
            return cols;
        }

        // The document leaves the pane it is in. Out of its OWN pane, that pane
        // keeps a document not on screen yet — there has to be one.
        if (FindSlot(cols, tab) is { } from)
        {
            if (ReferenceEquals(tab, anchor))
            {
                var other = OpenTabs().FirstOrDefault(t => FindSlot(cols, t) is null);
                if (other is null) return null;
                cols[from.Col][from.Row] = other;
                anchor = other;
            }
            else
            {
                cols[from.Col].RemoveAt(from.Row);
                if (cols[from.Col].Count == 0) cols.RemoveAt(from.Col);
            }
        }
        if (cols.Sum(c => c.Count) >= MaxSplitPanes) return null;

        var (ac, ar) = FindSlot(cols, anchor)!.Value;
        switch (side)
        {
            case SplitSide.Down:
            case SplitSide.Up:
                if (cols[ac].Count >= 2) return null;
                cols[ac].Insert(side == SplitSide.Down ? ar + 1 : ar, tab);
                return cols;

            case SplitSide.Right:
            case SplitSide.Left:
            {
                bool right = side == SplitSide.Right;
                if (cols.Count < 2)
                {
                    cols.Insert(right ? ac + 1 : ac, new List<DocTab> { tab });
                    return cols;
                }
                // Two columns already: the neighbour on that side takes it as a
                // second row, level with the anchor.
                int target = right ? ac + 1 : ac - 1;
                if (target < 0 || target >= cols.Count || cols[target].Count >= 2) return null;
                cols[target].Insert(Math.Min(ar, cols[target].Count), tab);
                return cols;
            }
        }
        return null;
    }

    private void ApplySplit(List<List<DocTab>> plan, DocTab focus)
    {
        _splitCols = plan;
        if (ReferenceEquals(focus, _activeTab)) SyncSplit();
        else if (ItemOf(focus) is { } item) DocTabs.SelectedItem = item;   // → ActivateTab → SyncSplit
    }

    private void SplitTab(DocTab tab, SplitSide side, DocTab? anchor = null)
    {
        if (PlanSplit(tab, side, anchor) is { } plan) ApplySplit(plan, tab);
    }

    private void Unsplit()
    {
        _splitCols.Clear();
        LayoutSplit();
    }

    /// <summary>Takes a document out of the split; it stays open in its tab.</summary>
    private void ClosePane(DocTab tab)
    {
        if (FindSlot(_splitCols, tab) is not { } slot) return;
        _splitCols[slot.Col].RemoveAt(slot.Row);
        if (_splitCols[slot.Col].Count == 0) _splitCols.RemoveAt(slot.Col);
        if (SplitCount <= 1)
        {
            var left = _splitCols.SelectMany(c => c).FirstOrDefault();
            _splitCols.Clear();
            if (left is not null && !ReferenceEquals(left, _activeTab) && ItemOf(left) is { } keep)
            {
                DocTabs.SelectedItem = keep;
                return;
            }
            LayoutSplit();
            return;
        }
        // The focused pane went: the focus moves to the pane that took its place.
        if (ReferenceEquals(tab, _activeTab))
        {
            int c = Math.Min(slot.Col, _splitCols.Count - 1);
            int r = Math.Min(slot.Row, _splitCols[c].Count - 1);
            _splitFocus = _splitCols[c][r];
            if (ItemOf(_splitCols[c][r]) is { } next) { DocTabs.SelectedItem = next; return; }
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
        if (_splitCols.Count == 0)
        {
            _splitFocus = _activeTab;
            if (_splitPanes.Count > 0) LayoutSplit();
            return;
        }

        var open = OpenTabs().ToHashSet();
        bool activeShown = _activeTab is not null && FindSlot(_splitCols, _activeTab) is not null;
        for (int c = 0; c < _splitCols.Count; c++)
            for (int r = 0; r < _splitCols[c].Count; r++)
            {
                if (open.Contains(_splitCols[c][r])) continue;
                if (!activeShown && _activeTab is not null)
                {
                    _splitCols[c][r] = _activeTab;   // the one that replaced it takes its place
                    activeShown = true;
                }
                else _splitCols[c].RemoveAt(r--);
            }
        _splitCols.RemoveAll(c => c.Count == 0);

        if (_activeTab is not null && !activeShown && _splitCols.Count > 0)
        {
            var slot = _splitFocus is not null ? FindSlot(_splitCols, _splitFocus) : null;
            var (c, r) = slot ?? (0, 0);
            _splitCols[c][r] = _activeTab;
        }
        if (SplitCount <= 1) _splitCols.Clear();
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
            Grid.SetColumn(PreviewSurface, 0);
            Grid.SetRow(PreviewSurface, 0);
            Grid.SetRowSpan(PreviewSurface, 1);
            return;
        }

        // One-pixel gaps through which the host's own colour draws the dividers.
        SplitHost.ColumnSpacing = 1;
        SplitHost.RowSpacing = 0;
        int rows = _splitCols.Max(c => c.Count);
        foreach (var _ in _splitCols)
            SplitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++)
        {
            SplitHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SplitHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        var shown = new HashSet<DocTab>();
        for (int c = 0; c < _splitCols.Count; c++)
        {
            var col = _splitCols[c];
            for (int r = 0; r < col.Count; r++)
            {
                var tab = col[r];
                shown.Add(tab);
                var pane = PaneFor(tab);
                bool focused = ReferenceEquals(tab, _activeTab);
                // A lone pane in its column runs the full height.
                int span = col.Count == 1 ? rows * 2 - 1 : 1;

                pane.Title.Text = TabTitle(tab);
                pane.Title.Opacity = focused ? 1 : 0.7;
                pane.Underline.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
                // A divider between the two rows of a column.
                pane.Header.BorderThickness = new Thickness(0, r > 0 ? 1 : 0, 0, 1);
                PlaceInSplit(pane.Header, c, r * 2, 1);

                if (focused)
                {
                    PlaceInSplit(PreviewSurface, c, r * 2 + 1, span);
                }
                else
                {
                    pane.Body.Background = PreviewSurface.Background;
                    PlaceInSplit(pane.Body, c, r * 2 + 1, span);
                    RenderPane(pane, tab);
                }
            }
        }
        foreach (var gone in _splitPanes.Keys.Where(t => !shown.Contains(t)).ToList())
            _splitPanes.Remove(gone);
    }

    private void PlaceInSplit(FrameworkElement element, int col, int row, int rowSpan)
    {
        Grid.SetColumn(element, col);
        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
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
    // Dropped near an edge of a pane, the document opens on that side of it;
    // dropped in the middle, it takes that pane. A tinted area shows where.

    private (DocTab Tab, SplitSide Side, DocTab Anchor, Windows.Foundation.Rect Hint)? DropTarget(DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragState.Key)) return null;
        if (!ReferenceEquals(TabDragState.SourcePage, this) || TabDragState.Tab is not { } tab) return null;
        if (_activeTab is null || DocTabs.TabItems.Count < 2) return null;

        var p = e.GetPosition(SplitHost);
        // Every pane on screen, with the document it shows.
        var panes = new List<(DocTab Tab, FrameworkElement Element)>();
        if (!IsSplit) panes.Add((_activeTab, PreviewSurface));
        else
            foreach (var t in _splitCols.SelectMany(c => c))
                panes.Add((t, ReferenceEquals(t, _activeTab) ? PreviewSurface : PaneFor(t).Body));

        foreach (var (anchor, element) in panes)
        {
            var origin = element.TransformToVisual(SplitHost).TransformPoint(new Windows.Foundation.Point(0, 0));
            var r = new Windows.Foundation.Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
            if (!r.Contains(p) || r.Width <= 0 || r.Height <= 0) continue;

            double fx = (p.X - r.X) / r.Width, fy = (p.Y - r.Y) / r.Height;
            // The nearest edge, if the pointer is in its outer third.
            var edges = new (SplitSide Side, double Distance)[]
            {
                (SplitSide.Left, fx), (SplitSide.Right, 1 - fx), (SplitSide.Up, fy), (SplitSide.Down, 1 - fy),
            };
            var nearest = edges.OrderBy(x => x.Distance).First();
            var side = nearest.Distance < 0.33 ? nearest.Side : SplitSide.Center;
            if (PlanSplit(tab, side, anchor) is null) return null;

            var hint = side switch
            {
                SplitSide.Left  => new Windows.Foundation.Rect(r.X, r.Y, r.Width / 2, r.Height),
                SplitSide.Right => new Windows.Foundation.Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height),
                SplitSide.Up    => new Windows.Foundation.Rect(r.X, r.Y, r.Width, r.Height / 2),
                SplitSide.Down  => new Windows.Foundation.Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2),
                _               => r,
            };
            return (tab, side, anchor, hint);
        }
        return null;
    }

    private void SplitHost_DragOver(object sender, DragEventArgs e)
    {
        if (DropTarget(e) is not { } target) { HideSplitDropHint(); return; }
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.Caption = target.Side == SplitSide.Center ? SpL("dropHere") : SpL("dropSplit");
        e.Handled = true;
        ShowSplitDropHint(target.Hint);
    }

    private void SplitHost_Drop(object sender, DragEventArgs e)
    {
        HideSplitDropHint();
        if (DropTarget(e) is not { } target) return;
        e.Handled = true;
        TabDragState.Clear();
        if (PlanSplit(target.Tab, target.Side, target.Anchor) is { } plan) ApplySplit(plan, target.Tab);
    }

    private void ShowSplitDropHint(Windows.Foundation.Rect r)
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

    private void AddSplitMenuItems(MenuFlyout menu, DocTab tab)
    {
        MenuFlyoutItem Mk(string text, string glyph, Action action, bool enabled)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Mk(SpL("right"), "", () => SplitTab(tab, SplitSide.Right),
            PlanSplit(tab, SplitSide.Right) is not null));
        menu.Items.Add(Mk(SpL("down"), "", () => SplitTab(tab, SplitSide.Down),
            PlanSplit(tab, SplitSide.Down) is not null));
        if (IsSplit)
            menu.Items.Add(Mk(SpL("unsplit"), "", Unsplit, true));
    }
}
