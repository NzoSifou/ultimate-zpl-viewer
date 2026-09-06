using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Foundation;

namespace Ultimate_ZPL_Viewer;

// ── Putting new things on the label ─────────────────────────────────────────
// The left-hand plate arms a tool; the next press on the label places it. Shapes
// can be sized by dragging, and a plain click gives them a sensible default —
// which is what a drawing application does, and what stops the third element from
// being a chore.
//
// Everything here ends as ONE snippet of ZPL inserted just before ^XZ, through the
// same edit path a drag uses: Monaco's undo stack, then the ordinary redraw.
public sealed partial class PreviewPage
{
    // None = the arrow: pick an element, read it, change its properties — but a
    // drag does nothing. Move adds the dragging, the handles and the arrow keys.
    // The rest put something new on the label.
    private enum EditTool { None, Move, Text, Barcode, Rect, Line, Ellipse, Circle, Fill, Image }

    // Moving is what the tool started out doing, so that is what it opens on; the
    // arrow is the safety you reach for, not the state you are dropped into.
    private const EditTool DefaultTool = EditTool.Move;

    private EditTool _tool = DefaultTool;

    /// <summary>True while a tool that PLACES something is armed.</summary>
    private bool IsPlacementTool => _tool is not (EditTool.None or EditTool.Move);

    /// <summary>True when a drag on the label is allowed to change it.</summary>
    internal bool CanMoveElements => _tool == EditTool.Move;
    private string _barcodeKind = "code128";

    // The placement gesture, which is separate from dragging an existing element.
    private bool _placing;
    private Point _placeStart;
    private Rectangle? _placeBand;

    // Shapes and codes get their defaults in MILLIMETRES, so a label at 12 dpmm
    // starts with the same object as one at 6 — dots would halve it.
    private const double DefaultTextMm = 4;
    private const double DefaultCodeHeightMm = 12;
    private const double DefaultBoxWidthMm = 25;
    private const double DefaultBoxHeightMm = 12;

    private void InitEditTools()
    {
        ToolSelectButton.Click += (_, _) => SetTool(EditTool.None);
        ToolMoveButton.Click += (_, _) => SetTool(EditTool.Move);
        ToolTextButton.Click += (_, _) => SetTool(EditTool.Text);
        var codes = Picker();
        var shapes = Picker();
        ToolCodeButton.Flyout = codes;
        ToolShapeButton.Flyout = shapes;
        // Filled when it opens, not when it is built: how many tiles fit on a line
        // depends on how wide the window is right now, and which way they run
        // depends on which way the plate is turned.
        codes.Opening += (_, _) =>
        {
            _openPicker = codes;
            codes.Placement = PickerPlacement();
            codes.Content = BuildCodePanel();
        };
        shapes.Opening += (_, _) =>
        {
            _openPicker = shapes;
            shapes.Placement = PickerPlacement();
            shapes.Content = BuildShapePanel();
        };
        ToolImageButton.Click += (_, _) => SetTool(EditTool.Image);
        ApplyToolButtons();
    }

    // ── The two menus ───────────────────────────────────────────────────────────

    // The symbologies live in BarcodeCatalog, shared with the properties bar: a
    // code offered here that the picker did not know would be a trap.
    //
    // One line per family - the linear codes, then the 2D ones - rather than a
    // paragraph of tiles that wraps wherever it happens to run out: the two kinds
    // answer different questions, and a line each says so without a word. When the
    // window is too narrow for a family to fit on one line it wraps, because the
    // alternative is a menu running off the side of the screen.
    private FrameworkElement BuildCodePanel()
    {
        var linear = BarcodeCatalog.All.Where(s => !s.TwoD).ToList();
        var twoD = BarcodeCatalog.All.Where(s => s.TwoD).ToList();

        if (_settings.ToolPlateHorizontal)
        {
            // A plate lying across the top drops its menu downwards, so the families
            // stand side by side and each one runs down the screen.
            var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            side.Children.Add(TitledColumn(SL2("linear"), linear));
            side.Children.Add(TitledColumn(SL2("twoD"), twoD));
            return side;
        }

        int perRow = Math.Max(FitAcross(linear.Count), FitAcross(twoD.Count));
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Section(SL2("linear")));
        panel.Children.Add(CodeTiles(linear, perRow));
        panel.Children.Add(Section(SL2("twoD")));
        panel.Children.Add(CodeTiles(twoD, perRow));
        return panel;
    }

    private StackPanel TitledColumn(string title, IReadOnlyList<BarcodeSpec> specs)
    {
        var column = new StackPanel { Spacing = 6 };
        column.Children.Add(Section(title));
        column.Children.Add(TileColumns(specs, FitDown(specs.Count),
            s => s.Label, s => SymbolPreview.Barcode(s.Key), s => PickBarcode(s.Key)));
        return column;
    }

    private ToolbarWrapPanel CodeTiles(IEnumerable<BarcodeSpec> specs, int perRow)
        => Tiles(specs, perRow, s => s.Label, s => SymbolPreview.Barcode(s.Key), s => PickBarcode(s.Key));

    private void PickBarcode(string key)
    {
        _barcodeKind = key;
        SetTool(EditTool.Barcode);
    }

    // The solid block is a shape like the others - it was a button of its own,
    // which is one more thing in the plate for no reason anyone could name. All
    // five on ONE line: they are five ways of doing the same thing.
    private FrameworkElement BuildShapePanel()
    {
        var shapes = new[]
        {
            ("rect", EditTool.Rect), ("line", EditTool.Line),
            ("ellipse", EditTool.Ellipse), ("circle", EditTool.Circle),
            ("fill", EditTool.Fill),
        };
        string Name((string Key, EditTool Tool) t) => SL2(t.Key == "fill" ? "fillShape" : t.Key);
        FrameworkElement Picture((string Key, EditTool Tool) t) => Miniature(t.Key);
        void Pick((string Key, EditTool Tool) t) => SetTool(t.Tool);

        if (_settings.ToolPlateHorizontal)
            return TileColumns(shapes, FitDown(shapes.Length), Name, Picture, Pick);
        return Tiles(shapes, FitAcross(shapes.Length), Name, Picture, Pick);
    }

    private static FrameworkElement Miniature(string key)
        => key == "fill" ? SymbolPreview.Fill() : SymbolPreview.Shape(key);

    // ── The picker's furniture ──────────────────────────────────────────────────

    // A flyout rather than a menu: a menu is a column of words, and what these
    // choices need is a picture of the thing being placed.
    private Flyout Picker()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Right };
        // A flyout wraps whatever it is given in a scroller, and a grid of tiles
        // that fits exactly still earns itself a pair of scrollbars along the edges.
        // There is nothing here to scroll.
        var bare = new Style(typeof(FlyoutPresenter));
        foreach (var (property, value) in new (DependencyProperty, object)[]
                 {
                     // A presenter is 456 dips wide out of the box, whatever it holds:
                     // that ceiling is what was folding a family of nine codes onto two
                     // lines however much room the window had.
                     (FrameworkElement.MaxWidthProperty, double.PositiveInfinity),
                     (FrameworkElement.MaxHeightProperty, double.PositiveInfinity),
                     (ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled),
                     (ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled),
                     (ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled),
                     (ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled),
                 })
            bare.Setters.Add(new Setter(property, value));
        flyout.FlyoutPresenterStyle = bare;
        return flyout;
    }

    /// <summary>Which way the menu opens: away from the edge the plate is against.</summary>
    private FlyoutPlacementMode PickerPlacement()
    {
        bool far, low;
        if (_settings.ToolPlateFree)
        {
            var origin = PlateOrigin(EditToolbar);
            far = origin.X > PreviewLayoutGrid.ActualWidth / 2;
            low = origin.Y > PreviewLayoutGrid.ActualHeight / 2;
        }
        else
        {
            far = _settings.ToolPlateAnchor.EndsWith("Right", StringComparison.Ordinal);
            low = _settings.ToolPlateAnchor.StartsWith("bottom", StringComparison.Ordinal);
        }
        if (_settings.ToolPlateHorizontal)
            return low ? FlyoutPlacementMode.Top : FlyoutPlacementMode.Bottom;
        return far ? FlyoutPlacementMode.Left : FlyoutPlacementMode.Right;
    }

    private FlyoutBase? _openPicker;

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.7,
        Margin = new Thickness(2, 2, 0, 0),
    };

    // A tile and the air around it. The width has to be SET on the panel: a flyout
    // sizes itself to its content, so it offers infinite width and a wrap panel
    // never wraps - the tail of the list simply ran off the side.
    private const double TileWidth = 74;
    private const double TileCell = TileWidth + 10;   // plus the button's own padding
    private const double TileRow = 68;                // picture + name + padding
    private const double TileGap = 4;

    /// <summary>How many tiles fit across the preview, never more than asked for.</summary>
    private int FitAcross(int wanted)
    {
        double room = PreviewLayoutGrid.ActualWidth > 0 ? PreviewLayoutGrid.ActualWidth : 900;
        room -= EditToolbar.ActualWidth + 60;         // the plate, and the menu's own frame
        return Math.Clamp((int)((room + TileGap) / (TileCell + TileGap)), 1, Math.Max(1, wanted));
    }

    /// <summary>How many tiles fit down the preview, never more than asked for.</summary>
    private int FitDown(int wanted)
    {
        double room = PreviewLayoutGrid.ActualHeight > 0 ? PreviewLayoutGrid.ActualHeight : 700;
        room -= EditToolbar.ActualHeight + 80;        // the plate, the frame, the title
        return Math.Clamp((int)((room + TileGap) / (TileRow + TileGap)), 1, Math.Max(1, wanted));
    }

    /// <summary>A grid of picture-and-name tiles, so many across.</summary>
    private ToolbarWrapPanel Tiles<T>(IEnumerable<T> items,
                                      int perRow,
                                      Func<T, string> label,
                                      Func<T, FrameworkElement> preview,
                                      Action<T> pick)
    {
        var wrap = new ToolbarWrapPanel { HorizontalSpacing = TileGap, VerticalSpacing = TileGap };
        wrap.Width = perRow * TileCell + (perRow - 1) * TileGap;
        foreach (var item in items) wrap.Children.Add(Tile(item, label, preview, pick));
        return wrap;
    }

    /// <summary>The same tiles running DOWN instead of across, so many to a column.</summary>
    private StackPanel TileColumns<T>(IReadOnlyList<T> items,
                                      int perColumn,
                                      Func<T, string> label,
                                      Func<T, FrameworkElement> preview,
                                      Action<T> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TileGap };
        StackPanel? column = null;
        for (int i = 0; i < items.Count; i++)
        {
            if (i % perColumn == 0)
            {
                column = new StackPanel { Spacing = TileGap };
                row.Children.Add(column);
            }
            column!.Children.Add(Tile(items[i], label, preview, pick));
        }
        return row;
    }

    private Button Tile<T>(T item,
                           Func<T, string> label,
                           Func<T, FrameworkElement> preview,
                           Action<T> pick)
    {
        var stack = new StackPanel { Spacing = 4, Width = TileWidth };
        stack.Children.Add(preview(item));
        stack.Children.Add(new TextBlock
        {
            Text = label(item),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        var button = new Button
        {
            Content = stack,
            Padding = new Thickness(5, 6, 5, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        button.Click += (_, _) => { _openPicker?.Hide(); pick(item); };
        return button;
    }

    // ── Arming a tool ───────────────────────────────────────────────────────

    private void SetTool(EditTool tool)
    {
        _tool = tool;
        // Arming a tool that places something drops the selection; switching
        // between the arrow and the cross-arrows keeps it — they are two ways of
        // handling the SAME element.
        if (IsPlacementTool) ClearInspectSelection();
        ApplyToolButtons();
        UpdatePreviewCursor();
        UpdateSelectionTools();     // the handles come and go with the tool
    }

    private void ApplyToolButtons()
    {
        var on = (Style)Application.Current.Resources["AccentButtonStyle"];
        var off = (Style)Application.Current.Resources["DefaultButtonStyle"];
        var onInk = ThemeInkOnAccent.Foreground;
        var offInk = ThemeInk.Foreground;

        void Dress(Button button, bool active, params Shape[] shapes)
        {
            button.Style = active ? on : off;
            var ink = active ? onInk : offInk;
            button.Foreground = ink;
            foreach (var shape in shapes)
            {
                // An outline shape is drawn by its stroke, a solid one by its fill.
                if (shape.StrokeThickness > 0) shape.Stroke = ink; else shape.Fill = ink;
            }
        }

        Dress(ToolSelectButton, _tool == EditTool.None, ToolSelectIcon);
        Dress(ToolMoveButton, _tool == EditTool.Move, ToolMoveIcon);
        Dress(ToolTextButton, _tool == EditTool.Text);
        Dress(ToolCodeButton, _tool == EditTool.Barcode,
              ToolCodeIcon.Children.OfType<Shape>().Append(ToolCodeMore).ToArray());
        Dress(ToolShapeButton,
            _tool is EditTool.Rect or EditTool.Line or EditTool.Ellipse or EditTool.Circle
                  or EditTool.Fill,
            ToolShapeIconRect, ToolShapeIconEllipse, ToolShapeMore);
        Dress(ToolImageButton, _tool == EditTool.Image,
              ToolImageIconFrame, ToolImageIconSun, ToolImageIconHill);

        ToolTipService.SetToolTip(ToolSelectButton, TipBlock(SL2("select")));
        ToolTipService.SetToolTip(ToolMoveButton, TipBlock(SL2("move")));
        ToolTipService.SetToolTip(ToolTextButton, TipBlock(SL2("text")));
        ToolTipService.SetToolTip(ToolCodeButton, TipBlock(SL2("code")));
        ToolTipService.SetToolTip(ToolShapeButton, TipBlock(SL2("shape")));
        ToolTipService.SetToolTip(ToolImageButton, TipBlock(SL2("image")));
    }

    private static string SL2(string key) => LocalizationService.Get("mode.tools." + key);

    /// <summary>Shown only in edit mode; leaving it disarms whatever was held.</summary>
    private void ApplyEditToolbar()
    {
        EditToolbar.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_editMode && _tool != DefaultTool) SetTool(DefaultTool);
        // Two plates pinned to the same place stand one above the other, and one
        // of them has just appeared or gone: on its own the mode switch takes the
        // whole spot back.
        ApplyPlatePlacement();
    }

    // ── Placing ─────────────────────────────────────────────────────────────

    /// <summary>True when the press was consumed to place something.</summary>
    private bool BeginPlacement(PointerRoutedEventArgs e)
    {
        if (!IsPlacementTool) return false;
        _placing = true;
        _placeStart = e.GetCurrentPoint(PreviewCanvas).Position;
        PreviewScrollViewer.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void UpdatePlacement(PointerRoutedEventArgs e)
    {
        if (!_placing) return;
        if (!e.GetCurrentPoint(PreviewScrollViewer).Properties.IsLeftButtonPressed)
        {
            FinishPlacement(e.GetCurrentPoint(PreviewCanvas).Position);
            return;
        }
        // Only the shapes are sized by dragging; text and codes have no box to pull.
        if (!SizeableTool) return;
        DrawPlacementBand(_placeStart, e.GetCurrentPoint(PreviewCanvas).Position);
    }

    // An image is in here for its WIDTH only: the box that gets dragged sets how
    // wide the picture prints, and its own proportions decide the rest.
    private bool SizeableTool =>
        _tool is EditTool.Rect or EditTool.Line or EditTool.Ellipse or EditTool.Circle
              or EditTool.Fill or EditTool.Image;

    private void FinishPlacement(Point end)
    {
        if (!_placing) return;
        _placing = false;
        RemovePlacementBand();

        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        var (fx, fy) = ContentFlip();

        // The drag box, in the field's own coordinates. A click that never moved
        // falls back to the default size, anchored where it was clicked.
        double x0 = Math.Min(_placeStart.X, end.X), y0 = Math.Min(_placeStart.Y, end.Y);
        double w = Math.Abs(end.X - _placeStart.X), h = Math.Abs(end.Y - _placeStart.Y);
        bool dragged = SizeableTool && (w > 4 || h > 4);
        if (!dragged) { x0 = _placeStart.X; y0 = _placeStart.Y; }
        if (fx < 0 || fy < 0) { x0 = _placeStart.X; y0 = _placeStart.Y; }

        int X = Round(x0), Y = Round(y0);
        int W = dragged ? Math.Max(1, Round(w)) : Round(DefaultBoxWidthMm * dpmm);
        int H = dragged ? Math.Max(1, Round(h)) : Round(DefaultBoxHeightMm * dpmm);

        // An image asks two questions before it can be written — which file, and how
        // it should be turned into black and white — so it leaves the synchronous
        // path here and comes back through InsertSnippet when the dialog closes.
        if (_tool == EditTool.Image)
        {
            SetTool(DefaultTool);
            _ = PlaceImageAsync(X, Y, dragged ? W : 0, dragged ? H : 0);
            return;
        }

        // Shift: keep the tool armed and put another one down. Shift rather than
        // Ctrl because Ctrl is already three things on this canvas - add to the
        // selection, snap a drag to the millimetre, zoom with the wheel - and a
        // modifier that means four things means none of them.
        var placed = _tool;
        bool again = IsHeld(Windows.System.VirtualKey.Shift);
        var snippet = BuildSnippet(X, Y, W, H, dpmm);
        if (!again) SetTool(DefaultTool);
        if (snippet is not null) InsertSnippet(snippet, placed, keepTool: again);
    }

    private void CancelPlacement()
    {
        _placing = false;
        RemovePlacementBand();
    }

    // A dashed outline following the pointer, in the accent colour, so a shape is
    // sized against what is already on the label rather than blind.
    private void DrawPlacementBand(Point a, Point b)
    {
        if (_placeBand is null)
        {
            _placeBand = new Rectangle
            {
                Fill = null,
                IsHitTestVisible = false,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
        }
        _placeBand.Stroke = new SolidColorBrush(AccentColor());
        double zoom = PreviewScrollViewer.ZoomFactor;
        _placeBand.StrokeThickness = zoom > 0 ? Math.Max(0.5, 1.5 / zoom) : 1.5;
        if (!PreviewCanvas.Children.Contains(_placeBand)) PreviewCanvas.Children.Add(_placeBand);
        Canvas.SetZIndex(_placeBand, 1001);
        _placeBand.Width = Math.Max(1, Math.Abs(b.X - a.X));
        _placeBand.Height = Math.Max(1, Math.Abs(b.Y - a.Y));
        Canvas.SetLeft(_placeBand, Math.Min(a.X, b.X));
        Canvas.SetTop(_placeBand, Math.Min(a.Y, b.Y));
    }

    private void RemovePlacementBand()
    {
        if (_placeBand?.Parent is Canvas parent) parent.Children.Remove(_placeBand);
    }

    private static int Round(double value) => (int)Math.Round(Math.Clamp(value, 0, 32000));

    // ── The ZPL that gets written ───────────────────────────────────────────

    private string? BuildSnippet(int x, int y, int w, int h, double dpmm)
    {
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        string origin = $"^FO{N(x)},{N(y)}";

        switch (_tool)
        {
            case EditTool.Text:
            {
                int size = Math.Max(6, Round(DefaultTextMm * dpmm));
                return $"{origin}^A0N,{N(size)},{N(size)}^FD{SL2("sampleText")}^FS";
            }
            case EditTool.Rect:
                return $"{origin}^GB{N(w)},{N(h)},2^FS";
            case EditTool.Line:
                // A line is a box with no height: the thickness is the third value.
                return $"{origin}^GB{N(Math.Max(w, 1))},0,2^FS";
            case EditTool.Ellipse:
                return $"{origin}^GE{N(w)},{N(h)},2^FS";
            case EditTool.Circle:
                return $"{origin}^GC{N(Math.Max(Math.Min(w, h), 1))},2^FS";
            case EditTool.Fill:
                // A border as thick as the SHORTER side fills the box in, and leaves
                // both of its dimensions alone: ZPL widens a box to at least its own
                // border, so a thicker one would square it off.
                return $"{origin}^GB{N(w)},{N(h)},{N(Math.Max(1, Math.Min(w, h)))}^FS";
            case EditTool.Barcode:
                return BuildBarcodeSnippet(origin, Math.Max(1, Round(DefaultCodeHeightMm * dpmm)), dpmm);
            default:
                return null;
        }
    }

    private string BuildBarcodeSnippet(string origin, int height, double dpmm)
    {
        var spec = BarcodeCatalog.ByKey(_barcodeKind) ?? BarcodeCatalog.All[0];
        int module = Math.Max(2, (int)Math.Round(dpmm / 2));
        // ^BY only means anything to the linear symbols.
        string by = spec.TwoD ? "" : "^BY2";
        return $"{origin}{by}^{spec.Command}{spec.Args(height, module)}^FD{spec.Sample}^FS";
    }

    /// <summary>
    /// Writes the snippet in just before the label ends, and selects it. Inserting
    /// at ^XZ rather than at the caret keeps the field order matching the drawing
    /// order: the newest element is the one on top.
    /// </summary>
    private void InsertSnippet(string snippet, EditTool tool = EditTool.None, bool keepTool = false)
    {
        int at = EndOfLabelOffset();
        string prefix = at > 0 && _currentText[at - 1] == '\n' ? "" : "\n";
        string text = prefix + snippet + "\n";

        ApplyEdit(new ZplPatcher.Edit(at, at, text));

        // Point the selection at what was just written, so the element comes up
        // framed and ready to be moved. Through SelectSpan, so the editor highlights
        // the new field as well as the label framing it.
        //
        // Not while the tool stays armed: the strip of tools would appear over the
        // element just placed, which is exactly where the next one is about to be
        // put down, and the click would land on the strip instead of the label.
        int at2 = at + prefix.Length;
        PreviewCursorHost.Focus(FocusState.Programmatic);
        if (keepTool) return;
        SelectSpan(at2, at2 + snippet.Length, revealInEditor: false, moveCaret: true);

        // A text field is placed to be written in, and what it holds until then is
        // the word "Text". Open the caret on it with that word selected, so the
        // first thing typed replaces it.
        if (tool == EditTool.Text)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                RefreshSelectionProperties();
                BeginInPlace(selectAll: true);
            });
    }

    /// <summary>Offset of the closing ^XZ, or the end of the text when there is none.</summary>
    private int EndOfLabelOffset()
    {
        var last = ZplRenderer.TokenizeForEditing(_currentText)
            .LastOrDefault(t => t.Command == "XZ");
        return last?.Start ?? _currentText.Length;
    }
}
