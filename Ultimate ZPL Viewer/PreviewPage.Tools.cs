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
    private enum EditTool { None, Text, Barcode, Rect, Line, Ellipse, Circle, Fill }

    private EditTool _tool = EditTool.None;
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
        ToolTextButton.Click += (_, _) => SetTool(EditTool.Text);
        ToolCodeButton.Flyout = BuildCodeFlyout();
        ToolShapeButton.Flyout = BuildShapeFlyout();
        ToolFillButton.Click += (_, _) => SetTool(EditTool.Fill);
        ApplyToolButtons();
    }

    // ── The two menus ───────────────────────────────────────────────────────

    // One button for every symbology rather than one per code: a dozen buttons
    // down the side would bury the four that get used.
    private static readonly (string Key, string Label)[] BarcodeKinds =
    {
        ("code128",  "Code 128"),
        ("gs1-128",  "GS1-128"),
        ("code39",   "Code 39"),
        ("code93",   "Code 93"),
        ("ean13",    "EAN-13"),
        ("ean8",     "EAN-8"),
        ("upca",     "UPC-A"),
        ("itf",      "ITF (2 sur 5)"),
        ("codabar",  "Codabar"),
        ("",         ""),               // separator
        ("qr",       "QR Code"),
        ("datamatrix", "Data Matrix"),
        ("aztec",    "Aztec"),
        ("pdf417",   "PDF417"),
    };

    private MenuFlyout BuildCodeFlyout()
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Right };
        foreach (var (key, label) in BarcodeKinds)
        {
            if (key.Length == 0) { flyout.Items.Add(new MenuFlyoutSeparator()); continue; }
            var item = new MenuFlyoutItem { Text = label, Tag = key };
            item.Click += (s, _) =>
            {
                _barcodeKind = (string)((MenuFlyoutItem)s).Tag;
                SetTool(EditTool.Barcode);
            };
            flyout.Items.Add(item);
        }
        return flyout;
    }

    private MenuFlyout BuildShapeFlyout()
    {
        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Right };
        void Add(string key, EditTool tool)
        {
            var item = new MenuFlyoutItem { Text = LocalizationService.Get("mode.tools." + key) };
            item.Click += (_, _) => SetTool(tool);
            flyout.Items.Add(item);
        }
        Add("rect", EditTool.Rect);
        Add("line", EditTool.Line);
        Add("ellipse", EditTool.Ellipse);
        Add("circle", EditTool.Circle);
        return flyout;
    }

    // ── Arming a tool ───────────────────────────────────────────────────────

    private void SetTool(EditTool tool)
    {
        _tool = tool;
        if (tool != EditTool.None) ClearInspectSelection();
        ApplyToolButtons();
        UpdatePreviewCursor();
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
        Dress(ToolTextButton, _tool == EditTool.Text);
        Dress(ToolCodeButton, _tool == EditTool.Barcode, ToolCodeIcon.Children.OfType<Shape>().ToArray());
        Dress(ToolShapeButton,
            _tool is EditTool.Rect or EditTool.Line or EditTool.Ellipse or EditTool.Circle,
            ToolShapeIcon);
        Dress(ToolFillButton, _tool == EditTool.Fill, ToolFillIcon);

        ToolTipService.SetToolTip(ToolSelectButton, TipBlock(SL2("select")));
        ToolTipService.SetToolTip(ToolTextButton, TipBlock(SL2("text")));
        ToolTipService.SetToolTip(ToolCodeButton, TipBlock(SL2("code")));
        ToolTipService.SetToolTip(ToolShapeButton, TipBlock(SL2("shape")));
        ToolTipService.SetToolTip(ToolFillButton, TipBlock(SL2("fill")));
    }

    private static string SL2(string key) => LocalizationService.Get("mode.tools." + key);

    /// <summary>Shown only in edit mode; leaving it disarms whatever was held.</summary>
    private void ApplyEditToolbar()
    {
        EditToolbar.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        if (!_editMode && _tool != EditTool.None) SetTool(EditTool.None);
    }

    // ── Placing ─────────────────────────────────────────────────────────────

    /// <summary>True when the press was consumed to place something.</summary>
    private bool BeginPlacement(PointerRoutedEventArgs e)
    {
        if (_tool == EditTool.None) return false;
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

    private bool SizeableTool =>
        _tool is EditTool.Rect or EditTool.Line or EditTool.Ellipse or EditTool.Circle or EditTool.Fill;

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

        var snippet = BuildSnippet(X, Y, W, H, dpmm);
        SetTool(EditTool.None);
        if (snippet is not null) InsertSnippet(snippet);
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

    // Sample data per symbology: what each one actually accepts. An EAN-13 seeded
    // with letters would draw nothing and read as a broken tool.
    private string BuildBarcodeSnippet(string origin, int height, double dpmm)
    {
        int mag = Math.Max(2, (int)Math.Round(dpmm / 2));   // 2D module size
        string h = height.ToString(CultureInfo.InvariantCulture);
        string m = mag.ToString(CultureInfo.InvariantCulture);
        return _barcodeKind switch
        {
            "code128" => $"{origin}^BY2^BCN,{h},Y,N,N^FD1234567890^FS",
            // AI (01) + a full 14-digit GTIN, in the >; >8 form the printers expect.
            "gs1-128" => $"{origin}^BY2^BCN,{h},Y,N,Y^FD>;>80112345678901231^FS",
            "code39"  => $"{origin}^BY2^B3N,N,{h},Y,N^FDABC123^FS",
            "code93"  => $"{origin}^BY2^BAN,{h},Y,N^FDABC123^FS",
            "ean13"   => $"{origin}^BY2^BEN,{h},Y,N^FD123456789012^FS",
            "ean8"    => $"{origin}^BY2^B8N,{h},Y,N^FD1234567^FS",
            "upca"    => $"{origin}^BY2^BUN,{h},Y,N^FD12345678901^FS",
            "itf"     => $"{origin}^BY2^B2N,{h},Y,N,N^FD12345678^FS",
            "codabar" => $"{origin}^BY2^BKN,N,{h},Y,N,A,A^FD12345^FS",
            "qr"          => $"{origin}^BQN,2,{m}^FDQA,https://example.com^FS",
            "datamatrix"  => $"{origin}^BXN,{m},200^FDDATA-MATRIX^FS",
            "aztec"       => $"{origin}^BON,{m}^FDAZTEC^FS",
            "pdf417"      => $"{origin}^B7N,{m},5^FDPDF417^FS",
            _ => $"{origin}^BY2^BCN,{h},Y,N,N^FD1234567890^FS",
        };
    }

    /// <summary>
    /// Writes the snippet in just before the label ends, and selects it. Inserting
    /// at ^XZ rather than at the caret keeps the field order matching the drawing
    /// order: the newest element is the one on top.
    /// </summary>
    private void InsertSnippet(string snippet)
    {
        int at = EndOfLabelOffset();
        string prefix = at > 0 && _currentText[at - 1] == '\n' ? "" : "\n";
        string text = prefix + snippet + "\n";

        ApplyEdit(new ZplPatcher.Edit(at, at, text));

        // Point the selection at what was just written, so the element comes up
        // framed and ready to be moved (the redraw that follows finds it by span).
        _selStart = at + prefix.Length;
        _selEnd = _selStart + snippet.Length;
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }

    /// <summary>Offset of the closing ^XZ, or the end of the text when there is none.</summary>
    private int EndOfLabelOffset()
    {
        var last = ZplRenderer.TokenizeForEditing(_currentText)
            .LastOrDefault(t => t.Command == "XZ");
        return last?.Start ?? _currentText.Length;
    }
}
