using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;

namespace Ultimate_ZPL_Viewer;

// ── Typing on the label itself ──────────────────────────────────────────────
// A text field is edited where it is printed: double-click it (or press F2) and a
// caret appears inside the words, which go on looking exactly as they will print.
//
// They look that way because they ARE the printed words. What is under the caret
// is drawn by the renderer, through the same helper the label is drawn with, and
// rebuilt on every keystroke; the TextBox on top of it carries the caret and the
// selection and nothing else — its own text is transparent. Letting the box draw
// the words instead would mean two typesetters agreeing on a font, a weight, an
// ink pass and a line height, and they do not.
//
// While it has the caret the canvas is NOT redrawn. Every keystroke still
// rewrites the ^FD and travels the usual path — Monaco's undo stack, the code
// view, the analyser — but the picture on screen is these blocks. A full redraw
// rebuilds the canvas whole and would tear the box out of the tree on every
// letter, taking the caret with it.
public sealed partial class PreviewPage
{
    private TextBox? _inPlace;
    private ZplText? _inPlaceField;      // the drawable it is standing in for
    private bool _inPlaceClosing;        // guards the exit path against itself
    private readonly List<TextBlock> _inPlaceInk = new();
    // The last payload written into the ^FD. Compared against rather than re-read
    // from the field, because the field is NOT re-read while the caret is in the
    // box: the redraw that would refresh it is exactly what is being held back.
    private string _inPlaceWritten = "";

    /// <summary>True while a field is being typed into on the label.</summary>
    // The parent is checked too: a redraw from somewhere that did not know about the
    // caret would have taken the box out of the canvas, and holding redraws back for
    // a box that is no longer on screen would freeze the preview.
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

        var layout = ZplRenderer.MeasureText(field);
        var box = new TextBox
        {
            Text = _inPlaceWritten.Replace("\\&", "\n", StringComparison.Ordinal),
            // The metrics still have to match the drawn words, or the caret would
            // stand where the letters are not.
            FontFamily = new FontFamily(field.Font),
            FontSize = layout.FontSize,
            FontWeight = layout.Weight,
            // Invisible: the words come from the renderer, on top of this.
            Foreground = new SolidColorBrush(Colors.Transparent),
            Template = BareTemplate(),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        box.RenderTransform = ZplRenderer.TextTransform(field, layout, 0);
        Canvas.SetLeft(box, 0);
        Canvas.SetTop(box, 0);
        Canvas.SetZIndex(box, 1003);

        box.TextChanged += InPlace_TextChanged;
        // PreviewKeyDown, not KeyDown: a TextBox that accepts returns swallows Enter
        // to insert the line break, and an ordinary handler never sees it. This one
        // runs first and can take the key for itself.
        box.PreviewKeyDown += InPlace_KeyDown;
        box.LostFocus += (_, _) =>
        {
            // The ScrollViewer under the label claims the focus on any press inside
            // it — including the press that has just placed the caret in these very
            // words. That is not the user leaving, and taking the focus back is what
            // lets a click move the caret instead of ending the edit. A press that
            // really does leave has already closed the box before this runs.
            if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), PreviewScrollViewer))
            {
                box.Focus(FocusState.Programmatic);
                return;
            }
            EndInPlace();
        };

        _inPlace = box;
        PreviewCanvas.Children.Add(box);

        // The field as the renderer draws it, over the box; the originals underneath
        // would double every letter.
        HideDrawnField();
        DrawInk();

        // The frame, the handles and the bar all belong to an element being moved
        // around, not to one being typed into — and the bar sits right on top of it.
        if (_inspectFrame is not null) _inspectFrame.Visibility = Visibility.Collapsed;
        ClearHandles();
        SelectionTools.Visibility = Visibility.Collapsed;

        box.Focus(FocusState.Programmatic);
        if (selectAll) box.SelectAll(); else box.Select(box.Text.Length, 0);
    }

    // A TextBox paints a filled rectangle, a border and an accent underline the
    // moment it takes focus, and its visual states force the text colour back to the
    // theme's — which is how a "transparent" foreground comes out black. Turning
    // those off resource by resource does not work; giving it a template with
    // nothing in it but the text does. The caret belongs to the editing layer
    // inside and survives.
    private static ControlTemplate? _bareTemplate;

    private static ControlTemplate BareTemplate() => _bareTemplate ??=
        (ControlTemplate)XamlReader.Load(
            "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
            " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"TextBox\">" +
            // The background has to be painted, transparent though it is: with
            // nothing drawing it the box has no surface, the press goes straight
            // through to the canvas underneath, and a click meant to move the
            // caret finishes the typing instead.
            "<Grid Background=\"#01FFFFFF\">" +
            "<ScrollViewer x:Name=\"ContentElement\" IsTabStop=\"False\" ZoomMode=\"Disabled\"" +
            " HorizontalScrollMode=\"Disabled\" VerticalScrollMode=\"Disabled\"" +
            " HorizontalScrollBarVisibility=\"Hidden\" VerticalScrollBarVisibility=\"Hidden\"" +
            " Padding=\"0\" Margin=\"0\" AutomationProperties.AccessibilityView=\"Raw\"/>" +
            "</Grid></ControlTemplate>");

    private void HideDrawnField()
    {
        foreach (var (element, drawable) in _hitMap)
            if (drawable is ZplText && drawable.SourceStart == _selStart)
                element.Opacity = 0;
    }

    /// <summary>
    /// Draws the field as it stands, through the renderer's own helper. Called again
    /// on every keystroke: it is the picture of the label while the redraws are held
    /// back, and for a rotated ^FO field it is also what keeps the words anchored —
    /// that anchor is the corner of the ROTATED bounding box, which moves as the
    /// text grows.
    /// </summary>
    private void DrawInk()
    {
        foreach (var block in _inPlaceInk)
            if (block.Parent is Canvas parent) parent.Children.Remove(block);
        _inPlaceInk.Clear();
        if (_inPlace is null || _inPlaceField is null) return;

        var shown = _inPlaceField with { Text = _inPlace.Text };
        foreach (var block in ZplRenderer.TextBlocksFor(shown))
        {
            block.IsHitTestVisible = false;
            // Above the box, so the selection highlight it paints sits BEHIND the
            // letters, the way a selection is supposed to look.
            Canvas.SetZIndex(block, 1004);
            PreviewCanvas.Children.Add(block);
            _inPlaceInk.Add(block);
        }
    }

    // ── Typing ──────────────────────────────────────────────────────────────

    private void InPlace_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_inPlace is null || _selStart < 0) return;
        DrawInk();

        // A line break inside a field is "\&" in ZPL, and that is what has to be
        // written; the box shows it as the break it prints as.
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

    /// <summary>True when a press landed inside the words being typed.</summary>
    private bool PressIsInsideInPlace(PointerRoutedEventArgs e)
    {
        if (_inPlace is null) return false;
        // A click in the middle of the words moves the caret; it does not finish.
        // The press arrives from somewhere inside the box's own template, so the
        // first question is whether the box is on the way up.
        for (var node = e.OriginalSource as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, _inPlace)) return true;

        // And the second is geometric, because the box is transparent and a press on
        // a part of it that paints nothing is reported against whatever is behind.
        try
        {
            var p = e.GetCurrentPoint(_inPlace).Position;
            return p.X >= -2 && p.Y >= -2
                && p.X <= _inPlace.ActualWidth + 2 && p.Y <= _inPlace.ActualHeight + 2;
        }
        catch { return false; }
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
        foreach (var block in _inPlaceInk)
            if (block.Parent is Canvas inkParent) inkParent.Children.Remove(block);
        _inPlaceInk.Clear();

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
