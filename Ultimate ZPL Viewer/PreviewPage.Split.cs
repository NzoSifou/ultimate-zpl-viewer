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
    // The places offered are the menu's, and only those: while a tab is dragged
    // over the preview, each one shows as a pad bearing the menu's picture,
    // standing in the middle of the half or quarter it would fill; the one under
    // the pointer is tinted, and that is where a drop puts the document. Outside
    // every place (the panes that cannot be shared any more), nothing is dropped.

    private Canvas? _splitDropLayer;
    private string? _splitDropShown;

    private (List<SplitOption> Options, SplitOption? Active)? DropTarget(DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragState.Key)) return null;
        if (!ReferenceEquals(TabDragState.SourcePage, this) || TabDragState.Tab is not { } tab) return null;
        if (_activeTab is null || DocTabs.TabItems.Count < 2) return null;
        double w = SplitHost.ActualWidth, h = SplitHost.ActualHeight;
        if (w <= 0 || h <= 0) return null;

        var options = SplitOptions(tab);
        if (options.Count == 0) return null;
        var p = e.GetPosition(SplitHost);
        double fx = p.X / w, fy = p.Y / h;
        // The place whose area holds the pointer; where two overlap (with a single
        // pane, its left half and its top half share a corner), the one whose
        // middle is nearer — which cuts the pane along its diagonals.
        var active = options
            .Where(o => o.Cell.Contains(new Point(fx, fy)))
            .OrderBy(o => Math.Pow((o.Cell.X + o.Cell.Width / 2 - fx) / o.Cell.Width, 2)
                        + Math.Pow((o.Cell.Y + o.Cell.Height / 2 - fy) / o.Cell.Height, 2))
            .FirstOrDefault();
        return (options, active);
    }

    private void SplitHost_DragOver(object sender, DragEventArgs e)
    {
        if (DropTarget(e) is not { } target) { HideSplitDropHint(); return; }
        e.Handled = true;
        ShowSplitDropZones(target.Options, target.Active);
        if (target.Active is not { } active) return;   // not over a place: no drop
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.Caption = SpL(active.Key);
    }

    private void SplitHost_Drop(object sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        HideSplitDropHint();
        if (target is not { Active: { } option } || TabDragState.Tab is not { } tab) return;
        e.Handled = true;
        TabDragState.Clear();
        ApplySplitOption(option, tab);
    }

    private void ShowSplitDropZones(List<SplitOption> options, SplitOption? active)
    {
        double w = SplitHost.ActualWidth, h = SplitHost.ActualHeight;
        var signature = $"{w:0}x{h:0}|{active?.Key}|{string.Join(",", options.Select(o => o.Key))}";
        if (_splitDropLayer is null)
        {
            _splitDropLayer = new Canvas { IsHitTestVisible = false };
            Canvas.SetZIndex(_splitDropLayer, 100);
        }
        if (!SplitHost.Children.Contains(_splitDropLayer))
        {
            SplitHost.Children.Add(_splitDropLayer);
            _splitDropShown = null;
        }
        Grid.SetRow(_splitDropLayer, 0);
        Grid.SetColumn(_splitDropLayer, 0);
        Grid.SetRowSpan(_splitDropLayer, Math.Max(1, SplitHost.RowDefinitions.Count));
        Grid.SetColumnSpan(_splitDropLayer, Math.Max(1, SplitHost.ColumnDefinitions.Count));
        if (signature == _splitDropShown) return;
        _splitDropShown = signature;

        var layer = _splitDropLayer;
        layer.Children.Clear();
        var accent = AccentColorService.Current;

        // The area the document would fill, tinted.
        if (active is not null)
        {
            var fill = new Border
            {
                Width = Math.Max(0, active.Cell.Width * w - 8),
                Height = Math.Max(0, active.Cell.Height * h - 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(accent),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(48, accent.R, accent.G, accent.B)),
            };
            Canvas.SetLeft(fill, active.Cell.X * w + 4);
            Canvas.SetTop(fill, active.Cell.Y * h + 4);
            layer.Children.Add(fill);
        }

        // One pad per place, in the middle of the area it stands for.
        const double padW = 56, padH = 46;
        foreach (var option in options)
        {
            bool on = ReferenceEquals(option, active);
            var pad = new Border
            {
                Width = padW, Height = padH,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(on ? 2 : 1),
                BorderBrush = on ? new SolidColorBrush(accent)
                                 : (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
                Child = new Viewbox
                {
                    Width = 30, Height = 30,
                    Child = SplitIcon(option.Cell, on ? accent : null),
                },
            };
            Canvas.SetLeft(pad, (option.Cell.X + option.Cell.Width / 2) * w - padW / 2);
            Canvas.SetTop(pad, (option.Cell.Y + option.Cell.Height / 2) * h - padH / 2);
            layer.Children.Add(pad);
        }
    }

    private void HideSplitDropHint()
    {
        if (_splitDropLayer is not null) SplitHost.Children.Remove(_splitDropLayer);
        _splitDropShown = null;
    }

    // ── Pictures of the places ───────────────────────────────────────────────
    // A window frame with the part the document would take filled in: the left
    // half, the bottom right quarter… The same picture in the tab menu and on the
    // pads shown while dragging. Without a place, the bare frame: one preview.

    private static PathIcon SplitIcon(Rect? cell, Windows.UI.Color? color = null)
    {
        static string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        // 16 × 16: a 1-unit frame from (1,2) to (15,14); inside it, 12 × 10.
        var data = "F1 M1,2 H15 V14 H1 Z M2,3 V13 H14 V3 Z";
        if (cell is { } c)
        {
            // The dividers the place implies: the middle line for a half, the cross
            // for a quarter — then the place itself, filled.
            if (c.Width < 1) data += " M7.6,3 H8.4 V13 H7.6 Z";
            if (c.Height < 1) data += " M2,7.6 H14 V8.4 H2 Z";
            double x = 2 + c.X * 12 + 1, y = 3 + c.Y * 10 + 1;
            double cw = c.Width * 12 - 2, ch = c.Height * 10 - 2;
            data += $" M{N(x)},{N(y)} H{N(x + cw)} V{N(y + ch)} H{N(x)} Z";
        }
        var icon = new PathIcon
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        };
        if (color is { } fg) icon.Foreground = new SolidColorBrush(fg);
        return icon;
    }

    // ── Tab menu entries ─────────────────────────────────────────────────────
    // Only the places that exist right now, named after where the document will be.

    private void AddSplitMenuItems(MenuFlyout menu, DocTab tab)
    {
        var options = SplitOptions(tab);
        if (options.Count == 0 && !IsSplit) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var option in options)
        {
            var mi = new MenuFlyoutItem { Text = SpL(option.Key), Icon = SplitIcon(option.Cell) };
            mi.Click += (_, _) => ApplySplitOption(option, tab);
            menu.Items.Add(mi);
        }
        if (IsSplit)
        {
            var un = new MenuFlyoutItem { Text = SpL("unsplit"), Icon = SplitIcon(null) };
            un.Click += (_, _) => Unsplit();
            menu.Items.Add(un);
        }
    }
}