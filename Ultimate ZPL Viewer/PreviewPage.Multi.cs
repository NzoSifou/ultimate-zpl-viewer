using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Ultimate_ZPL_Viewer;

// ── Several elements at once ────────────────────────────────────────────────
// Ctrl+click adds an element to the selection or takes it out again, a drag
// across empty label pulls a band around everything it touches, and Ctrl+A takes
// the lot. What follows — moving, nudging, duplicating, deleting — then applies
// to all of them, as one edit and one step of undo.
//
// A field is held by its START. That is what the frame already matches on, and it
// is the one offset that does not move while the field itself is being changed;
// an edit BEFORE it does move it, so every selected start is carried along when
// one is applied (see ApplyEdits).
//
// One of them is the primary — the last one picked. Everything that only makes
// sense for a single element (the properties, the turn, the order, the corner
// handles, typing on the label) belongs to it, and is put away while more than
// one is held.
public sealed partial class PreviewPage
{
    /// <summary>Every selected field, by its start. The primary is _selStart.</summary>
    private readonly List<int> _selected = new();

    private readonly List<Rectangle> _extraFrames = new();

    // The band pulled across empty label.
    private bool _banding;
    private Point _bandStart;
    private bool _bandAdds;              // Ctrl held: add to what is already held
    private Rectangle? _band;

    internal bool HasMultiSelection => _selected.Count > 1;

    // ── Keeping the list ────────────────────────────────────────────────────

    /// <summary>The spans of every selected field, ends resolved from what is drawn.</summary>
    private List<(int Start, int End)> SelectedSpans()
    {
        var spans = new List<(int, int)>();
        foreach (int start in _selected)
        {
            int end = EndOf(start);
            if (end > start) spans.Add((start, end));
        }
        spans.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return spans;
    }

    private int EndOf(int start)
    {
        if (start == _selStart && _selEnd > start) return _selEnd;
        foreach (var drawable in _hitMap.Values)
            if (drawable.SourceStart == start && drawable.SourceEnd > start)
                return drawable.SourceEnd;
        return -1;
    }

    /// <summary>Ctrl+click: in if it was out, out if it was in.</summary>
    private void ToggleSelected(int start, int end)
    {
        if (_selected.Remove(start))
        {
            if (_selected.Count == 0) { ClearInspectSelection(); return; }
            // The primary went: the one picked before it takes over.
            if (start == _selStart) PromoteTo(_selected[^1]);
            else { UpdateInspectFrame(); }
            return;
        }
        _selected.Add(start);
        _selStart = start;
        _selEnd = end;
        UpdateInspectFrame();
        HighlightPrimary();
    }

    private void PromoteTo(int start)
    {
        _selStart = start;
        _selEnd = EndOf(start);
        UpdateInspectFrame();
        HighlightPrimary();
    }

    /// <summary>Lights the primary up in the code, without moving the caret there.</summary>
    private void HighlightPrimary()
    {
        if (_selStart < 0 || _selEnd <= _selStart) return;
        PostToEditor("{\"type\":\"highlightRange\",\"start\":" + _selStart +
                     ",\"end\":" + _selEnd +
                     ",\"reveal\":false,\"caret\":false,\"color\":\"" + AccentHex() + "\"}");
    }

    /// <summary>Everything the label draws.</summary>
    private void SelectAllFields()
    {
        if (!_editMode) return;
        var spans = FieldBoxes().Select(f => f.Span).Distinct().OrderBy(s => s.Start).ToList();
        if (spans.Count == 0) return;
        _selected.Clear();
        foreach (var span in spans) _selected.Add(span.Start);
        _selStart = spans[^1].Start;
        _selEnd = spans[^1].End;
        UpdateInspectFrame();
        HighlightPrimary();
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }

    /// <summary>Carries the held fields past an edit that changed the text before them.</summary>
    private void ShiftSelection(IReadOnlyList<ZplPatcher.Edit> edits)
    {
        if (_selected.Count == 0) return;
        for (int i = 0; i < _selected.Count; i++)
        {
            int shift = 0;
            foreach (var edit in edits)
                if (edit.End <= _selected[i]) shift += edit.Text.Length - (edit.End - edit.Start);
            _selected[i] += shift;
        }
        if (_selStart >= 0)
        {
            int shift = 0;
            foreach (var edit in edits)
                if (edit.End <= _selStart) shift += edit.Text.Length - (edit.End - edit.Start);
            _selStart += shift;
            _selEnd += shift;
        }
    }

    // ── The extra frames ────────────────────────────────────────────────────

    /// <summary>Frames every held field except the primary, which has its own.</summary>
    private void UpdateExtraFrames()
    {
        foreach (var frame in _extraFrames)
            if (frame.Parent is Canvas parent) parent.Children.Remove(frame);
        _extraFrames.Clear();
        if (!_editMode || _selected.Count < 2) return;

        var boxes = FieldBoxes().ToDictionary(f => f.Span.Start, f => f.Box);
        double zoom = PreviewScrollViewer.ZoomFactor;
        double thickness = zoom > 0
            ? Math.Max(0.5, Math.Clamp(_settings.InspectFrameThickness, 1, 10) / zoom)
            : 2;

        foreach (int start in _selected)
        {
            if (start == _selStart) continue;
            if (!boxes.TryGetValue(start, out var box) || box.IsEmpty) continue;
            const double pad = 2;
            var frame = new Rectangle
            {
                Fill = null,
                IsHitTestVisible = false,
                RadiusX = 1,
                RadiusY = 1,
                Stroke = new SolidColorBrush(AccentColor()),
                StrokeThickness = thickness,
                Width = box.Width + 2 * pad,
                Height = box.Height + 2 * pad,
            };
            Canvas.SetLeft(frame, box.X - pad);
            Canvas.SetTop(frame, box.Y - pad);
            Canvas.SetZIndex(frame, 1000);
            PreviewCanvas.Children.Add(frame);
            _extraFrames.Add(frame);
        }
    }

    // ── The band ────────────────────────────────────────────────────────────

    /// <summary>True when the press opened a band instead of picking something.</summary>
    private bool BeginBand(PointerRoutedEventArgs e)
    {
        // Only the two tools that select. A tool that places something has its own
        // use for a drag on empty label.
        if (IsPlacementTool) return false;
        _banding = true;
        _bandAdds = IsHeld(Windows.System.VirtualKey.Control);
        _bandStart = e.GetCurrentPoint(PreviewCanvas).Position;
        PreviewScrollViewer.CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private void UpdateBand(PointerRoutedEventArgs e)
    {
        if (!_banding) return;
        if (!e.GetCurrentPoint(PreviewScrollViewer).Properties.IsLeftButtonPressed)
        {
            FinishBand(e.GetCurrentPoint(PreviewCanvas).Position);
            return;
        }
        DrawBand(_bandStart, e.GetCurrentPoint(PreviewCanvas).Position);
    }

    private void FinishBand(Point end)
    {
        if (!_banding) return;
        _banding = false;
        RemoveBand();

        var box = new Rect(Math.Min(_bandStart.X, end.X), Math.Min(_bandStart.Y, end.Y),
                           Math.Abs(end.X - _bandStart.X), Math.Abs(end.Y - _bandStart.Y));
        // A press that never travelled is a click on nothing: it drops the selection.
        if (box.Width < 4 && box.Height < 4)
        {
            if (!_bandAdds) ClearInspectSelection();
            return;
        }

        var caught = FieldBoxes()
            .Where(f => Intersects(f.Box, box))
            .Select(f => f.Span)
            .OrderBy(s => s.Start)
            .ToList();
        if (caught.Count == 0)
        {
            if (!_bandAdds) ClearInspectSelection();
            return;
        }

        if (!_bandAdds) _selected.Clear();
        foreach (var span in caught)
            if (!_selected.Contains(span.Start)) _selected.Add(span.Start);

        _selStart = caught[^1].Start;
        _selEnd = caught[^1].End;
        UpdateInspectFrame();
        HighlightPrimary();
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }

    private void CancelBand()
    {
        _banding = false;
        RemoveBand();
    }

    private static bool Intersects(Rect a, Rect b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private void DrawBand(Point a, Point b)
    {
        _band ??= new Rectangle
        {
            IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        var accent = AccentColor();
        _band.Stroke = new SolidColorBrush(accent);
        _band.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, accent.R, accent.G, accent.B));
        double zoom = PreviewScrollViewer.ZoomFactor;
        _band.StrokeThickness = zoom > 0 ? Math.Max(0.5, 1.5 / zoom) : 1.5;
        if (!PreviewCanvas.Children.Contains(_band)) PreviewCanvas.Children.Add(_band);
        Canvas.SetZIndex(_band, 1001);
        _band.Width = Math.Max(1, Math.Abs(b.X - a.X));
        _band.Height = Math.Max(1, Math.Abs(b.Y - a.Y));
        Canvas.SetLeft(_band, Math.Min(a.X, b.X));
        Canvas.SetTop(_band, Math.Min(a.Y, b.Y));
    }

    private void RemoveBand()
    {
        if (_band?.Parent is Canvas parent) parent.Children.Remove(_band);
    }
}
