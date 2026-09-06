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
    private DateTime _lastFieldPress = DateTime.MinValue;
    private bool _openInPlaceOnRelease;
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
        PreviewScrollViewer.PointerCaptureLost += (_, _) => { CancelBand(); FinishDrag(); };
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
        // A press INSIDE the words being typed is the caret being placed, and none
        // of this applies to it. Anywhere else finishes the typing — here rather
        // than on the lost focus, because the canvas is about to be rebuilt.
        if (IsEditingInPlace)
        {
            // A press ON the words places the caret and may open a selection; the
            // event stops here, or the ScrollViewer under it would take over the
            // gesture. Anywhere else finishes the typing — here rather than on the
            // lost focus, because the canvas is about to be rebuilt.
            if (InPlacePointerPressed(e)) { e.Handled = true; return; }
            EndInPlace();
            return;
        }

        // A tool is armed: this press puts something down rather than picking
        // something up.
        if (BeginPlacement(e)) return;

        var hit = DrawableAt(e.GetCurrentPoint(null).Position, e.GetCurrentPoint(PreviewCanvas).Position);
        if (hit is null || hit.SourceStart < 0)
        {
            // Empty label: pull a band across it and take everything it touches.
            // A press that never travels ends as a click on nothing, which drops
            // the selection — what this did before.
            if (BeginBand(e)) return;
            ClearInspectSelection();
            return;
        }

        // Ctrl: add this element to what is already held, or take it back out.
        if (IsHeld(VirtualKey.Control))
        {
            e.Handled = true;
            ToggleSelected(hit.SourceStart, hit.SourceEnd);
            PreviewCursorHost.Focus(FocusState.Pointer);
            return;
        }

        // Pressing INSIDE a selection of several keeps them all: that press is the
        // start of moving the group, not a new pick.
        if (HasMultiSelection && _selected.Contains(hit.SourceStart))
        {
            e.Handled = true;
            if (_selStart != hit.SourceStart) PromoteTo(hit.SourceStart);
            PreviewCursorHost.Focus(FocusState.Pointer);
            if (CanMoveElements && ZplPatcher.CanMove(_currentText, _selStart, _selEnd))
                StartDrag(e);
            return;
        }

        // Twice on the same field, quickly: that is "let me type in it". Counted
        // here rather than through DoubleTapped, which never arrives — the press is
        // marked handled to keep the panning off the drag gesture.
        bool again = hit.SourceStart == _selStart
                     && (DateTime.UtcNow - _lastFieldPress).TotalMilliseconds < 450;
        _lastFieldPress = DateTime.UtcNow;

        SelectSpan(hit.SourceStart, hit.SourceEnd, revealInEditor: true);

        if (again)
        {
            // Opened on the RELEASE, not here: the button coming back up hands the
            // focus to whatever is under the pointer, which took it straight back
            // off the box and closed it again forty milliseconds after it opened.
            _openInPlaceOnRelease = true;
            e.Handled = true;
            return;
        }
        PreviewCursorHost.Focus(FocusState.Pointer);

        // The arrow selects and stops there. Dragging the label around is what the
        // move tool is for — the whole point of having two.
        if (!CanMoveElements) return;
        if (!ZplPatcher.CanMove(_currentText, _selStart, _selEnd)) return;
        StartDrag(e);
    }

    /// <summary>Opens the move gesture on whatever is held.</summary>
    private void StartDrag(PointerRoutedEventArgs e)
    {
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
        if (IsEditingInPlace) { InPlacePointerMoved(e); return; }
        if (_banding) { UpdateBand(e); return; }
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
        if (IsEditingInPlace) { InPlacePointerReleased(); return; }
        if (_banding) { FinishBand(e.GetCurrentPoint(PreviewCanvas).Position); return; }
        if (_placing) { FinishPlacement(e.GetCurrentPoint(PreviewCanvas).Position); return; }
        FinishDrag();

        if (!_openInPlaceOnRelease) return;
        _openInPlaceOnRelease = false;     // the release reaches here twice
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            RefreshSelectionProperties();  // BeginInPlace reads the field from these
            BeginInPlace(selectAll: true);
        });
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
        // Every held field, in one edit and one step of undo. Each rewrites its own
        // ^FO, so the ranges never overlap.
        var edits = new List<ZplPatcher.Edit>();
        foreach (var (start, end) in SelectedSpans())
            if (ZplPatcher.Move(_currentText, start, end, dx * fx, dy * fy, SelectedDpmm, snapDots)
                is { } move) edits.Add(move);
        if (edits.Count > 0) ApplyEdits(edits);
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
        if (!_editMode || _dragging) return;
        // Ctrl+A takes everything the label draws, whether or not anything is held.
        if (e.Key == VirtualKey.A && IsHeld(VirtualKey.Control))
        {
            e.Handled = true;
            SelectAllFields();
            return;
        }
        // Copy, cut and paste, on the label rather than in the code: what travels is
        // the ZPL of the held fields, as text.
        if (IsHeld(VirtualKey.Control) && e.Key is VirtualKey.C or VirtualKey.X or VirtualKey.V)
        {
            e.Handled = true;
            if (e.Key == VirtualKey.V) _ = PasteAsync();
            else CopySelection(cut: e.Key == VirtualKey.X);
            return;
        }
        if (_selStart < 0) return;
        // This handler sits on an ancestor of the preview, so every key pressed
        // inside the in-place box passes through it on its way up. While there is a
        // caret in the words, the keys are the caret's: Backspace rubs out a letter
        // rather than the whole element, and the arrows move through the text.
        if (IsEditingInPlace) return;
        double step = IsHeld(VirtualKey.Shift) ? 10 : 1;
        double dx = 0, dy = 0;
        switch (e.Key)
        {
            // Nudging is moving: the arrow tool does not do it either. Deleting
            // is not, so Del keeps working whichever of the two is held.
            case VirtualKey.Left: if (!CanMoveElements) return; dx = -step; break;
            case VirtualKey.Right: if (!CanMoveElements) return; dx = step; break;
            case VirtualKey.Up: if (!CanMoveElements) return; dy = -step; break;
            case VirtualKey.Down: if (!CanMoveElements) return; dy = step; break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                e.Handled = true;
                DeleteSelection();
                return;
            // The two keys every list and every spreadsheet uses to start editing
            // the thing that is selected.
            case VirtualKey.F2:
            case VirtualKey.Enter:
                e.Handled = true;
                // Typing goes into one field; there is no caret for a group.
                if (!HasMultiSelection) BeginInPlace(selectAll: true);
                return;
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

        ShiftSelection(valid);

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
        // Not while the caret is in the words: the canvas is rebuilt whole, and the
        // box being typed into is one of its children. The box is showing the field
        // in its own font at its own size, so there is nothing to catch up on until
        // the caret leaves (PreviewPage.InPlace.cs).
        if (!IsEditingInPlace) RefreshPreview(SizeUpdate.TextEdited);
        ScheduleHighlighting();
        // And ask the frame to find its element again once the new canvas has been
        // measured. RefreshPreview posts that itself, but it declines to run at all
        // when one redraw is already under way — which is exactly what happens when
        // edits arrive one after another, a character at a time.
        if (!IsEditingInPlace)
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateInspectFrame);

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
        var held = new HashSet<int>(_selected);
        foreach (var (element, drawable) in _hitMap)
        {
            if (!held.Contains(drawable.SourceStart)) continue;
            EnableTranslation(element);
            _dragElements.Add(element);
        }
        if (_inspectFrame is not null) EnableTranslation(_inspectFrame);
        foreach (var frame in _extraFrames) EnableTranslation(frame);
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
        var frameShift = new Vector3((float)_dragDx, (float)_dragDy, 0);
        if (_inspectFrame is not null) _inspectFrame.Translation = frameShift;
        foreach (var frame in _extraFrames) frame.Translation = frameShift;
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
    /// Puts the rotate button and the position readout just below the selection —
    /// or just above it when there is no room left underneath. Called
    /// on every selection change, redraw, zoom and scroll, so the bar never lags
    /// behind the element it belongs to.
    /// </summary>
    internal void UpdateSelectionTools()
    {
        // Holding something changes what a drag would do, and the pointer says so.
        // Here because this runs on every selection change there is. Twice: once
        // now, and once after the frame has been laid out. Redrawing the label
        // replaces the element the pointer is standing on, and the framework
        // re-resolves the cursor from scratch when it does - reaching the default
        // rather than the one this page put on the preview. Setting it again once
        // that has happened is what makes it stick.
        UpdatePreviewCursor();
        ScheduleCursorUpdate();

        // Hiding the bar takes the focus out of whatever is being typed into it.
        // While it holds the caret it stays, even if the element it belongs to
        // momentarily has nothing to frame — emptying a field does exactly that.
        bool typing = SelectionToolsHasFocus();

        // The caret is in the words themselves: the bar would sit right on top of
        // what is being typed, and the frame belongs to an element being moved.
        if (IsEditingInPlace)
        {
            ClearHandles();
            SelectionTools.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_editMode || _selStart < 0 || _inspectFrame is null
            || _inspectFrame.Visibility != Visibility.Visible)
        {
            ClearHandles();
            if (!typing) SelectionTools.Visibility = Visibility.Collapsed;
            return;
        }

        Rect box;
        try
        {
            box = _inspectFrame.TransformToVisual(EditOverlay)
                .TransformBounds(new Rect(0, 0, _inspectFrame.Width, _inspectFrame.Height));
        }
        catch { ClearHandles(); SelectionTools.Visibility = Visibility.Collapsed; return; }

        // Scrolled or zoomed out of sight, the element has no edge to lean on: the
        // strip was landing wherever the clamp left it, which for anything above
        // the view is the top of the preview - in among the two standing plates,
        // looking for all the world as though it belonged to them. A strip whose
        // element cannot be seen is not attached to anything, so it goes too.
        // Unless it holds the caret: what is being typed is not thrown away for a
        // scroll.
        if (!typing
            && (box.Right <= 0 || box.Bottom <= 0
                || box.Left >= EditOverlay.ActualWidth || box.Top >= EditOverlay.ActualHeight))
        {
            ClearHandles();
            SelectionTools.Visibility = Visibility.Collapsed;
            return;
        }

        // A group is moved, copied and deleted; everything else — the turn, the
        // order, the properties — is about one element and waits for one.
        RotateElementButton.Visibility = !HasMultiSelection
            && ZplPatcher.CanRotate(_currentText, _selStart, _selEnd)
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(RotateElementButton, TipBlock(LocalizationService.Get("mode.act.rotate")));
        ToolTipService.SetToolTip(DuplicateElementButton, TipBlock(LocalizationService.Get("mode.act.duplicate")));
        ToolTipService.SetToolTip(DeleteElementButton, TipBlock(LocalizationService.Get("mode.act.delete")));
        SelectionCoords.Text = HasMultiSelection
            ? string.Format(LocalizationService.Get("mode.act.several"), _selected.Count)
            : SelectionPositionText();
        SelectionCoords.Visibility = SelectionCoords.Text.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        // The content field, the symbology picker and the "…" button (Props.cs).
        RefreshSelectionProperties();
        // The corner handles, for what ZPL can actually resize (Resize.cs).
        UpdateResizeHandles(box);
        // Forward / backward, when there is a neighbour to step past (Order.cs).
        UpdateOrderButtons();

        // Nothing left to show (a field with neither a rotation nor an origin).
        if (!typing
            && RotateElementButton.Visibility == Visibility.Collapsed
            && SelectionCoords.Visibility == Visibility.Collapsed
            && !HasMultiSelection)
        {
            SelectionTools.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionTools.Visibility = Visibility.Visible;
        SelectionTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = SelectionTools.DesiredSize.Width, h = SelectionTools.DesiredSize.Height;

        const double gap = 8;
        // The strip leans on one edge of the element and unfolds AWAY from it, so
        // the properties never cover what is being edited and the row of buttons
        // never moves out from under the pointer. Which edge is the user's to
        // choose; the other one is still used when there is no room on this one.
        double belowTop = box.Bottom + gap, aboveTop = box.Top - h - gap;
        bool roomBelow = belowTop + h <= EditOverlay.ActualHeight;
        bool roomAbove = aboveTop >= 0;
        bool goAbove = _settings.ElementPlateAbove ? roomAbove || !roomBelow : !roomBelow && roomAbove;
        // Above, the properties hang over the buttons rather than under them: it is
        // what keeps the buttons against the element whatever the panel does.
        SetPropsSide(goAbove);
        SelectionTools.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        h = SelectionTools.DesiredSize.Height;
        aboveTop = box.Top - h - gap;
        double top = goAbove ? aboveTop : belowTop;
        double left = box.Left + (box.Width - w) / 2;
        left = Math.Max(0, Math.Min(left, Math.Max(0, EditOverlay.ActualWidth - w)));
        top = Math.Max(0, Math.Min(top, Math.Max(0, EditOverlay.ActualHeight - h)));

        // The two standing plates are drawn above this strip: one sliding under
        // either of them loses its first buttons. It steps aside - to whichever
        // side of the plate it is already nearer, and only when it really is
        // underneath. Stepping always to the RIGHT was fine while the tools lived
        // in the left-hand corner and wrong the moment they could be put anywhere:
        // a plate against the right edge sent the strip off to the right edge too.
        left = StepAside(left, top, w, h, EditToolbar);
        left = StepAside(left, top, w, h, ModeSwitch);

        Canvas.SetLeft(SelectionTools, left);
        Canvas.SetTop(SelectionTools, top);
    }

    /// <summary>Moves the strip clear of a plate it would otherwise hide under.</summary>
    private double StepAside(double left, double top, double w, double h, Border plate)
    {
        if (plate.Visibility != Visibility.Visible) return left;
        Rect box;
        try
        {
            box = plate.TransformToVisual(EditOverlay)
                .TransformBounds(new Rect(0, 0, plate.ActualWidth, plate.ActualHeight));
        }
        catch { return left; }
        if (box.Width <= 0 || box.Height <= 0) return left;
        if (top >= box.Bottom || top + h <= box.Top) return left;   // not level with it
        if (left >= box.Right || left + w <= box.Left) return left;  // already clear

        const double gap = 8;
        double past = box.Right + gap, before = box.Left - gap - w;
        bool roomPast = past + w <= EditOverlay.ActualWidth;
        bool roomBefore = before >= 0;
        if (!roomPast && !roomBefore) return left;
        if (roomPast && (!roomBefore || left + w / 2 >= box.Left + box.Width / 2)) return past;
        return before;
    }

    /// <summary>Whether the caret sits somewhere inside the properties bar.</summary>
    private bool SelectionToolsHasFocus()
    {
        if (XamlRoot is null) return false;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)
            as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, SelectionTools)) return true;
            focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
        }
        return false;
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
