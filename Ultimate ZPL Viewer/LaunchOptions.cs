using System;
using System.Collections.Generic;

namespace Ultimate_ZPL_Viewer;

// What a window is asked to open, and how. Built from the command line (see
// CommandLine) or by the application itself when it opens a window of its own.
//
// The Show*/EditMode overrides are FORCED values: while a window carries one, the
// matching preference is not written back when the user changes it in that window.
// The command line shapes one session; it does not rewrite the settings.
public sealed record LaunchOptions
{
    /// <summary>The documents to open, first one active.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>The first document, when there is one.</summary>
    public string? FilePath => Files.Count > 0 ? Files[0] : null;

    // null = the saved preference.
    public bool? ShowToolbar { get; init; }
    public bool? ShowEditor { get; init; }
    public bool? EditMode { get; init; }

    /// <summary>Open a window of its own even when the application is already running.</summary>
    public bool NewWindow { get; init; }

    /// <summary>How the documents above are read, and whether they are rewritten.</summary>
    public DocumentOptions Document { get; init; } = DocumentOptions.None;

    /// <summary>A document handed over by another window (a tab dragged out, …).</summary>
    public DocTab? Adopt { get; init; }

    /// <summary>False for every window but the first, so only one reopens the last session.</summary>
    public bool RestoreSession { get; init; } = true;

    public bool ForcedLayout => ShowToolbar is not null || ShowEditor is not null;

    /// <summary>A window the application opens for one document, or for none.</summary>
    public static LaunchOptions ForFile(string? path) => new()
    {
        Files = path is null ? Array.Empty<string>() : new[] { path },
        RestoreSession = false,
    };
}
