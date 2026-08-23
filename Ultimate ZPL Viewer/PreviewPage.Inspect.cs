using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Ultimate_ZPL_Viewer;

// Inspect mode: the preview and the code point at each other.
//
// Clicking an element on the label frames it and highlights the ZPL that produced
// it; putting the caret on that ZPL frames the element back. Both directions read
// the same thing — the source span each drawable carries out of the parser (see
// ZplDrawable.SourceStart/SourceEnd) — so neither side has to guess.
//
// The mode is off by default: while it is on, a click on the preview selects rather
// than doing nothing, and that is a change the user opts into from the toolbar.
public sealed partial class PreviewPage
{
    // Every element on the canvas and the drawable it came from, filled by
    // ZplRenderer.Draw on each redraw.
    private readonly Dictionary<UIElement, ZplDrawable> _hitMap = new();

    // The selected field, as a span in the ZPL text. -1 means nothing is selected.
    private int _selStart = -1;
    private int _selEnd = -1;

    private Rectangle? _inspectFrame;

    // Set while WE move the caret, so the caret move that follows is not read back
    // as the user picking a line — which would bounce the selection between the two
    // sides for as long as the editor kept reporting.
    private bool _syncingCaret;

    private bool InspectOn => _settings.InspectMode;

    // ── Toolbar toggle ───────────────────────────────────────────────────────

    private void InspectButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.InspectMode = !_settings.InspectMode;
        _settings.Save();
        ApplyInspectButtonState();
        if (!InspectOn) ClearInspectSelection();
    }

    // Lit = on. The accent button style is swapped in rather than a hand-painted
    // background: it tracks a custom accent colour on its own, and keeps the hover
    // and pressed states that a locally-set Background would flatten.
    private void ApplyInspectButtonState()
    {
        InspectButton.Style = InspectOn
            ? (Style)Application.Current.Resources["AccentButtonStyle"]
            : null;
        // The marquee is a Rectangle, not a glyph, so its stroke has to be told
        // which foreground it sits on.
        InspectIcon.Stroke = (Brush)Application.Current.Resources[
            InspectOn ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
        ToolTipService.SetToolTip(InspectButton,
            LocalizationService.Get(InspectOn ? "toolbar.inspectOn" : "toolbar.inspectOff"));
    }

    // ── Preview → code ───────────────────────────────────────────────────────

    private void PreviewCanvas_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!InspectOn) return;

        // Host coordinates: what FindElementsInHostCoordinates expects.
        var hit = VisualTreeHelper
            .FindElementsInHostCoordinates(e.GetPosition(null), PreviewCanvas)
            .OfType<UIElement>()
            .Select(el => _hitMap.TryGetValue(el, out var d) ? d : null)
            .FirstOrDefault(d => d is not null);

        // Barcodes are a crowd of thin bars: a click landing in a white gap hits
        // nothing, so fall back to whichever field's box contains the point.
        hit ??= DrawableAtPoint(e.GetPosition(PreviewCanvas));

        if (hit is null || hit.SourceStart < 0) { ClearInspectSelection(); return; }
        SelectSpan(hit.SourceStart, hit.SourceEnd, revealInEditor: true);
    }

    // Smallest field box containing the point — the tie-break when a click misses
    // the ink itself. Smallest, so a barcode inside a frame wins over the frame.
    private ZplDrawable? DrawableAtPoint(Point p)
    {
        // The boxes are widened by a few SCREEN pixels first: a ^GB rule is one to
        // three dots thick, which at a fitted zoom is barely a pixel and impossible to
        // hit squarely. The tolerance goes through the zoom so it stays the same size
        // to the eye however far in or out the user is.
        double zoom = PreviewScrollViewer.ZoomFactor;
        double tol = zoom > 0 ? 4.0 / zoom : 4.0;

        ZplDrawable? best = null;
        double bestArea = double.MaxValue;
        foreach (var (span, box) in FieldBoxes())
        {
            var rect = new Rect(box.X - tol, box.Y - tol, box.Width + 2 * tol, box.Height + 2 * tol);
            if (!rect.Contains(p)) continue;
            double area = rect.Width * rect.Height;
            if (area >= bestArea) continue;
            best = _hitMap.Values.FirstOrDefault(d => d.SourceStart == span.Start && d.SourceEnd == span.End);
            bestArea = area;
        }
        return best;
    }

    // One box per field, in PreviewCanvas coordinates.
    private IEnumerable<((int Start, int End) Span, Rect Box)> FieldBoxes()
    {
        var byField = new Dictionary<(int, int), Rect>();
        foreach (var (element, drawable) in _hitMap)
        {
            if (drawable.SourceStart < 0) continue;
            var box = BoundsInCanvas(element);
            if (box.IsEmpty) continue;
            var key = (drawable.SourceStart, drawable.SourceEnd);
            byField[key] = byField.TryGetValue(key, out var acc) ? Union(acc, box) : box;
        }
        foreach (var (key, box) in byField) yield return (key, box);
    }

    // An element's on-canvas box. Taken from the live visual rather than recomputed
    // from the model: rotation, condensed glyphs and baseline anchors are all
    // already baked into the transform here, and cannot drift out of step.
    private Rect BoundsInCanvas(UIElement element)
    {
        if (element is not FrameworkElement fe) return Rect.Empty;
        double w = fe.ActualWidth, h = fe.ActualHeight;
        if (w <= 0 || h <= 0) return Rect.Empty;
        try
        {
            return element.TransformToVisual(PreviewCanvas)
                          .TransformBounds(new Rect(0, 0, w, h));
        }
        catch { return Rect.Empty; }   // not in the tree (mid-redraw)
    }

    private static Rect Union(Rect a, Rect b)
    {
        double l = Math.Min(a.Left, b.Left), t = Math.Min(a.Top, b.Top);
        double r = Math.Max(a.Right, b.Right), bo = Math.Max(a.Bottom, b.Bottom);
        return new Rect(l, t, Math.Max(0, r - l), Math.Max(0, bo - t));
    }

    // ── Code → preview ───────────────────────────────────────────────────────

    /// <summary>The caret moved in the editor: frame whatever field it sits in.</summary>
    internal void OnEditorCaretMoved(int offset)
    {
        if (!InspectOn || _syncingCaret) return;

        var hit = _hitMap.Values
            .Where(d => d.SourceStart >= 0 && offset >= d.SourceStart && offset < d.SourceEnd)
            .OrderBy(d => d.SourceEnd - d.SourceStart)   // innermost field wins
            .FirstOrDefault();

        if (hit is null) { ClearInspectSelection(); return; }
        SelectSpan(hit.SourceStart, hit.SourceEnd, revealInEditor: false);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private void SelectSpan(int start, int end, bool revealInEditor)
    {
        _selStart = start;
        _selEnd = end;
        UpdateInspectFrame();

        var colour = AccentHex();
        if (revealInEditor)
        {
            // The editor is about to move its caret because we asked it to; ignore the
            // report that comes back.
            _syncingCaret = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _syncingCaret = false);
        }
        PostToEditor("{\"type\":\"highlightRange\",\"start\":" + start +
                     ",\"end\":" + end +
                     ",\"reveal\":" + (revealInEditor ? "true" : "false") +
                     ",\"color\":\"" + colour + "\"}");
    }

    private void ClearInspectSelection()
    {
        _selStart = _selEnd = -1;
        if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
        PostToEditor("{\"type\":\"clearHighlight\"}");
    }

    // Draws (or moves) the frame around the selected field. Called after every
    // redraw too, so the frame follows an edit instead of hanging in mid-air.
    internal void UpdateInspectFrame()
    {
        if (!InspectOn || _selStart < 0)
        {
            if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
            return;
        }

        Rect box = Rect.Empty;
        foreach (var (element, drawable) in _hitMap)
        {
            if (drawable.SourceStart != _selStart || drawable.SourceEnd != _selEnd) continue;
            var b = BoundsInCanvas(element);
            if (b.IsEmpty) continue;
            box = box.IsEmpty ? b : Union(box, b);
        }

        if (box.IsEmpty)
        {
            // The edit removed the element the selection pointed at.
            if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureInspectFrame();
        // A couple of dots of air, so the frame reads as around the element rather
        // than as part of it.
        const double pad = 2;
        _inspectFrame!.Width = box.Width + 2 * pad;
        _inspectFrame.Height = box.Height + 2 * pad;
        Canvas.SetLeft(_inspectFrame, box.X - pad);
        Canvas.SetTop(_inspectFrame, box.Y - pad);
        _inspectFrame.Visibility = Visibility.Visible;
        UpdateInspectFrameThickness();
    }

    private void EnsureInspectFrame()
    {
        // The canvas is rebuilt on every redraw, so the frame has to be re-parented
        // rather than created once.
        if (_inspectFrame is null)
        {
            _inspectFrame = new Rectangle
            {
                Fill = null,
                IsHitTestVisible = false,   // never stand between the user and an element
                RadiusX = 1,
                RadiusY = 1,
            };
        }
        _inspectFrame.Stroke = new SolidColorBrush(AccentColor());
        if (_inspectFrame.Parent is Canvas old && !ReferenceEquals(old, PreviewCanvas))
            old.Children.Remove(_inspectFrame);
        if (!PreviewCanvas.Children.Contains(_inspectFrame))
            PreviewCanvas.Children.Add(_inspectFrame);
        Canvas.SetZIndex(_inspectFrame, 1000);
    }

    // The canvas is in label dots and the ScrollViewer scales it, so a fixed
    // thickness would thin out as the user zooms out. Keep it ~2 screen pixels.
    internal void UpdateInspectFrameThickness()
    {
        if (_inspectFrame is null) return;
        double px = Math.Clamp(_settings.InspectFrameThickness, 1, 10);
        double zoom = PreviewScrollViewer.ZoomFactor;
        _inspectFrame.StrokeThickness = zoom > 0 ? Math.Max(0.5, px / zoom) : px;
    }

    private static Color AccentColor() =>
        Application.Current.Resources["SystemAccentColor"] is Color c ? c : Microsoft.UI.Colors.DodgerBlue;

    private static string AccentHex()
    {
        var c = AccentColor();
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
