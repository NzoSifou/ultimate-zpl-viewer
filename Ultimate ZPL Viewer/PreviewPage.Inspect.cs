using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using System.Numerics;
using Windows.UI;

namespace Ultimate_ZPL_Viewer;

// Selection: the preview and the code point at each other.
//
// Clicking an element on the label frames it and highlights the ZPL that produced
// it; putting the caret on that ZPL frames the element back. Both directions read
// the same thing — the source span each drawable carries out of the parser (see
// ZplDrawable.SourceStart/SourceEnd) — so neither side has to guess.
//
// This used to be a toolbar toggle of its own. It is now simply what EDIT mode
// does: there is no reason to pick an element while nothing can be changed, and
// no reason to have to ask for it once something can (see PreviewPage.Mode.cs).
public sealed partial class PreviewPage
{
    // Every element on the canvas and the drawable it came from, filled by
    // ZplRenderer.Draw on each redraw.
    private readonly Dictionary<UIElement, ZplDrawable> _hitMap = new();

    // The selected field, as a span in the ZPL text. -1 means nothing is selected.
    private int _selStart = -1;
    private int _selEnd = -1;

    private Rectangle? _inspectFrame;

    // Set while a second attempt at finding the selected element is pending.
    private bool _frameRetry;

    // Set while WE move the caret, so the caret move that follows is not read back
    // as the user picking a line — which would bounce the selection between the two
    // sides for as long as the editor kept reporting.
    private bool _syncingCaret;

    // Selecting is edit mode's business, and nothing else's.
    private bool InspectOn => _editMode;

    // ── Preview → code ───────────────────────────────────────────────────────

    /// <summary>
    /// The drawable under the pointer, or null. The press handler in
    /// PreviewPage.Edit.cs is the one caller: picking and starting to drag are the
    /// same gesture, so both have to happen on the way DOWN.
    /// </summary>
    /// <param name="hostPoint">Window coordinates — what the hit test wants.</param>
    /// <param name="canvasPoint">Label dots — what the fallback box test wants.</param>
    internal ZplDrawable? DrawableAt(Point hostPoint, Point canvasPoint)
    {
        var hit = VisualTreeHelper
            .FindElementsInHostCoordinates(hostPoint, PreviewCanvas)
            .OfType<UIElement>()
            .Select(el => _hitMap.TryGetValue(el, out var d) ? d : null)
            .FirstOrDefault(d => d is not null);

        // Barcodes are a crowd of thin bars: a click landing in a white gap hits
        // nothing, so fall back to whichever field's box contains the point.
        return hit ?? DrawableAtPoint(canvasPoint);
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

    /// <summary>
    /// Frames a field and lights it up in the code. <paramref name="moveCaret"/> is
    /// for a field the app has just WRITTEN: an edit leaves Monaco's caret past the
    /// end of what it inserted, and the report of that move would immediately drop
    /// this selection again, so the caret is sent back inside the field.
    /// </summary>
    private void SelectSpan(int start, int end, bool revealInEditor, bool moveCaret = false)
    {
        _selStart = start;
        _selEnd = end;
        // Picking one element is picking ONE element: whatever else was held goes.
        // Ctrl+click and the band are the two ways to keep more (PreviewPage.Multi.cs).
        _selected.Clear();
        _selected.Add(start);
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
                     ",\"caret\":" + (moveCaret ? "true" : "false") +
                     ",\"color\":\"" + colour + "\"}");
    }

    private void ClearInspectSelection()
    {
        _selStart = _selEnd = -1;
        _selected.Clear();
        UpdateExtraFrames();
        if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
        ClearHandles();
        UpdateSelectionTools();
        PostToEditor("{\"type\":\"clearHighlight\"}");
    }

    // Draws (or moves) the frame around the selected field. Called after every
    // redraw too, so the frame follows an edit instead of hanging in mid-air.
    internal void UpdateInspectFrame()
    {
        if (!InspectOn || _selStart < 0)
        {
            if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
            UpdateExtraFrames();
            UpdateSelectionTools();
            return;
        }

        // The element's box is read from the live visual, so the canvas has to have
        // been measured. Several redraws in a row — a character typed into the
        // content box — outrun the layout pass, and every element then reports a
        // size of zero. Forcing the pass here is what makes the frame keep up.
        try { PreviewCanvas.UpdateLayout(); } catch { }

        // Matched on the field's START alone. Its end moves with every edit inside
        // it — a character typed into the content box — and tracking that by
        // arithmetic drifts sooner or later; the start does not move unless the text
        // BEFORE the field changes, and then the selection is meant to be lost. The
        // end is taken from whatever was found, which also repairs any drift.
        Rect box = Rect.Empty;
        int end = _selEnd;
        foreach (var (element, drawable) in _hitMap)
        {
            if (drawable.SourceStart != _selStart) continue;
            end = drawable.SourceEnd;
            var b = BoundsInCanvas(element);
            if (b.IsEmpty) continue;
            box = box.IsEmpty ? b : Union(box, b);
        }
        _selEnd = end;

        if (box.IsEmpty)
        {
            // Either the edit removed the element, or the canvas simply has not been
            // measured yet: redraws coming one after another — a character typed into
            // the content field — outrun the layout pass, and every element reports a
            // size of zero until it catches up. Ask once more before concluding the
            // element is gone.
            if (!_frameRetry)
            {
                _frameRetry = true;
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateInspectFrame);
                return;
            }
            _frameRetry = false;
            if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
            UpdateSelectionTools();
            return;
        }
        _frameRetry = false;

        EnsureInspectFrame();
        // The frame survives a redraw (it is re-parented, not rebuilt), so the
        // offset a drag left on it has to be cleared or it would be applied twice.
        if (!_dragging) _inspectFrame!.Translation = default;
        // A couple of dots of air, so the frame reads as around the element rather
        // than as part of it.
        const double pad = 2;
        _inspectFrame!.Width = box.Width + 2 * pad;
        _inspectFrame.Height = box.Height + 2 * pad;
        Canvas.SetLeft(_inspectFrame, box.X - pad);
        Canvas.SetTop(_inspectFrame, box.Y - pad);
        _inspectFrame.Visibility = Visibility.Visible;
        UpdateInspectFrameThickness();
        UpdateExtraFrames();
        UpdateSelectionTools();
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
