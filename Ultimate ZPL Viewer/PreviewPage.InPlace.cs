using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using Windows.System;

namespace Ultimate_ZPL_Viewer;

// ── Typing on the label itself ──────────────────────────────────────────────
// A text field is edited where it is printed: double-click it (or press F2) and a
// caret appears inside the words, in the field's own font, at the field's own
// size, turned the way the field is turned. It is a real TextBox sitting on the
// canvas — but it is dressed and placed by the SAME geometry the renderer uses to
// draw the glyphs, so it lands on top of them rather than near them.
//
// While it has the caret the canvas is NOT redrawn. Every keystroke still rewrites
// the ^FD and travels the usual path — Monaco's undo stack, the code view, the
// analyser — but the picture on screen is the box itself, which is showing the
// same text in the same font. Redrawing would tear the box out of the tree on
// every letter (the canvas is rebuilt whole) and take the caret with it.
public sealed partial class PreviewPage
{
    private TextBox? _inPlace;
    private ZplText? _inPlaceField;      // the drawable it is standing in for
    private bool _inPlaceClosing;        // guards the exit path against itself
    // The last payload written into the ^FD. Compared against rather than re-read
    // from the field, because the field is NOT re-read while the caret is in the
    // box: the redraw that would refresh it is exactly what is being held back.
    private string _inPlaceWritten = "";

    /// <summary>True while a field is being typed into on the label.</summary>
    // The parent is checked as well: a redraw from somewhere that did not know about
    // the caret would have taken the box out of the canvas, and holding the redraws
    // back for a box that is no longer on screen would freeze the preview.
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

        var layout = ZplRenderer.MeasureText(field);
        _inPlaceWritten = _facts!.Data ?? "";
        var box = new TextBox
        {
            Text = _inPlaceWritten.Replace("\\&", "\n", StringComparison.Ordinal),
            FontFamily = new FontFamily(field.Font),
            FontSize = layout.FontSize,
            FontWeight = layout.Weight,
            Foreground = new SolidColorBrush(field.Reverse ? Colors.White : Colors.Black),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        StripChrome(box, field.Reverse);
        box.RenderTransform = ZplRenderer.TextTransform(field, layout, 0);
        Canvas.SetLeft(box, 0);
        Canvas.SetTop(box, 0);
        Canvas.SetZIndex(box, 1003);

        box.TextChanged += InPlace_TextChanged;
        // PreviewKeyDown, not KeyDown: a TextBox that accepts returns swallows Enter
        // to insert the line break, and an ordinary handler never sees it. This one
        // runs first and can take the key for itself.
        box.PreviewKeyDown += InPlace_KeyDown;
        box.LostFocus += (_, _) => EndInPlace();

        _inPlace = box;
        PreviewCanvas.Children.Add(box);

        // The glyphs underneath would show through the box and double every letter.
        HideDrawnField();
        // The frame, the handles and the bar all belong to an element being moved
        // around, not to one being typed into — and the bar sits right on top of it.
        if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
        ClearHandles();
        SelectionTools.Visibility = Visibility.Collapsed;

        box.Focus(FocusState.Programmatic);
        if (selectAll) box.SelectAll(); else box.Select(box.Text.Length, 0);
    }

    // A TextBox paints a filled rectangle and an accent underline the moment it
    // takes focus. On a label that reads as a defect, so every one of those brushes
    // is turned off locally — the caret and the words are the whole control here.
    private static void StripChrome(TextBox box, bool reverse)
    {
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver",
                     "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                     "TextControlButtonBackground", "TextControlButtonBackgroundPressed",
                 })
            box.Resources[key] = clear;

        var ink = new SolidColorBrush(reverse ? Colors.White : Colors.Black);
        foreach (var key in new[]
                 {
                     "TextControlForeground", "TextControlForegroundPointerOver",
                     "TextControlForegroundFocused",
                 })
            box.Resources[key] = ink;

        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.Resources["TextControlThemeMinHeight"] = 0d;
        box.Resources["TextControlThemeMinWidth"] = 0d;
    }

    private void HideDrawnField()
    {
        foreach (var (element, drawable) in _hitMap)
            if (drawable is ZplText && drawable.SourceStart == _selStart)
                element.Opacity = 0;
    }

    // ── Typing ──────────────────────────────────────────────────────────────

    private void InPlace_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_inPlace is null || _selStart < 0) return;
        // A line break inside a field is "\&" in ZPL, and that is what has to be
        // written; the box shows it as the break it prints as.
        var value = _inPlace.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace("\r", "\n", StringComparison.Ordinal)
                                 .Replace("\n", "\\&", StringComparison.Ordinal);
        if (value == _inPlaceWritten) return;
        if (ZplPatcher.SetData(_currentText, _selStart, _selEnd, value) is not { } edit) return;
        _inPlaceWritten = value;
        ApplyEdit(edit);

        // A rotated ^FO field is anchored by the top-left of its ROTATED bounding
        // box, so where it sits depends on how wide the text is. Re-place the box on
        // every change, or it would slide the moment the caret left.
        if (_inPlaceField is { Rotation: not 0, Baseline: false } turned)
        {
            var grown = turned with { Text = _inPlace.Text };
            _inPlace.RenderTransform =
                ZplRenderer.TextTransform(grown, ZplRenderer.MeasureText(grown), 0);
        }
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

        var box = _inPlace;
        _inPlace = null;
        _inPlaceField = null;
        box.TextChanged -= InPlace_TextChanged;
        box.PreviewKeyDown -= InPlace_KeyDown;
        if (box.Parent is Canvas parent) parent.Children.Remove(box);

        // The redraws were held back while the caret was in the box; catch up.
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
