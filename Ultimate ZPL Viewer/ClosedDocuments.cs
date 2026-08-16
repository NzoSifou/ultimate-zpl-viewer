using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

/// <summary>A document as it was when it disappeared, ready to be brought back.</summary>
internal sealed record ClosedDoc(string? FilePath, string Text, bool Dirty);

/// <summary>
/// One undo step for Ctrl+Shift+T. A closed TAB comes back where the user is; a
/// closed WINDOW comes back as a window with all the documents it held, which is
/// what a browser does.
/// </summary>
internal sealed record ClosedEntry(IReadOnlyList<ClosedDoc> Docs, bool WasWindow);

/// <summary>
/// The recently closed tabs and windows, most recent first, shared by every window
/// of the app — closing a window and pressing Ctrl+Shift+T in another one brings it
/// back.
/// </summary>
internal static class ClosedDocuments
{
    private const int MaxEntries = 25;
    private static readonly List<ClosedEntry> _stack = new();

    public static bool Any => _stack.Count > 0;

    public static void PushTab(ClosedDoc doc) => Push(new ClosedEntry(new[] { doc }, WasWindow: false));

    public static void PushWindow(IReadOnlyList<ClosedDoc> docs)
    {
        if (docs.Count > 0) Push(new ClosedEntry(docs.ToList(), WasWindow: true));
    }

    private static void Push(ClosedEntry entry)
    {
        _stack.Insert(0, entry);
        if (_stack.Count > MaxEntries) _stack.RemoveRange(MaxEntries, _stack.Count - MaxEntries);
    }

    public static ClosedEntry? Pop()
    {
        if (_stack.Count == 0) return null;
        var entry = _stack[0];
        _stack.RemoveAt(0);
        return entry;
    }
}
