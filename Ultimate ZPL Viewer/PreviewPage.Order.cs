using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// ── Which element is on top ─────────────────────────────────────────────────
// ZPL has no z-index: a field is drawn over the ones written before it. Bringing
// an element forward therefore means moving its text AFTER the next field's, and
// that is exactly what happens here — the two blocks trade places and everything
// between them stays where it is, comments included.
//
// The order of the fields is read from what was actually DRAWN, not from a second
// scan of the text: a ^FX comment or a stray command is not a field, and only the
// renderer knows the difference.
public sealed partial class PreviewPage
{
    private void InitElementOrder()
    {
        ForwardElementButton.Click += (_, _) => Reorder(forward: true);
        BackwardElementButton.Click += (_, _) => Reorder(forward: false);
    }

    /// <summary>Every drawn field's span, in the order the printer reads them.</summary>
    private List<(int Start, int End)> FieldOrder()
        => _hitMap.Values
            .Where(d => d.SourceStart >= 0 && d.SourceEnd > d.SourceStart)
            .Select(d => (d.SourceStart, d.SourceEnd))
            .Distinct()
            .OrderBy(span => span.Item1)
            .ToList();

    private int SelectedFieldIndex(List<(int Start, int End)> order)
        => order.FindIndex(span => span.Start == _selStart);

    /// <summary>Shows each arrow only when there is something to step past.</summary>
    private void UpdateOrderButtons()
    {
        var order = FieldOrder();
        // Stepping past a neighbour is a question about one element.
        int index = _selStart < 0 || HasMultiSelection ? -1 : SelectedFieldIndex(order);
        bool canForward = index >= 0 && index < order.Count - 1;
        bool canBackward = index > 0;

        ForwardElementButton.Visibility = canForward ? Visibility.Visible : Visibility.Collapsed;
        BackwardElementButton.Visibility = canBackward ? Visibility.Visible : Visibility.Collapsed;

        var ink = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        foreach (var shape in new Shape[]
                 { ForwardIconBack, ForwardIconFront, BackwardIconBack, BackwardIconFront })
        {
            if (shape.StrokeThickness > 0) shape.Stroke = ink; else shape.Fill = ink;
        }

        ToolTipService.SetToolTip(ForwardElementButton,
            TipBlock(LocalizationService.Get("mode.act.forward")));
        ToolTipService.SetToolTip(BackwardElementButton,
            TipBlock(LocalizationService.Get("mode.act.backward")));
    }

    /// <summary>
    /// Swaps the selected field with its neighbour. What sits BETWEEN the two —
    /// blank lines, a ^FX comment — is left in the middle rather than dragged
    /// along: it belongs to that spot in the file, not to either field.
    /// </summary>
    private void Reorder(bool forward)
    {
        if (_selStart < 0) return;
        var order = FieldOrder();
        int index = SelectedFieldIndex(order);
        if (index < 0) return;

        int other = forward ? index + 1 : index - 1;
        if (other < 0 || other >= order.Count) return;

        var first = order[Math.Min(index, other)];
        var second = order[Math.Max(index, other)];
        if (first.End > second.Start) return;                 // overlapping: leave it alone

        string a = _currentText[first.Start..first.End];
        string between = _currentText[first.End..second.Start];
        string b = _currentText[second.Start..second.End];

        ApplyEdit(new ZplPatcher.Edit(first.Start, second.End, b + between + a));

        // Follow the element that moved — through SelectSpan, so the highlight in
        // the EDITOR follows too. Assigning the two offsets on their own moves the
        // frame on the label and leaves the code pointing at the field that was
        // stepped over.
        int start = forward ? first.Start + b.Length + between.Length : first.Start;
        SelectSpan(start, start + (forward ? a.Length : b.Length), revealInEditor: false, moveCaret: true);
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }
}
