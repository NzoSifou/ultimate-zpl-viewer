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

// ── Resizing, removing, copying ─────────────────────────────────────────────
// The handles sit in the overlay, in SCREEN space, so they stay the same size to
// the eye at any zoom — a handle drawn on the canvas would be a speck at 20 % and
// a slab at 400 %.
//
// Only what ZPL can actually resize gets them: a box or an ellipse by its two
// sides, a circle by its diameter, a linear barcode by the height of its bars. A
// text field is sized by its font, which is a number in the properties row, not a
// corner to pull.
public sealed partial class PreviewPage
{
    private enum Grip { None, NW, N, NE, E, SE, S, SW, W }

    private readonly Dictionary<Rectangle, Grip> _grips = new();
    private Grip _resizing = Grip.None;
    private Point _resizeStart;              // overlay space
    private Rect _resizeBox;                 // the element's box, overlay space
    private Rectangle? _resizeBand;

    private const double HandleSize = 9;

    private void InitResize()
    {
        DuplicateElementButton.Click += (_, _) => DuplicateSelection();
        DeleteElementButton.Click += (_, _) => DeleteSelection();
        EditOverlay.PointerMoved += Overlay_PointerMoved;
        EditOverlay.PointerReleased += (_, e) => EndResize(e);
        EditOverlay.PointerCaptureLost += (_, _) => CancelResize();
    }

    // ── What can be pulled ──────────────────────────────────────────────────

    /// <summary>Which handles this field offers, if any.</summary>
    private Grip[] GripsFor()
    {
        // Pulling a corner changes the label, so it belongs to the move tool for the
        // same reason dragging does.
        if (!CanMoveElements) return Array.Empty<Grip>();
        // The canvas is turned: a handle dragged right would resize downwards, and
        // guessing wrong is worse than not offering the handle.
        if (Math.Abs(_rotationDegrees) > 0.5) return Array.Empty<Grip>();
        if (_facts is null) return Array.Empty<Grip>();

        if (_facts.Shape is "GB" or "GE")
            return new[] { Grip.NW, Grip.N, Grip.NE, Grip.E, Grip.SE, Grip.S, Grip.SW, Grip.W };
        if (_facts.Shape == "GC")
            return new[] { Grip.NW, Grip.NE, Grip.SE, Grip.SW };
        // A 1D symbol: only its bars have a height to pull.
        if (_facts.Barcode is not null
            && BarcodeCatalog.ByCommand(_facts.Barcode, _facts.Data) is { TwoD: false })
            return new[] { Grip.S };
        return Array.Empty<Grip>();
    }

    /// <summary>Draws the handles around the selection, or clears them away.</summary>
    private void UpdateResizeHandles(Rect box)
    {
        var wanted = GripsFor();
        if (wanted.Length == 0 || _dragging) { ClearHandles(); return; }

        if (_grips.Count != wanted.Length)
        {
            ClearHandles();
            foreach (var grip in wanted) AddHandle(grip);
        }

        var accent = new SolidColorBrush(AccentColor());
        foreach (var (handle, grip) in _grips)
        {
            handle.Fill = accent;
            var at = GripPoint(box, grip);
            Canvas.SetLeft(handle, at.X - HandleSize / 2);
            Canvas.SetTop(handle, at.Y - HandleSize / 2);
        }
    }

    private void AddHandle(Grip grip)
    {
        var handle = new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            RadiusX = 2,
            RadiusY = 2,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 1,
        };
        handle.PointerPressed += (s, e) => BeginResize((Rectangle)s, e);
        _grips[handle] = grip;
        ResizeHandles.Children.Add(handle);
    }

    private void ClearHandles()
    {
        if (_grips.Count == 0) return;
        ResizeHandles.Children.Clear();
        _grips.Clear();
    }

    private static Point GripPoint(Rect box, Grip grip) => grip switch
    {
        Grip.NW => new Point(box.Left, box.Top),
        Grip.N => new Point(box.Left + box.Width / 2, box.Top),
        Grip.NE => new Point(box.Right, box.Top),
        Grip.E => new Point(box.Right, box.Top + box.Height / 2),
        Grip.SE => new Point(box.Right, box.Bottom),
        Grip.S => new Point(box.Left + box.Width / 2, box.Bottom),
        Grip.SW => new Point(box.Left, box.Bottom),
        _ => new Point(box.Left, box.Top + box.Height / 2),
    };

    // ── The gesture ─────────────────────────────────────────────────────────

    private void BeginResize(Rectangle handle, PointerRoutedEventArgs e)
    {
        if (!_grips.TryGetValue(handle, out var grip)) return;
        _resizing = grip;
        _resizeStart = e.GetCurrentPoint(EditOverlay).Position;
        _resizeBox = SelectionBoxInOverlay();
        EditOverlay.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing == Grip.None) return;
        if (!e.GetCurrentPoint(EditOverlay).Properties.IsLeftButtonPressed) { EndResize(e); return; }
        DrawResizeBand(ResizedBox(e.GetCurrentPoint(EditOverlay).Position));
        e.Handled = true;
    }

    private void EndResize(PointerRoutedEventArgs? e)
    {
        if (_resizing == Grip.None) return;
        var grip = _resizing;
        var box = e is null ? _resizeBox : ResizedBox(e.GetCurrentPoint(EditOverlay).Position);
        _resizing = Grip.None;
        RemoveResizeBand();
        if (e is not null) EditOverlay.ReleasePointerCapture(e.Pointer);
        ApplyResize(grip, box);
    }

    private void CancelResize()
    {
        if (_resizing == Grip.None) return;
        _resizing = Grip.None;
        RemoveResizeBand();
    }

    /// <summary>The box the pointer is asking for, in overlay space.</summary>
    private Rect ResizedBox(Point at)
    {
        double dx = at.X - _resizeStart.X, dy = at.Y - _resizeStart.Y;
        double l = _resizeBox.Left, t = _resizeBox.Top;
        double r = _resizeBox.Right, b = _resizeBox.Bottom;

        if (_resizing is Grip.NW or Grip.W or Grip.SW) l += dx;
        if (_resizing is Grip.NE or Grip.E or Grip.SE) r += dx;
        if (_resizing is Grip.NW or Grip.N or Grip.NE) t += dy;
        if (_resizing is Grip.SW or Grip.S or Grip.SE) b += dy;

        return new Rect(Math.Min(l, r), Math.Min(t, b),
                        Math.Max(1, Math.Abs(r - l)), Math.Max(1, Math.Abs(b - t)));
    }

    /// <summary>Writes the new size — and the new origin, when a top or left edge moved.</summary>
    private void ApplyResize(Grip grip, Rect box)
    {
        if (_selStart < 0 || _facts is null) return;
        double zoom = PreviewScrollViewer.ZoomFactor;
        if (zoom <= 0) return;

        // Overlay pixels back to label dots.
        double w = box.Width / zoom, h = box.Height / zoom;
        double dx = (box.Left - _resizeBox.Left) / zoom;
        double dy = (box.Top - _resizeBox.Top) / zoom;

        var edits = new List<ZplPatcher.Edit>();

        if (_facts.Shape is "GB" or "GE")
        {
            if (ZplPatcher.SetShape(_currentText, _selStart, _selEnd, w, h, null) is { } size)
                edits.Add(size);
        }
        else if (_facts.Shape == "GC")
        {
            if (ZplPatcher.SetShape(_currentText, _selStart, _selEnd, Math.Min(w, h), null, null) is { } size)
                edits.Add(size);
        }
        else if (ZplPatcher.SetBarcodeSize(_currentText, _selStart, _selEnd, h) is { } bars)
        {
            // Only the bars were pulled; the interpretation line under them is not
            // part of the height the command carries.
            edits.Add(bars);
        }

        // A handle on the top or left edge moves the element as well as sizing it.
        var (fx, fy) = ContentFlip();
        if ((Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
            && ZplPatcher.Move(_currentText, _selStart, _selEnd, dx * fx, dy * fy, SelectedDpmm) is { } move)
            edits.Add(move);

        if (edits.Count > 0) ApplyEdits(edits);
    }

    // ── The dashed preview ──────────────────────────────────────────────────

    private void DrawResizeBand(Rect box)
    {
        _resizeBand ??= new Rectangle
        {
            Fill = null,
            IsHitTestVisible = false,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        _resizeBand.Stroke = new SolidColorBrush(AccentColor());
        if (!EditOverlay.Children.Contains(_resizeBand)) EditOverlay.Children.Add(_resizeBand);
        _resizeBand.Width = box.Width;
        _resizeBand.Height = box.Height;
        Canvas.SetLeft(_resizeBand, box.Left);
        Canvas.SetTop(_resizeBand, box.Top);
    }

    private void RemoveResizeBand()
    {
        if (_resizeBand is not null) EditOverlay.Children.Remove(_resizeBand);
    }

    /// <summary>The selection frame's box, in overlay coordinates.</summary>
    private Rect SelectionBoxInOverlay()
    {
        if (_inspectFrame is null) return Rect.Empty;
        try
        {
            return _inspectFrame.TransformToVisual(EditOverlay)
                .TransformBounds(new Rect(0, 0, _inspectFrame.Width, _inspectFrame.Height));
        }
        catch { return Rect.Empty; }
    }

    // ── Removing and copying ────────────────────────────────────────────────

    private void DuplicateSelection()
    {
        if (_selStart < 0) return;
        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        double step = Math.Round(2 * dpmm);      // 2 mm, so the copy is visibly its own
        var edit = ZplPatcher.Duplicate(_currentText, _selStart, _selEnd, step, step, dpmm);
        if (edit is not { } value) return;

        // Follow the copy: it is the one the user is about to move.
        int at = value.Start + 1;                // past the newline the copy opens with
        ApplyEdit(value);
        SelectSpan(at, at + (value.Text.Length - 1), revealInEditor: false);
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }

    private void DeleteSelection()
    {
        if (_selStart < 0) return;
        var edit = ZplPatcher.Delete(_currentText, _selStart, _selEnd);
        if (edit is not { } value) return;
        ClearInspectSelection();
        ApplyEdit(value);
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }
}
