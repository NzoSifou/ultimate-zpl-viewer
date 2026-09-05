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
        var codes = BuildCodeFlyout();
        var shapes = BuildShapeFlyout();
        ToolCodeButton.Flyout = codes;
        ToolShapeButton.Flyout = shapes;
        // Picker() remembers the last one BUILT; remember the last one OPENED, so a
        // tile closes the flyout it actually belongs to.
        codes.Opening += (_, _) => _openPicker = codes;
        shapes.Opening += (_, _) => _openPicker = shapes;
        ToolFillButton.Click += (_, _) => SetTool(EditTool.Fill);
        ToolImageButton.Click += (_, _) => SetTool(EditTool.Image);
        ApplyToolButtons();
    }

    // ── The two menus ───────────────────────────────────────────────────────

    // The symbologies live in BarcodeCatalog, shared with the properties bar: a
    // code offered here that the picker did not know would be a trap.
    private Flyout BuildCodeFlyout()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Section(SL2("linear")));
        panel.Children.Add(Tiles(BarcodeCatalog.All.Where(s => !s.TwoD),
            s => s.Label, s => SymbolPreview.Barcode(s.Key), s => PickBarcode(s.Key)));
        panel.Children.Add(Section(SL2("twoD")));
        panel.Children.Add(Tiles(BarcodeCatalog.All.Where(s => s.TwoD),
            s => s.Label, s => SymbolPreview.Barcode(s.Key), s => PickBarcode(s.Key)));
        return Picker(panel);
    }

    private void PickBarcode(string key)
    {
        _barcodeKind = key;
        SetTool(EditTool.Barcode);
    }

    private Flyout BuildShapeFlyout()
    {
        var shapes = new[]
        {
            ("rect", EditTool.Rect), ("line", EditTool.Line),
            ("ellipse", EditTool.Ellipse), ("circle", EditTool.Circle),
        };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Tiles(shapes,
            t => SL2(t.Item1), t => SymbolPreview.Shape(t.Item1), t => SetTool(t.Item2),
            wrapAtFive: false));
        return Picker(panel);
    }

    // ── The picker's furniture ──────────────────────────────────────────────

    // A flyout rather than a menu: a menu is a column of words, and what these
    // choices need is a picture of the thing being placed. Laid out across, so a
    // dozen symbologies fit in a glance instead of a scroll.
    private Flyout Picker(FrameworkElement content)
    {
        var flyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.Right,
        };
        _openPicker = flyout;
        return flyout;
    }

    private FlyoutBase? _openPicker;

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Opacity = 0.7,
        Margin = new Thickness(2, 2, 0, 0),
    };

    // Five tiles to a row. The width has to be SET: a flyout sizes itself to its
    // content, so it offers infinite width and the panel never wraps — the tail of
    // the list simply ran off the side.
    private const double TileWidth = 74;
    private const double TilesPerRow = 5;

    /// <summary>A grid of picture-and-name tiles, five across.</summary>
    private ToolbarWrapPanel Tiles<T>(IEnumerable<T> items,
                                      Func<T, string> label,
                                      Func<T, FrameworkElement> preview,
                                      Action<T> pick,
                                      bool wrapAtFive = true)
    {
        var wrap = new ToolbarWrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
        if (wrapAtFive)
            wrap.Width = TilesPerRow * (TileWidth + 10) + (TilesPerRow - 1) * 4;
        foreach (var item in items)
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
            var captured = item;
            button.Click += (_, _) => { _openPicker?.Hide(); pick(captured); };
            wrap.Children.Add(button);
        }
        return wrap;
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
        var onInk = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        var offInk = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

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
        Dress(ToolCodeButton, _tool == EditTool.Barcode, ToolCodeIcon.Children.OfType<Shape>().ToArray());
        Dress(ToolShapeButton,
            _tool is EditTool.Rect or EditTool.Line or EditTool.Ellipse or EditTool.Circle,
            ToolShapeIconRect, ToolShapeIconEllipse);
        Dress(ToolFillButton, _tool == EditTool.Fill, ToolFillIcon);
        Dress(ToolImageButton, _tool == EditTool.Image,
              ToolImageIconFrame, ToolImageIconSun, ToolImageIconHill);

        ToolTipService.SetToolTip(ToolSelectButton, TipBlock(SL2("select")));
        ToolTipService.SetToolTip(ToolMoveButton, TipBlock(SL2("move")));
        ToolTipService.SetToolTip(ToolTextButton, TipBlock(SL2("text")));
        ToolTipService.SetToolTip(ToolCodeButton, TipBlock(SL2("code")));
        ToolTipService.SetToolTip(ToolShapeButton, TipBlock(SL2("shape")));
        ToolTipService.SetToolTip(ToolFillButton, TipBlock(SL2("fill")));
        ToolTipService.SetToolTip(ToolImageButton, TipBlock(SL2("image")));
    }

    private static string SL2(string key) => LocalizationService.Get("mode.tools." + key);

    /// <summary>Shown only in edit mode; leaving it disarms whatever was held.</summary>
    private void ApplyEditToolbar()
    {
        EditToolbar.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_editMode && _tool != DefaultTool) SetTool(DefaultTool);
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

        var placed = _tool;
        var snippet = BuildSnippet(X, Y, W, H, dpmm);
        SetTool(DefaultTool);
        if (snippet is not null) InsertSnippet(snippet, placed);
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
                // Thickness equal to the height is how ZPL fills a box in.
                return $"{origin}^GB{N(w)},{N(h)},{N(h)}^FS";
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
    private void InsertSnippet(string snippet, EditTool tool = EditTool.None)
    {
        int at = EndOfLabelOffset();
        string prefix = at > 0 && _currentText[at - 1] == '\n' ? "" : "\n";
        string text = prefix + snippet + "\n";

        ApplyEdit(new ZplPatcher.Edit(at, at, text));

        // Point the selection at what was just written, so the element comes up
        // framed and ready to be moved. Through SelectSpan, so the editor highlights
        // the new field as well as the label framing it.
        int at2 = at + prefix.Length;
        SelectSpan(at2, at2 + snippet.Length, revealInEditor: false, moveCaret: true);
        PreviewCursorHost.Focus(FocusState.Programmatic);

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
