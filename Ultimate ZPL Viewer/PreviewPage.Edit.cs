using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.System;

namespace Ultimate_ZPL_Viewer;

// ── Editing on the label itself ─────────────────────────────────────────────
// Edit mode turns the preview into a canvas: press an element, drag it, let go.
// The ZPL follows — not by being regenerated, but by having the numbers of that
// one ^FO rewritten (see ZplPatcher), through Monaco so Ctrl+Z undoes the drag.
//
// While the pointer is down nothing is written. The elements are simply offset on
// screen, at whatever rate the mouse moves; the text is edited ONCE, on release.
// Writing on every mouse move would reparse the document sixty times a second and
// fill the undo history with two hundred one-dot steps.
public sealed partial class PreviewPage
{
    private bool _dragging;
    private bool _dragMoved;                       // passed the slop threshold
    private Point _dragStart;                      // in canvas space (label dots)
    private double _dragDx, _dragDy;               // applied offset, label dots
    private (double X, double Y)? _dragOrigin;     // the field's ^FO as written
    private readonly List<UIElement> _dragElements = new();
    private double _snapDuringDrag;

    // Below this, a press is a click that happened to wobble, not a drag.
    private const double DragSlopDots = 2;

    private void InitEditGestures()
    {
        // Selection is taken on PRESS rather than on tap: the same gesture has to
        // pick the element and start moving it, and a tap only arrives once the
        // pointer is already back up.
        //
        // All three on the SCROLLVIEWER, not on the canvas. The canvas sits inside a
        // ScrollViewer whose own manipulation takes the pointer over as soon as it
        // moves. That takeover cannot be prevented — not by marking the press
        // handled, not by capturing first, not by turning the scroll modes off (that
        // last one stops the moves from arriving at all) — but it does not have to
        // matter: the moves keep coming here regardless. What used to end the drag
        // was reacting to the lost capture, so nothing does now.
        PreviewScrollViewer.PointerPressed += PreviewCanvas_PointerPressed;
        PreviewScrollViewer.PointerMoved += PreviewCanvas_PointerMoved;
        PreviewScrollViewer.PointerReleased += PreviewCanvas_PointerReleased;
        // The button coming back up does not always reach the ScrollViewer — the
        // manipulation running underneath swallows it — and a drag left open would
        // never be written. Three ways in, all idempotent: whichever arrives first
        // closes the gesture, the others find nothing left to do.
        Root.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(PreviewCanvas_PointerReleased), true);
        PreviewScrollViewer.PointerCaptureLost += (_, _) => FinishDrag();
        RotateElementButton.Click += RotateElementButton_Click;
        // Arrow-key nudging only makes sense while the preview itself has focus,
        // which it takes when an element is picked — never while the caret is in
        // the editor, where the arrows belong to the text.
        PreviewCursorHost.IsTabStop = true;
        PreviewCursorHost.UseSystemFocusVisuals = false;
        PreviewCursorHost.KeyDown += PreviewHost_KeyDown;
    }

    // ── Picking and dragging ────────────────────────────────────────────────

    private void PreviewCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_editMode) return;
        if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;

        // A tool is armed: this press puts something down rather than picking
        // something up.
        if (BeginPlacement(e)) return;

        var hit = DrawableAt(e.GetCurrentPoint(null).Position, e.GetCurrentPoint(PreviewCanvas).Position);
        if (hit is null || hit.SourceStart < 0)
        {
            // Empty space: drop the selection, and leave the event alone so the
            // press still pans the view.
            ClearInspectSelection();
            return;
        }

        SelectSpan(hit.SourceStart, hit.SourceEnd, revealInEditor: true);
        PreviewCursorHost.Focus(FocusState.Pointer);

        if (!ZplPatcher.CanMove(_currentText, _selStart, _selEnd)) return;

        _dragging = true;
        _dragMoved = false;
        _dragDx = _dragDy = 0;
        _dragStart = e.GetCurrentPoint(PreviewCanvas).Position;
        _dragOrigin = ZplPatcher.Origin(_currentText, _selStart, _selEnd, SelectedDpmm);
        CollectDragElements();
        // The capture is what routes the rest of the gesture here. The ScrollViewer's
        // own manipulation takes it back on the first movement — and that is fine,
        // the moves keep coming — so losing it is deliberately NOT treated as the
        // end of the drag. Handled keeps the panning code off the same gesture.
        PreviewScrollViewer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_placing) { UpdatePlacement(e); return; }
        if (!_dragging) return;
        // The button came back up somewhere we never heard about (outside the
        // window, or swallowed by the manipulation): close the gesture here.
        if (!e.GetCurrentPoint(PreviewScrollViewer).Properties.IsLeftButtonPressed)
        {
            FinishDrag();
            return;
        }
        var p = e.GetCurrentPoint(PreviewCanvas).Position;
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;

        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < DragSlopDots) return;
        _dragMoved = true;

        // Ctrl snaps to the millimetre. Snapping the DELTA would leave the element
        // wherever it happened to start, so the target position is what is rounded
        // — which is also exactly what the patcher will write.
        double step = SnapStepDots(e.KeyModifiers);
        _snapDuringDrag = step;
        if (step > 0 && _dragOrigin is { } o)
        {
            dx = Math.Round((o.X + dx) / step) * step - o.X;
            dy = Math.Round((o.Y + dy) / step) * step - o.Y;
        }

        _dragDx = dx;
        _dragDy = dy;
        ApplyDragOffset();
    }

    private void PreviewCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_placing) { FinishPlacement(e.GetCurrentPoint(PreviewCanvas).Position); return; }
        FinishDrag();
    }

    /// <summary>Closes the gesture and writes the move, if there was one.</summary>
    private void FinishDrag()
    {
        if (!_dragging) return;
        bool moved = _dragMoved && (_dragDx != 0 || _dragDy != 0);
        double dx = _dragDx, dy = _dragDy;
        double step = _snapDuringDrag;
        EndDragVisuals(keepOffset: moved);
        if (moved) MoveSelection(dx, dy, step);
    }

    private void CancelDrag()
    {
        if (!_dragging) return;
        _dragDx = _dragDy = 0;
        EndDragVisuals(keepOffset: false);
    }

    private void EndDragVisuals(bool keepOffset)
    {
        _dragging = false;
        _dragMoved = false;
        _dragOrigin = null;
        if (!keepOffset)
        {
            _dragDx = _dragDy = 0;
            ApplyDragOffset();
        }
        _dragElements.Clear();
        UpdateSelectionTools();
    }

    /// <summary>Writes the move into the ZPL, and lets the usual redraw follow.</summary>
    private void MoveSelection(double dx, double dy, double snapDots)
    {
        // A mirrored or upside-down label (^PMY / ^POI) draws its content through a
        // flip, so what the pointer did on screen is the opposite of what the
        // field's own coordinates have to do.
        var (fx, fy) = ContentFlip();
        var edit = ZplPatcher.Move(_currentText, _selStart, _selEnd,
                                   dx * fx, dy * fy, SelectedDpmm, snapDots);
        if (edit is { } value) ApplyEdit(value);
    }

    private void RotateElementButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selStart < 0) return;
        var edit = ZplPatcher.Rotate(_currentText, _selStart, _selEnd);
        if (edit is { } value) ApplyEdit(value);
        // Clicking the button moved the focus onto it, and the arrow keys and
        // Ctrl+Z are listened for on the preview: without this, a turn could not be
        // taken back with the keyboard right after it was made.
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }

    private void PreviewHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+Z is a page-wide accelerator now (RegisterShortcuts): it has to work
        // whether the focus landed on the preview, the toolbar or nowhere at all.
        if (!_editMode || _selStart < 0 || _dragging) return;
        double step = IsHeld(VirtualKey.Shift) ? 10 : 1;
        double dx = 0, dy = 0;
        switch (e.Key)
        {
            case VirtualKey.Left: dx = -step; break;
            case VirtualKey.Right: dx = step; break;
            case VirtualKey.Up: dy = -step; break;
            case VirtualKey.Down: dy = step; break;
            default: return;
        }
        e.Handled = true;
        MoveSelection(dx, dy, 0);
    }

    // ── Applying an edit ────────────────────────────────────────────────────

    /// <summary>
    /// Hands one range replacement to Monaco. It lands on the editor's undo stack
    /// and comes back as an ordinary text change, so the dirty flag, the
    /// highlighting, the analysis and the redraw all react as if it were typed.
    /// </summary>
    private void ApplyEdit(ZplPatcher.Edit edit) => ApplyEdits(new[] { edit });

    /// <summary>
    /// Applies several replacements as ONE change. Every range is measured against
    /// the document as it stands now — Monaco resolves them together — so callers
    /// never have to compensate for each other's offsets.
    /// </summary>
    private void ApplyEdits(IReadOnlyList<ZplPatcher.Edit> edits)
    {
        var valid = edits
            .Where(e => e.Start >= 0 && e.End <= _currentText.Length && e.Start <= e.End)
            .OrderBy(e => e.Start)
            .ToList();
        if (valid.Count == 0) return;

        // The selection is remembered as a span of the text: an edit inside it that
        // changes its length moves its end.
        foreach (var edit in valid)
        {
            int delta = edit.Text.Length - (edit.End - edit.Start);
            if (_selStart >= 0 && edit.Start >= _selStart && edit.End <= _selEnd) _selEnd += delta;
        }

        // The same edits are applied HERE as well, right away, rather than waiting
        // for Monaco to echo the document back. Two changes in quick succession —
        // a spinner clicked twice, a height that drags its width along — would
        // otherwise both be computed against a text one round trip out of date, and
        // the second would cut at the wrong offset. It also puts the change on the
        // label immediately instead of a frame later.
        var text = _currentText;
        foreach (var edit in valid.OrderByDescending(e => e.Start))
            text = text[..edit.Start] + edit.Text + text[edit.End..];
        _currentText = text;
        if (!_isDirty) { _isDirty = true; UpdateDocumentTitle(); }
        RefreshPreview(SizeUpdate.TextEdited);
        ScheduleHighlighting();

        // Monaco gets the same edits, for its undo stack and its own view of the
        // document. What comes back matches what is already here, so the echo is
        // recognised as ours and changes nothing a second time.
        if (_editorReady)
        {
            var parts = valid.Select(e =>
                $"{{\"start\":{e.Start},\"end\":{e.End}," +
                $"\"text\":{System.Text.Json.JsonSerializer.Serialize(e.Text)}}}");
            PostToEditor("{\"type\":\"applyEdit\",\"edits\":[" + string.Join(",", parts) + "]}");
        }
    }

    // ── The moving picture ──────────────────────────────────────────────────

    private void CollectDragElements()
    {
        _dragElements.Clear();
        foreach (var (element, drawable) in _hitMap)
        {
            if (drawable.SourceStart != _selStart || drawable.SourceEnd != _selEnd) continue;
            EnableTranslation(element);
            _dragElements.Add(element);
        }
        if (_inspectFrame is not null) EnableTranslation(_inspectFrame);
    }

    // UIElement.Translation is a composition facade, and a XAML element ignores it
    // until the facade is switched on for that element.
    private static void EnableTranslation(UIElement element)
        => Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(element, true);

    /// <summary>
    /// Offsets the dragged element and its frame on screen, without touching the
    /// text. This goes through UIElement.Translation, NOT RenderTransform: the
    /// renderer already uses RenderTransform to place and rotate what it draws —
    /// a text field is a row of separately positioned glyphs — and writing over
    /// that piles the whole field into the canvas corner.
    /// </summary>
    private void ApplyDragOffset()
    {
        var (fx, fy) = ContentFlip();
        var shift = new Vector3((float)(_dragDx * fx), (float)(_dragDy * fy), 0);
        foreach (var element in _dragElements) element.Translation = shift;
        if (_inspectFrame is not null)
            _inspectFrame.Translation = new Vector3((float)_dragDx, (float)_dragDy, 0);
        UpdateSelectionTools();
    }

    /// <summary>
    /// ±1 per axis, telling on-screen movement from movement in the field's own
    /// coordinates. ^PMY mirrors left/right, ^POI turns the whole label over.
    /// </summary>
    private (double X, double Y) ContentFlip()
    {
        bool mirror = _model?.MirrorImage == true;
        bool invert = _model?.InvertOrientation == true;
        return ((mirror ? -1 : 1) * (invert ? -1 : 1), invert ? -1 : 1);
    }

    private double SnapStepDots(VirtualKeyModifiers modifiers)
        => modifiers.HasFlag(VirtualKeyModifiers.Control) ? Math.Max(1, SelectedDpmm) : 0;

    // KeyRoutedEventArgs does not carry the modifiers, so they are read from the
    // thread's keyboard state instead.
    private static bool IsHeld(VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ── The tools floating over the selection ───────────────────────────────

    /// <summary>
    /// Puts the rotate button and the position readout just above the selection —
    /// or just below it when the selection is at the very top of the view. Called
    /// on every selection change, redraw, zoom and scroll, so the bar never lags
    /// behind the element it belongs to.
    /// </summary>
    internal void UpdateSelectionTools()
    {
        if (!_editMode || _selStart < 0 || _inspectFrame is null
            || _inspectFrame.Visibility != Visibility.Visible)
        {
            SelectionTools.Visibility = Visibility.Collapsed;
            return;
        }

        Rect box;
        try
        {
            box = _inspectFrame.TransformToVisual(EditOverlay)
                .TransformBounds(new Rect(0, 0, _inspectFrame.Width, _inspectFrame.Height));
        }
        catch { SelectionTools.Visibility = Visibility.Collapsed; return; }

        RotateElementButton.Visibility = ZplPatcher.CanRotate(_currentText, _selStart, _selEnd)
            ? Visibility.Visible : Visibility.Collapsed;
        SelectionCoords.Text = SelectionPositionText();
        SelectionCoords.Visibility = SelectionCoords.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        // The content field, the symbology picker and the "…" button (Props.cs).
        RefreshSelectionProperties();

        // Nothing left to show (a field with neither a rotation nor an origin).
        if (RotateElementButton.Visibility == Visibility.Collapsed
            && SelectionCoords.Visibility == Visibility.Collapsed
            && SelectionData.Visibility == Visibility.Collapsed)
        {
            SelectionTools.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionTools.Visibility = Visibility.Visible;
        SelectionTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = SelectionTools.DesiredSize.Width, h = SelectionTools.DesiredSize.Height;

        const double gap = 8;
        double top = box.Top - h - gap;
        if (top < 0) top = box.Bottom + gap;      // no room above: sit underneath
        double left = box.Left + (box.Width - w) / 2;
        left = Math.Max(0, Math.Min(left, Math.Max(0, EditOverlay.ActualWidth - w)));
        top = Math.Max(0, Math.Min(top, Math.Max(0, EditOverlay.ActualHeight - h)));

        Canvas.SetLeft(SelectionTools, left);
        Canvas.SetTop(SelectionTools, top);
    }

    /// <summary>The selected field's origin, in the unit the user works in.</summary>
    private string SelectionPositionText()
    {
        var origin = ZplPatcher.Origin(_currentText, _selStart, _selEnd, SelectedDpmm);
        if (origin is not { } o) return "";
        var (fx, fy) = ContentFlip();
        double x = o.X + _dragDx * fx, y = o.Y + _dragDy * fy;
        double dpmm = SelectedDpmm;
        if (dpmm <= 0) return "";
        string unit = UnitConverter.UnitLabel(_settings.Unit);
        string n(double dots) => UnitConverter.FormatLength(
            UnitConverter.FromMillimeters(dots / dpmm, _settings.Unit));
        return string.Create(CultureInfo.InvariantCulture, $"{n(x)} × {n(y)} {unit}");
    }
}
