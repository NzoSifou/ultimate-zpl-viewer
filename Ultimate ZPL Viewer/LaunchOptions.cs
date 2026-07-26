using System;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// Parsed GUI launch arguments. FilePath opens a document; HideEditor/HideToolbar
// force those panes hidden at startup (`--hide editor,toolbar`). Forced is true
// whenever a --hide flag was given: while set, the editor/toolbar visibility is
// NOT persisted on exit (the override is a one-off, not a saved preference).
public sealed record LaunchOptions(string? FilePath, bool HideEditor, bool HideToolbar, bool Forced)
{
    // args come from Environment.GetCommandLineArgs() (args[0] = executable path).
    public static LaunchOptions Parse(string[] args)
    {
        string? file = null;
        bool hideEditor = false, hideToolbar = false, forced = false;

        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--hide", StringComparison.OrdinalIgnoreCase))
            {
                forced = true;
                if (i + 1 < args.Length)
                {
                    foreach (var part in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = part.Trim().ToLowerInvariant();
                        if (p == "editor") hideEditor = true;
                        else if (p == "toolbar") hideToolbar = true;
                    }
                }
            }
            else if (!a.StartsWith('-') && file is null)
            {
                file = a;
            }
        }

        return new LaunchOptions(file, hideEditor, hideToolbar, forced);
    }
}
