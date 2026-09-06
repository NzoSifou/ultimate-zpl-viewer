using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace Ultimate_ZPL_Viewer;

// ── Copy, cut, paste on the label ───────────────────────────────────────────
// What travels is the ZPL itself: the selected fields, verbatim, as text. So a
// copy can be pasted into the code view, into another label, into an e-mail —
// and anything a person copied from any of those can be pasted onto the label.
//
// A paste lands two millimetres down and across from where it was written, the
// way a duplicate does: dropped exactly on top of the original it would look
// like nothing had happened.
public sealed partial class PreviewPage
{
    private void CopySelection(bool cut)
    {
        if (!_editMode || _selStart < 0) return;
        var spans = SelectedSpans();
        if (spans.Count == 0) return;

        var text = string.Join("\n", spans.Select(s => _currentText[s.Start..s.End].Trim()));
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        try { Clipboard.SetContent(package); } catch { return; }

        if (cut) DeleteSelection();
    }

    private async Task PasteAsync()
    {
        if (!_editMode) return;
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            text = await content.GetTextAsync();
        }
        catch { return; }

        // Only ZPL: pasting a paragraph of prose onto a label would produce a
        // document that no longer parses, with no way to see what went wrong.
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length == 0 || !text.Contains('^')) return;

        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        double step = Math.Round(2 * dpmm);
        var snippet = ZplPatcher.Nudge(text, step, step, dpmm);

        int at = EndOfLabelOffset();
        string prefix = at > 0 && _currentText[at - 1] == '\n' ? "" : "\n";
        ApplyEdit(new ZplPatcher.Edit(at, at, prefix + snippet + "\n"));

        // Everything that came out of the paste, held together — it is one thing as
        // far as the person who pasted it is concerned. The fields are only known
        // once the label has been drawn again.
        int from = at + prefix.Length, to = from + snippet.Length;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var landed = FieldBoxes()
                .Select(f => f.Span)
                .Where(s => s.Start >= from && s.Start < to)
                .OrderBy(s => s.Start)
                .ToList();
            if (landed.Count == 0) return;

            _selected.Clear();
            foreach (var span in landed) _selected.Add(span.Start);
            _selStart = landed[^1].Start;
            _selEnd = landed[^1].End;
            UpdateInspectFrame();
            HighlightPrimary();
            PreviewCursorHost.Focus(FocusState.Programmatic);
        });
    }
}
