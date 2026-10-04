using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Ultimate_ZPL_Viewer;

// ── Split views ─────────────────────────────────────────────────────────────
// Several documents side by side, one above the other, or both: up to four
// previews at once, never more — a third column or row would leave each label
// too small to read. As soon as a second document is put beside the first, the
// two become a SPLIT VIEW: a tab of its own in the strip, « Vue fractionnée »,
// that stands for the whole arrangement. Its documents' tabs follow it, framed
// together, while it is on screen; choosing any other tab shows that document
// alone and folds them away behind the split view's tab, and a click on that
// tab brings the arrangement back. A window can hold several split views; a
// document belongs to one at most.
//
// Inside the split view on screen, the code editor and the toolbar work on ONE
// pane, the focused one — the live preview (zoom, rulers, edit mode,
// everything); the others are drawn from their document as it stands. Clicking
// a pane, or its tab, gives it the focus.
//
// An arrangement is a list of groups — columns, or rows — of one or two panes
// each: two columns of which one is cut in two, two rows of which one is cut in
// two, a plain 2 × 2. That is every layout of up to four panes on a 2 × 2 grid,
// and none that would need a third column or row. Where a document can go is
// therefore one of the grid's halves or quarters still free, and both the tab
// menu and a dragged tab offer exactly those.
public sealed partial class PreviewPage
{
    private const int MaxSplitPanes = 4;

    /// <summary>One split view: its arrangement and the tab that stands for it.</summary>
    private sealed class SplitView
    {
        public int Number { get; init; }
        // False: the groups are columns (left to right), their panes stacked top to
        // bottom. True: the groups are rows (top to bottom), their panes side by side.
        public bool Rows { get; set; }
        public List<List<DocTab>> Groups { get; set; } = new();
        // The pane that had the focus when the view was left: it gets it back.
        public DocTab? Focus { get; set; }
        public TabViewItem Pill { get; set; } = null!;
        public IEnumerable<DocTab> Members => Groups.SelectMany(g => g);
        public int Count => Groups.Sum(g => g.Count);
    }

    private readonly List<SplitView> _views = new();
    // The split view on screen — the one holding the active document — or null.
    private SplitView? _view;
    private readonly Dictionary<DocTab, SplitPane> _splitPanes = new();
    private bool _normalizingStrip;

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

    private bool IsSplit => _view is not null;
    private bool _splitRows => _view?.Rows ?? false;
    private List<List<DocTab>> _splitGroups => _view?.Groups ?? new List<List<DocTab>>();

    private void InitSplit()
    {
        SplitHost.AllowDrop = true;
        SplitHost.DragOver += SplitHost_DragOver;
        SplitHost.DragLeave += (_, _) => HideSplitDropHint();
        SplitHost.Drop += SplitHost_Drop;
        // A tab moved by hand inside the strip: the split views' tabs follow their
        // own tab again, in the order of their panes.
        DocTabs.TabItemsChanged += (_, _) =>
        {
            if (!_normalizingStrip) DispatcherQueue.TryEnqueue(NormalizeStrip);
        };
        DocTabs.LayoutUpdated += (_, _) => UpdateTabGroupFrames();
    }

    // ── The strip: documents and split views ─────────────────────────────────

    /// <summary>The tabs that are documents (not split views), in strip order.</summary>
    private IEnumerable<TabViewItem> DocItems()
        => DocTabs.TabItems.OfType<TabViewItem>().Where(i => i.Tag is DocTab);

    private int DocCount => DocItems().Count();

    private IEnumerable<DocTab> OpenTabs() => DocItems().Select(i => (DocTab)i.Tag);

    private TabViewItem? ItemOf(DocTab? tab)
        => tab is null ? null : DocItems().FirstOrDefault(i => ReferenceEquals(i.Tag, tab));

    private SplitView? ViewOf(DocTab? tab)
        => tab is null ? null : _views.FirstOrDefault(v => v.Members.Contains(tab));

    /// <summary>
    /// The document a strip item leads to: itself, or for a split view the pane
    /// that had the focus there.
    /// </summary>
    private DocTab? DocOfItem(TabViewItem? item) => item?.Tag switch
    {
        DocTab tab => tab,
        SplitView view => view.Focus is { } f && view.Members.Contains(f) ? f : view.Members.FirstOrDefault(),
        _ => null,
    };

    /// <summary>
    /// The stops of Ctrl+Tab and Ctrl+1…9: every tab that can be seen — a split
    /// view folded away is one stop, its own tab; unfolded, its documents are.
    /// </summary>
    private List<TabViewItem> TabStops()
        => DocTabs.TabItems.OfType<TabViewItem>()
            .Where(i => i.Visibility == Visibility.Visible && !(i.Tag is SplitView v && ReferenceEquals(v, _view)))
            .ToList();

    private int CurrentStopIndex(List<TabViewItem> stops)
    {
        var current = ItemOf(_activeTab);
        int i = current is null ? -1 : stops.IndexOf(current);
        if (i < 0 && _view is not null) i = stops.IndexOf(_view.Pill);
        return i;
    }

    private void SelectStop(TabViewItem item)
    {
        if (DocOfItem(item) is { } doc && ItemOf(doc) is { } docItem) DocTabs.SelectedItem = docItem;
    }

    // ── Where a document can go ──────────────────────────────────────────────

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

    /// <summary>Shown in a pane — or, with no split view on screen, the one document shown.</summary>
    private bool IsOnScreen(DocTab tab)
        => _view is not null ? FindSlot(_view.Groups, tab) is not null : ReferenceEquals(tab, _activeTab);

    /// <summary>
    /// The places <paramref name="tab"/> can be shown, counted on what is on screen
    /// now. One pane: left or right of it, above or below. Two: any of the four
    /// quarters, the pane on that side giving up half its room. Three: the two
    /// quarters of the pane that still has a half to itself. Four: none. A
    /// document already on screen has no place to go — it is shown already.
    /// </summary>
    private List<SplitOption> SplitOptions(DocTab tab)
    {
        var options = new List<SplitOption>();
        if (_activeTab is null || IsOnScreen(tab)) return options;

        var (rows, layout) = _view is not null
            ? Canonical(_view.Rows, CopyGroups(_view.Groups))
            : (false, new List<List<DocTab>> { new() { _activeTab } });
        int total = layout.Sum(g => g.Count);
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

    /// <summary>
    /// Puts <paramref name="tab"/> at a place: into the split view on screen, or —
    /// a single document being shown — into a new split view made of the two.
    /// </summary>
    private void ApplySplitOption(SplitOption option, DocTab tab)
    {
        if (_activeTab is null) return;
        var target = _view;
        // A document from a split view folded away leaves it for this one.
        if (ViewOf(tab) is { } from && !ReferenceEquals(from, target)) RemoveMember(from, tab);

        if (target is null)
        {
            target = new SplitView { Number = NextViewNumber() };
            target.Pill = MakeSplitPill(target);
            var anchorItem = ItemOf(_activeTab);
            int at = anchorItem is null ? DocTabs.TabItems.Count : DocTabs.TabItems.IndexOf(anchorItem);
            _normalizingStrip = true;
            try { DocTabs.TabItems.Insert(Math.Max(0, at), target.Pill); }
            finally { _normalizingStrip = false; }
            _views.Add(target);
        }
        (target.Rows, target.Groups) = Canonical(option.Rows, option.Groups);
        target.Focus = tab;
        NormalizeStrip();
        WindowManager.SaveSessionLayout();
        if (ItemOf(tab) is { } item) DocTabs.SelectedItem = item;   // → ActivateTab → SyncSplit
        else SyncSplit();
    }

    private int NextViewNumber()
    {
        int n = 1;
        while (_views.Any(v => v.Number == n)) n++;
        return n;
    }

    /// <summary>Takes a document out of a split view; a view left with one document is undone.</summary>
    private void RemoveMember(SplitView view, DocTab tab)
    {
        if (FindSlot(view.Groups, tab) is not { } slot) return;
        view.Groups[slot.Group].RemoveAt(slot.Index);
        (view.Rows, view.Groups) = Canonical(view.Rows, view.Groups);
        if (ReferenceEquals(view.Focus, tab)) view.Focus = view.Members.FirstOrDefault();
        if (view.Count <= 1) DissolveView(view);
    }

    /// <summary>Undoes a split view: its documents become plain tabs again, where they are.</summary>
    private void DissolveView(SplitView view)
    {
        _views.Remove(view);
        if (ReferenceEquals(_view, view)) _view = null;
        _normalizingStrip = true;
        try
        {
            var selected = DocTabs.SelectedItem;
            DocTabs.TabItems.Remove(view.Pill);
            if (selected is not null && !ReferenceEquals(selected, view.Pill) && DocTabs.TabItems.Contains(selected))
                DocTabs.SelectedItem = selected;
        }
        finally { _normalizingStrip = false; }
        foreach (var member in view.Members)
            if (ItemOf(member) is { } item) item.Visibility = Visibility.Visible;
    }

    /// <summary>The menu's « undo the split view » on the view on screen.</summary>
    private void Unsplit()
    {
        if (_view is null) return;
        DissolveView(_view);
        WindowManager.SaveSessionLayout();
        SyncSplit();
    }

    /// <summary>Takes a document out of the split view on screen; it stays open in its tab.</summary>
    private void ClosePane(DocTab tab)
    {
        if (ViewOf(tab) is not { } view) return;
        bool wasActive = ReferenceEquals(tab, _activeTab);
        var others = view.Members.Where(m => !ReferenceEquals(m, tab)).ToList();
        RemoveMember(view, tab);
        bool kept = _views.Contains(view);
        // Out of the view, its tab moves out of the view's block too.
        if (kept && ItemOf(tab) is { } item)
            MoveItem(item, () =>
            {
                var last = view.Members.Select(ItemOf).OfType<TabViewItem>()
                    .Select(i => DocTabs.TabItems.IndexOf(i)).DefaultIfEmpty(DocTabs.TabItems.Count - 1).Max();
                return last + 1;
            });
        WindowManager.SaveSessionLayout();
        // It had the focus: the view keeps showing, on another of its documents —
        // or, undone, the document left from it shows.
        if (wasActive)
        {
            var next = kept ? view.Focus : others.FirstOrDefault();
            if (ItemOf(next) is { } nextItem) { DocTabs.SelectedItem = nextItem; return; }
        }
        SyncSplit();
    }

    private void FocusPane(DocTab tab)
    {
        if (ReferenceEquals(tab, _activeTab)) return;
        if (ItemOf(tab) is { } item) DocTabs.SelectedItem = item;
    }

    /// <summary>A split view's own tab was chosen: its arrangement comes back.</summary>
    private void OpenSplitView(SplitView view)
    {
        if (DocOfItem(view.Pill) is { } doc && ItemOf(doc) is { } item) DocTabs.SelectedItem = item;
    }

    // ── Keeping everything in step with the tabs ─────────────────────────────

    /// <summary>
    /// Called whenever the active document or the set of tabs changes: closed
    /// documents leave their split views (a view left with one is undone), the
    /// view of the active document is the one shown and unfolded in the strip,
    /// and every other folds away.
    /// </summary>
    private void SyncSplit()
    {
        var open = OpenTabs().ToHashSet();
        foreach (var view in _views.ToList())
        {
            foreach (var gone in view.Members.Where(m => !open.Contains(m)).ToList())
                RemoveMember(view, gone);
        }

        _view = ViewOf(_activeTab);
        if (_view is not null) _view.Focus = _activeTab;

        foreach (var view in _views)
        {
            bool unfolded = ReferenceEquals(view, _view);
            foreach (var member in view.Members)
                if (ItemOf(member) is { } item)
                    item.Visibility = unfolded ? Visibility.Visible : Visibility.Collapsed;
            UpdateSplitPill(view, unfolded);
        }
        LayoutSplit();
        UpdateTabGroupFrames();
    }

    // Moves a strip item without the move counting as a choice of tab. The place
    // is worked out once the item is out of the strip.
    private void MoveItem(TabViewItem item, Func<int> indexAfterRemoval)
    {
        int current = DocTabs.TabItems.IndexOf(item);
        if (current < 0) return;
        _normalizingStrip = true;
        bool suppress = _suppressTabEvents;
        _suppressTabEvents = true;
        try
        {
            var selected = DocTabs.SelectedItem;
            DocTabs.TabItems.RemoveAt(current);
            int index = Math.Clamp(indexAfterRemoval(), 0, DocTabs.TabItems.Count);
            DocTabs.TabItems.Insert(index, item);
            if (selected is not null) DocTabs.SelectedItem = selected;
        }
        finally
        {
            _suppressTabEvents = suppress;
            _normalizingStrip = false;
        }
    }

    /// <summary>
    /// Each split view's documents right after its own tab, in the order their
    /// panes are read (top left, top right, bottom left, bottom right).
    /// </summary>
    private void NormalizeStrip()
    {
        foreach (var view in _views)
        {
            var ordered = view.Groups
                .SelectMany((g, gi) => g.Select((t, ii) => (Tab: t,
                    Row: view.Rows ? gi : (g.Count == 1 ? 0 : ii),
                    Col: view.Rows ? (g.Count == 1 ? 0 : ii) : gi)))
                .OrderBy(x => x.Row).ThenBy(x => x.Col)
                .Select(x => ItemOf(x.Tab)).OfType<TabViewItem>().ToList();
            for (int k = 0; k < ordered.Count; k++)
            {
                if (DocTabs.TabItems.IndexOf(ordered[k]) == DocTabs.TabItems.IndexOf(view.Pill) + 1 + k) continue;
                int slot = k;
                MoveItem(ordered[k], () => DocTabs.TabItems.IndexOf(view.Pill) + 1 + slot);
            }
        }
        UpdateTabGroupFrames();
    }

    // ── The split view's tab, and the frame around its documents ─────────────

    private TabViewItem MakeSplitPill(SplitView view)
    {
        var item = new TabViewItem
        {
            Tag = view,
            IsClosable = false,
            Style = (Style)Resources["FloatingTabViewItemStyle"],
        };
        // The floating tab template draws its cross whatever IsClosable says: a split
        // view is not closed, it is undone (its menu), so the cross goes.
        item.Loaded += (_, _) =>
        {
            if (FindDescendant<Button>(item, "CloseButton") is { } close) close.Visibility = Visibility.Collapsed;
        };
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var undo = new MenuFlyoutItem { Text = SpL("unsplit"), Icon = SplitIcon(null) };
            undo.Click += (_, _) =>
            {
                DissolveView(view);
                WindowManager.SaveSessionLayout();
                SyncSplit();
            };
            menu.Items.Add(undo);
        };
        item.ContextFlyout = menu;
        item.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _pressedTabItem = item), true);
        view.Pill = item;
        UpdateSplitPill(view, false);
        return item;
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (FindDescendant<T>(child, name) is { } deeper) return deeper;
        }
        return null;
    }

    private void UpdateSplitPill(SplitView view, bool unfolded)
    {
        var name = SpL("viewName") + (view.Number > 1 ? " " + view.Number : "");
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new Viewbox { Width = 16, Height = 16, Child = LayoutIcon(view) });
        header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        // Folded away: a chevron says there is more behind it.
        header.Children.Add(new FontIcon
        {
            Glyph = unfolded ? "" : "", FontSize = 10, Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        view.Pill.Header = header;
        var names = string.Join("\n", view.Members.Select(TabTitle));
        ToolTipService.SetToolTip(view.Pill, new TextBlock { Text = name + "\n" + names, MaxWidth = 260, TextWrapping = TextWrapping.Wrap });
        ToolTipService.SetPlacement(view.Pill, Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom);
    }

    private string? _framesShown;

    /// <summary>
    /// A rounded frame, in the accent colour, around each split view's tab and the
    /// documents it has unfolded: one look tells which tabs go together.
    /// </summary>
    private void UpdateTabGroupFrames()
    {
        if (TabGroupLayer is null) return;
        var rects = new List<Rect>();
        if (DocTabs.Visibility == Visibility.Visible)
            foreach (var view in _views)
            {
                var items = new[] { view.Pill }
                    .Concat(view.Members.Select(ItemOf).OfType<TabViewItem>())
                    .Where(i => i.Visibility == Visibility.Visible && i.ActualWidth > 0)
                    .ToList();
                if (items.Count == 0) continue;
                Rect? union = null;
                foreach (var i in items)
                {
                    Point o;
                    try { o = i.TransformToVisual(TabGroupLayer).TransformPoint(new Point(0, 0)); }
                    catch { continue; }
                    var r = new Rect(o.X, o.Y, i.ActualWidth, i.ActualHeight);
                    if (union is { } u) { u.Union(r); union = u; } else union = r;
                }
                if (union is { } done) rects.Add(done);
            }
        var signature = string.Join(";", rects.Select(r => $"{r.X:0},{r.Y:0},{r.Width:0},{r.Height:0}"));
        if (signature == _framesShown) return;
        _framesShown = signature;

        TabGroupLayer.Children.Clear();
        var accent = AccentColorService.Current;
        foreach (var r in rects)
        {
            var frame = new Border
            {
                Width = r.Width + 8, Height = r.Height + 8,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new SolidColorBrush(accent),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, accent.R, accent.G, accent.B)),
            };
            Canvas.SetLeft(frame, r.X - 4);
            Canvas.SetTop(frame, r.Y - 4);
            TabGroupLayer.Children.Add(frame);
        }
    }

    // A window frame showing the view's arrangement.
    private static PathIcon LayoutIcon(SplitView view)
    {
        var data = "F1 M1,2 H15 V14 H1 Z M2,3 V13 H14 V3 Z";
        if (view.Groups.Count == 2)
            data += view.Rows ? " M2,7.6 H14 V8.4 H2 Z" : " M7.6,3 H8.4 V13 H7.6 Z";
        for (int g = 0; g < view.Groups.Count; g++)
        {
            if (view.Groups[g].Count < 2) continue;
            data += view.Rows
                ? (g == 0 ? " M7.6,3 H8.4 V7.6 H7.6 Z" : " M7.6,8.4 H8.4 V13 H7.6 Z")
                : (g == 0 ? " M2,7.6 H7.6 V8.4 H2 Z" : " M8.4,7.6 H14 V8.4 H8.4 Z");
        }
        return new PathIcon
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        };
    }

    // ── Saving and restoring the split views ─────────────────────────────────

    /// <summary>This window's split views, for the session (saved documents only).</summary>
    internal List<SplitViewState> SplitViewStates()
        => _views
            .Where(v => v.Members.All(m => !string.IsNullOrWhiteSpace(FilePathOf(m))))
            .Select(v => new SplitViewState
            {
                Rows = v.Rows,
                Panes = v.Groups.Select(g => g.Select(m => FilePathOf(m)!).ToList()).ToList(),
                Focus = v.Focus is null ? null : FilePathOf(v.Focus),
            })
            .ToList();

    private string? FilePathOf(DocTab tab) => ReferenceEquals(tab, _activeTab) ? _currentFilePath : tab.FilePath;

    /// <summary>Rebuilds saved split views from the documents of this window.</summary>
    internal void RestoreSplitViews(List<SplitViewState>? states)
    {
        if (states is null || states.Count == 0) return;
        foreach (var state in states)
        {
            DocTab? Find(string path) => OpenTabs().FirstOrDefault(t =>
                string.Equals(FilePathOf(t), path, StringComparison.OrdinalIgnoreCase) && ViewOf(t) is null);
            var groups = state.Panes.Select(g => g.Select(Find).ToList()).ToList();
            if (groups.Any(g => g.Any(t => t is null))) continue;          // a file is gone
            var (rows, layout) = Canonical(state.Rows, groups.Select(g => g.Select(t => t!).ToList()).ToList());
            int count = layout.Sum(g => g.Count);
            if (count < 2 || count > MaxSplitPanes || layout.SelectMany(g => g).Distinct().Count() != count) continue;

            var view = new SplitView { Number = NextViewNumber(), Rows = rows, Groups = layout };
            view.Focus = state.Focus is null ? null : view.Members.FirstOrDefault(t =>
                string.Equals(FilePathOf(t), state.Focus, StringComparison.OrdinalIgnoreCase));
            view.Pill = MakeSplitPill(view);
            var first = ItemOf(view.Members.First());
            _normalizingStrip = true;
            try { DocTabs.TabItems.Insert(first is null ? 0 : DocTabs.TabItems.IndexOf(first), view.Pill); }
            finally { _normalizingStrip = false; }
            _views.Add(view);
        }
        NormalizeStrip();
        SyncSplit();
        WindowManager.SaveSessionLayout();
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

        if (_view is null)
        {
            _splitPanes.Clear();
            SplitHost.ColumnSpacing = SplitHost.RowSpacing = 0;
            PlaceInSplit(PreviewSurface, 0, 0, 1, 1);
            return;
        }

        // One-pixel gaps through which the host's own colour draws the dividers.
        SplitHost.ColumnSpacing = 1;
        SplitHost.RowSpacing = 0;
        var groups = _view.Groups;
        bool byRows = _view.Rows;
        int inner = groups.Max(g => g.Count);
        int columns = byRows ? inner : groups.Count;
        int rowSlots = byRows ? groups.Count : inner;
        for (int c = 0; c < columns; c++)
            SplitHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rowSlots; r++)
        {
            SplitHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            SplitHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        var shown = new HashSet<DocTab>();
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            for (int i = 0; i < group.Count; i++)
            {
                var tab = group[i];
                shown.Add(tab);
                var pane = PaneFor(tab);
                bool focused = ReferenceEquals(tab, _activeTab);
                int col = byRows ? i : g;
                int rowSlot = byRows ? g : i;
                // A pane alone in its group takes the group's whole length.
                bool lone = group.Count == 1;
                int colSpan = byRows && lone ? columns : 1;
                int bodyRowSpan = !byRows && lone ? rowSlots * 2 - 1 : 1;

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
        if (_activeTab is null || DocCount < 2) return null;
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

    /// <summary>True when a screen point (physical pixels, like the cursor) is over this window's preview.</summary>
    internal bool IsOverPreview(int screenX, int screenY)
    {
        if (XamlRoot is null || SplitHost.ActualWidth <= 0
            || AppWindowLookup.MainWindowForXamlRoot(XamlRoot) is not MainWindow window) return false;
        double scale = XamlRoot.RasterizationScale;
        var pos = window.AppWindow.Position;
        // AppWindow.Position is the outer frame; the content starts at the client
        // origin, which TransformToVisual(null) measures from.
        var client = window.AppWindow.ClientSize;
        var size = window.AppWindow.Size;
        double offX = (size.Width - client.Width) / 2.0;
        double offY = size.Height - client.Height - offX;
        double x = (screenX - pos.X - offX) / scale, y = (screenY - pos.Y - offY) / scale;
        var origin = SplitHost.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, SplitHost.ActualWidth, SplitHost.ActualHeight).Contains(new Point(x, y));
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
        bool inPane = IsSplit && FindSlot(_splitGroups, tab) is not null;
        if (options.Count == 0 && !IsSplit) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        // A document on screen already: it can only leave the split.
        if (inPane)
        {
            var leave = new MenuFlyoutItem { Text = SpL("closePane"), Icon = new FontIcon { Glyph = "\uE711" } };
            leave.Click += (_, _) => ClosePane(tab);
            menu.Items.Add(leave);
        }
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