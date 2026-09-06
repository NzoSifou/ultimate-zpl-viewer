using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.System;

namespace Ultimate_ZPL_Viewer;

// ── Typing on the label itself ──────────────────────────────────────────────
// A text field is edited where it is printed: double-click it (or press F2) and a
// caret appears inside the words, which go on looking exactly as they will print.
//
// Everything visible here is drawn by the renderer or measured against it. The
// words are the renderer's own blocks, rebuilt on every keystroke. The caret and
// the selection are rectangles placed from widths measured in the field's own
// font. And a TextBox — invisible, untouchable — holds the text, the caret index
// and the selection, so that every key, every shortcut and the IME behave the way
// they do everywhere else.
//
// It is arranged that way because a TextBox cannot be made to agree with the
// renderer. Asked for the same string in the same family at the same size and
// weight, its text engine measured 296 dots where the renderer measured 251: it
// does not resolve a condensed face the way a TextBlock does. Anything positioned
// by the box's own layout — its caret, its selection — therefore stands beside
// the words instead of on them.
//
// While the caret is there the canvas is NOT redrawn. Every keystroke still
// rewrites the ^FD and travels the usual path — Monaco's undo stack, the code
// view, the analyser — but the picture on screen is what is drawn here. A full
// redraw rebuilds the canvas whole and would tear all of it out on every letter.
public sealed partial class PreviewPage
{
    private TextBox? _inPlace;                 // the keyboard model, never shown
    private ZplText? _inPlaceField;
    private bool _inPlaceClosing;
    private readonly List<TextBlock> _inPlaceInk = new();
    private readonly List<Rectangle> _inPlaceSelection = new();
    private Rectangle? _caret;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _blink;
    private int _blinkTicks;
    private int _shownStart = -1, _shownLength = -1;
    private bool _selectingByPointer;
    private int _selectAnchor;

    private ScrollMode _scrollHorizontal = ScrollMode.Auto;
    private ScrollMode _scrollVertical = ScrollMode.Auto;

    // The last payload written into the ^FD. Compared against rather than re-read
    // from the field, because the field is NOT re-read while the caret is in it:
    // the redraw that would refresh it is exactly what is being held back.
    private string _inPlaceWritten = "";

    /// <summary>True while a field is being typed into on the label.</summary>
    internal bool IsEditingInPlace => _inPlace?.Parent is not null;

    // ── Getting in ──────────────────────────────────────────────────────────

    /// <summary>The selected field, when it is a text field that can be typed into.</summary>
    private ZplText? EditableText()
    {
        if (!_editMode || _selStart < 0) return null;
        if (_facts?.FontName is null || _facts.DataStart < 0) return null;
        return _hitMap.Values.OfType<ZplText>()
            .FirstOrDefault(t => t.SourceStart == _selStart);
    }

    /// <summary>Opens the caret inside the selected text field.</summary>
    private void BeginInPlace(bool selectAll)
    {
        if (IsEditingInPlace) return;
        if (EditableText() is not { } field) return;
        _inPlaceField = field;
        _inPlaceWritten = _facts!.Data ?? "";

        var box = new TextBox
        {
            Text = _inPlaceWritten.Replace("\\&", "\n", StringComparison.Ordinal),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            // Never seen: it is the text and the keyboard and nothing else, and what
            // is on screen is drawn from its contents. It stays hit-testable all the
            // same — an element that is not cannot hold the keyboard focus, and then
            // no key reaches it at all.
            Opacity = 0,
        };
        // Parked far outside the label. It has to stay hit-testable — an element
        // that is not cannot hold the keyboard focus, and then no key reaches it at
        // all — and anything hit-testable lying over the words would take the
        // presses meant for the caret and answer them with its own idea of where
        // the letters are.
        Canvas.SetLeft(box, -20000);
        Canvas.SetTop(box, -20000);
        Canvas.SetZIndex(box, 1003);

        box.TextChanged += InPlace_TextChanged;
        // PreviewKeyDown, not KeyDown: a TextBox that accepts returns swallows Enter
        // to insert the line break, and an ordinary handler never sees it. This one
        // runs first and can take the key for itself.
        box.PreviewKeyDown += InPlace_KeyDown;
        box.KeyUp += (_, _) => RefreshCaret();
        box.LostFocus += (_, _) =>
        {
            // The ScrollViewer under the label claims the focus on any press inside
            // it. That is not the user leaving: a press that really leaves has
            // already closed the box before this runs.
            if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), PreviewScrollViewer))
            {
                box.Focus(FocusState.Programmatic);
                return;
            }
            EndInPlace();
        };

        _inPlace = box;
        PreviewCanvas.Children.Add(box);

        // The ScrollViewer's own panning takes the pointer over the moment it moves,
        // which is what stopped a drag across the words from selecting any of them.
        // Nothing needs panning while there is a caret in them.
        _scrollHorizontal = PreviewScrollViewer.HorizontalScrollMode;
        _scrollVertical = PreviewScrollViewer.VerticalScrollMode;
        PreviewScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
        PreviewScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
        // The box that holds the text is parked twenty thousand dips off the
        // canvas, where it cannot swallow a click. A ScrollViewer scrolls to
        // whatever child has just taken the focus, and that is where it was
        // scrolling to - the label jumped to its top-left corner the moment the
        // caret opened. Nothing in the preview ever wants that.
        PreviewScrollViewer.BringIntoViewOnFocusChange = false;
        // And where the view is right now, so anything that manages to move it
        // anyway can be put straight back (ViewChanged, PutViewBack).
        _frozenH = PreviewScrollViewer.HorizontalOffset;
        _frozenV = PreviewScrollViewer.VerticalOffset;

        // The glyphs underneath would double every letter; they are replaced by the
        // ones drawn here, from the same helper.
        HideDrawnField();
        DrawInk();

        // The frame, the handles and the bar all belong to an element being moved
        // around, not to one being typed into — and the bar sits right on top of it.
        if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
        ClearHandles();
        SelectionTools.Visibility = Visibility.Collapsed;

        box.Focus(FocusState.Programmatic);
        if (selectAll) { _selectAnchor = 0; box.SelectAll(); }
        else { _selectAnchor = box.Text.Length; box.Select(box.Text.Length, 0); }

        StartBlinking();
        RefreshCaret(force: true);
    }

    private void HideDrawnField()
    {
        foreach (var (element, drawable) in _hitMap)
            if (drawable is ZplText && drawable.SourceStart == _selStart)
                element.Opacity = 0;
    }

    // ── The words ───────────────────────────────────────────────────────────

    private Transform InkTransform(ZplText field)
        => ZplRenderer.TextTransform(field, ZplRenderer.MeasureText(field), 0);

    /// <summary>
    /// Draws the field as it stands, through the renderer's own helper. Called again
    /// on every keystroke: it is the picture of the label while the redraws are held
    /// back, and for a rotated ^FO field it is also what keeps the words anchored —
    /// that anchor is the corner of the ROTATED bounding box, which moves as the
    /// text grows.
    /// </summary>
    private void DrawInk()
    {
        Remove(_inPlaceInk);
        if (_inPlace is null || _inPlaceField is null) return;

        var shown = _inPlaceField with { Text = _inPlace.Text };
        foreach (var block in ZplRenderer.TextBlocksFor(shown))
        {
            block.IsHitTestVisible = false;
            Canvas.SetZIndex(block, 1004);      // over the selection, under the caret
            PreviewCanvas.Children.Add(block);
            _inPlaceInk.Add(block);
        }
    }

    private static void Remove<T>(List<T> shapes) where T : FrameworkElement
    {
        foreach (var shape in shapes)
            if (shape.Parent is Canvas parent) parent.Children.Remove(shape);
        shapes.Clear();
    }

    // ── Where each letter begins ────────────────────────────────────────────

    // Measured in the field's own font — the one the words are really drawn in.
    // Cached: the same line is measured again on every caret move, and one
    // measurement per character adds up.
    private readonly Dictionary<string, double[]> _advances = new();

    private double[] Advances(string line)
    {
        if (_inPlaceField is null) return new double[] { 0 };
        var layout = ZplRenderer.MeasureText(_inPlaceField);
        string key = layout.FontSize.ToString("F2") + " " + line;
        if (_advances.TryGetValue(key, out var cached)) return cached;

        var widths = new double[line.Length + 1];
        var probe = new TextBlock
        {
            FontFamily = new FontFamily(_inPlaceField.Font),
            FontSize = layout.FontSize,
            FontWeight = layout.Weight,
            TextWrapping = TextWrapping.NoWrap,
        };
        for (int i = 1; i <= line.Length; i++)
        {
            probe.Text = line[..i];
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[i] = probe.DesiredSize.Width;
        }
        if (_advances.Count > 64) _advances.Clear();
        _advances[key] = widths;
        return widths;
    }

    private string[] Lines() => (_inPlace?.Text ?? "").Split('\n');

    /// <summary>The line and column an offset into the whole text falls on.</summary>
    private (int Line, int Column) Place(int offset)
    {
        var lines = Lines();
        int at = Math.Clamp(offset, 0, (_inPlace?.Text ?? "").Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (at <= lines[i].Length) return (i, at);
            at -= lines[i].Length + 1;          // and the newline
        }
        return (lines.Length - 1, lines[^1].Length);
    }

    // ── The caret and the selection ─────────────────────────────────────────

    // Both are drawn in the field's own text space — x from the measured widths, y
    // in whole cells — and carry the field's transform, so they follow a condensed
    // cell and a quarter turn without knowing anything about either.
    private void RefreshCaret(bool force = false)
    {
        if (_inPlace is null || _inPlaceField is null) return;
        int start = _inPlace.SelectionStart, length = _inPlace.SelectionLength;
        if (!force && start == _shownStart && length == _shownLength) return;
        _shownStart = start;
        _shownLength = length;

        double cell = ZplRenderer.MeasureText(_inPlaceField).CellHeight;
        var lines = Lines();
        var accent = new SolidColorBrush(AccentColor());

        Remove(_inPlaceSelection);
        if (length > 0)
        {
            var (fromLine, fromCol) = Place(start);
            var (toLine, toCol) = Place(start + length);
            for (int i = fromLine; i <= toLine && i < lines.Length; i++)
            {
                var widths = Advances(lines[i]);
                int a = i == fromLine ? fromCol : 0;
                int b = i == toLine ? toCol : lines[i].Length;
                double x = widths[Math.Clamp(a, 0, lines[i].Length)];
                double w = widths[Math.Clamp(b, 0, lines[i].Length)] - x;
                // A line selected through to its end shows a little past the last
                // letter, the way the break it contains is selected too.
                if (i != toLine) w = Math.Max(w, 0) + cell * 0.3;
                if (w <= 0) continue;
                _inPlaceSelection.Add(Place(new Rectangle
                {
                    Width = w,
                    Height = cell,
                    Fill = accent,
                    IsHitTestVisible = false,
                }, x, i * cell, 1002));         // under the words
            }
        }

        // The caret sits at the moving end of the selection — where typing goes.
        int caretAt = length > 0 && _selectAnchor <= start ? start + length : start;
        var (line, column) = Place(caretAt);
        var lineWidths = Advances(line < lines.Length ? lines[line] : "");
        double caretX = lineWidths[Math.Clamp(column, 0, lineWidths.Length - 1)];

        if (_caret is not null && _caret.Parent is Canvas old) old.Children.Remove(_caret);
        _caret = Place(new Rectangle
        {
            Width = Math.Max(1, cell * 0.07),
            Height = cell,
            Fill = new SolidColorBrush(_inPlaceField.Reverse ? Colors.White : Colors.Black),
            IsHitTestVisible = false,
        }, caretX, line * cell, 1005);          // over the words
        _blinkTicks = 0;
    }

    /// <summary>Puts a shape at (x, y) in the field's text space.</summary>
    private T Place<T>(T shape, double x, double y, int z) where T : FrameworkElement
    {
        // Inside the transform, not through Canvas.Left or a margin: those place the
        // element BEFORE its render transform runs, so the offset would escape the
        // condensing and the quarter turns the field is drawn with — a caret in a
        // condensed field ended up a third of the way past the last letter.
        var field = _inPlaceField!;
        shape.RenderTransform = ZplRenderer.TextTransform(
            field, ZplRenderer.MeasureText(field), 0, new TranslateTransform { X = x, Y = y });
        Canvas.SetLeft(shape, 0);
        Canvas.SetTop(shape, 0);
        Canvas.SetZIndex(shape, z);
        PreviewCanvas.Children.Add(shape);
        return shape;
    }

    private void StartBlinking()
    {
        _blink ??= DispatcherQueue.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(130);
        _blink.IsRepeating = true;
        _blink.Tick -= Blink_Tick;
        _blink.Tick += Blink_Tick;
        _blinkTicks = 0;
        _blink.Start();
    }

    private void Blink_Tick(object? sender, object e)
    {
        if (_inPlace is null) return;
        // The same beat watches the selection: a TextBox announces nothing when the
        // caret moves, and the keys that move it are far too many to hook one by one.
        RefreshCaret();
        // Rebuilding the caret changes the tree under a pointer that may not have
        // moved, and the framework answers a change like that by going back to the
        // default cursor. Cleared and set again - setting the same value twice
        // changes nothing, and both land in the one frame, so nothing is seen.
        PreviewCursorHost.SetCursor(null!);
        UpdatePreviewCursor();
        if (++_blinkTicks % 4 != 0 || _caret is null) return;
        _caret.Opacity = _caret.Opacity > 0.5 ? 0 : 1;
    }

    // ── Pointer: placing the caret, dragging across the words ───────────────

    /// <summary>The character the pointer is over, or -1 when it is off the field.</summary>
    private int OffsetAt(Point canvasPoint)
    {
        if (_inPlace is null || _inPlaceField is null) return -1;
        double cell = ZplRenderer.MeasureText(_inPlaceField).CellHeight;
        Point p;
        try
        {
            var inverse = InkTransform(_inPlaceField).Inverse;
            if (inverse is null) return -1;
            p = inverse.TransformPoint(canvasPoint);
        }
        catch { return -1; }

        var lines = Lines();
        double slack = cell * 0.6;              // a little room around the words
        if (p.Y < -slack || p.Y > lines.Length * cell + slack) return -1;
        int line = Math.Clamp((int)Math.Floor(p.Y / cell), 0, lines.Length - 1);

        var widths = Advances(lines[line]);
        if (p.X < -slack || p.X > widths[^1] + slack) return -1;

        // The nearest gap between letters, not the letter itself: that is where a
        // caret goes.
        int column = 0;
        double best = double.MaxValue;
        for (int i = 0; i < widths.Length; i++)
        {
            double d = Math.Abs(widths[i] - p.X);
            if (d < best) { best = d; column = i; }
        }

        int offset = column;
        for (int i = 0; i < line; i++) offset += lines[i].Length + 1;
        return offset;
    }

    /// <summary>True when the press belongs to the caret. Opens a drag-selection.</summary>
    private bool InPlacePointerPressed(PointerRoutedEventArgs e)
    {
        if (_inPlace is null) return false;
        int at = OffsetAt(e.GetCurrentPoint(PreviewCanvas).Position);
        if (at < 0) return false;
        _selectAnchor = at;
        _selectingByPointer = true;
        _inPlace.Select(at, 0);
        PreviewScrollViewer.CapturePointer(e.Pointer);
        RefreshCaret(force: true);
        return true;
    }

    private void InPlacePointerMoved(PointerRoutedEventArgs e)
    {
        if (!_selectingByPointer || _inPlace is null) return;
        if (!e.GetCurrentPoint(PreviewScrollViewer).Properties.IsLeftButtonPressed)
        {
            _selectingByPointer = false;
            return;
        }
        int at = OffsetAt(e.GetCurrentPoint(PreviewCanvas).Position);
        if (at < 0) return;
        _inPlace.Select(Math.Min(_selectAnchor, at), Math.Abs(at - _selectAnchor));
        RefreshCaret(force: true);
    }

    private void InPlacePointerReleased() => _selectingByPointer = false;

    // ── Typing ──────────────────────────────────────────────────────────────

    private void InPlace_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_inPlace is null || _selStart < 0) return;
        DrawInk();
        RefreshCaret(force: true);

        // A line break inside a field is "\&" in ZPL, and that is what has to be
        // written; the caret shows it as the break it prints as.
        var value = _inPlace.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace("\r", "\n", StringComparison.Ordinal)
                                 .Replace("\n", "\\&", StringComparison.Ordinal);
        if (value == _inPlaceWritten) return;
        if (ZplPatcher.SetData(_currentText, _selStart, _selEnd, value) is not { } edit) return;
        _inPlaceWritten = value;
        ApplyEdit(edit);
    }

    private void InPlace_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                EndInPlace();
                break;
            case VirtualKey.Enter:
                // Shift+Enter breaks the line, like everywhere else. Enter on its own
                // is "done" — a label field is one line far more often than not.
                if (IsHeld(VirtualKey.Shift)) return;
                e.Handled = true;
                EndInPlace();
                break;
        }
    }

    // ── Getting out ─────────────────────────────────────────────────────────

    /// <summary>Closes the caret and puts the drawn field back.</summary>
    internal void EndInPlace()
    {
        if (_inPlace is null || _inPlaceClosing) return;
        _inPlaceClosing = true;

        _blink?.Stop();
        var box = _inPlace;
        _inPlace = null;
        _inPlaceField = null;
        _selectingByPointer = false;
        _shownStart = _shownLength = -1;
        _advances.Clear();
        box.TextChanged -= InPlace_TextChanged;
        box.PreviewKeyDown -= InPlace_KeyDown;
        if (box.Parent is Canvas parent) parent.Children.Remove(box);
        Remove(_inPlaceInk);
        Remove(_inPlaceSelection);
        if (_caret?.Parent is Canvas caretParent) caretParent.Children.Remove(_caret);
        _caret = null;

        PreviewScrollViewer.HorizontalScrollMode = _scrollHorizontal;
        PreviewScrollViewer.VerticalScrollMode = _scrollVertical;
        PreviewScrollViewer.BringIntoViewOnFocusChange = true;

        // The redraws were held back while the caret was in the words; catch up.
        RefreshPreview(SizeUpdate.TextEdited);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            UpdateInspectFrame();
            RefreshSelectionProperties();
        });
        PreviewCursorHost.Focus(FocusState.Programmatic);
        _inPlaceClosing = false;
    }
}
