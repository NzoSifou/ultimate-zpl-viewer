using System;
using System.Collections.Generic;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// One slot in a toolbar row: a single button on its own, or a named group of
/// buttons that carries its name underneath them.
/// </summary>
public sealed class ToolbarSlot
{
    /// <summary>Null for a button standing alone; a name (possibly blank) for a group.</summary>
    public string? Group { get; set; }

    /// <summary>The button ids this slot holds, in order. Exactly one when loose.</summary>
    public List<string> Items { get; set; } = new();

    public bool IsGroup => Group is not null;

    public static ToolbarSlot Loose(string id) => new() { Items = { id } };

    public static ToolbarSlot Named(string name, params string[] ids)
        => new() { Group = name, Items = ids.ToList() };
}

// The toolbar layout: three rows, each an ordered list of slots.
//
// A group is optional scaffolding, not a container everything must live in: a
// button sits loose unless somebody puts it in one. That is the whole point of
// the shape - a row is a list of slots, and a slot is either one button or
// several under a name.
public static class ToolbarItems
{
    // Every button the toolbar can show, in the order a fresh layout falls back
    // to. Splitting the file and export buttons apart is what lets them be
    // grouped differently from how they were shipped.
    public static readonly string[] AllIds =
    {
        "newFile", "openFile", "save",
        "density", "size", "zoom", "rotate",
        "transform", "pdf", "png", "print",
    };

    /// <summary>Number of rows the designer offers.</summary>
    public const int RowCount = 3;

    /// <summary>
    /// The layout a fresh install starts on, and the one "Réinitialiser" returns
    /// to: three groups on one row, named for what they do rather than for the
    /// buttons they happen to contain.
    /// </summary>
    public static List<List<ToolbarSlot>> Default()
    {
        string Name(string key) => LocalizationService.Get("settings.toolbar.groups." + key);
        var rows = new List<List<ToolbarSlot>>
        {
            new()
            {
                ToolbarSlot.Named(Name("file"), "newFile", "openFile", "save"),
                ToolbarSlot.Named(Name("view"), "density", "size", "zoom", "rotate"),
                ToolbarSlot.Named(Name("output"), "transform", "pdf", "png", "print"),
            },
        };
        while (rows.Count < RowCount) rows.Add(new List<ToolbarSlot>());
        return rows;
    }

    /// <summary>
    /// Repairs a layout read back from the settings: always RowCount rows, every
    /// known button present exactly once, unknown ones dropped. A group with no
    /// buttons left in it is kept — an empty group is a legitimate thing to be
    /// half-way through making, and it simply does not show on the toolbar.
    /// </summary>
    public static List<List<ToolbarSlot>> Normalize(List<List<ToolbarSlot>>? rows)
    {
        if (rows is null) return Default();

        var result = new List<List<ToolbarSlot>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < RowCount; i++)
        {
            var row = new List<ToolbarSlot>();
            if (i < rows.Count && rows[i] is not null)
                foreach (var slot in rows[i])
                {
                    if (slot is null) continue;
                    var items = new List<string>();
                    foreach (var id in slot.Items ?? new List<string>())
                        if (Array.IndexOf(AllIds, id) >= 0 && seen.Add(id)) items.Add(id);

                    if (slot.IsGroup) row.Add(new ToolbarSlot { Group = slot.Group, Items = items });
                    // A loose slot holds one button; anything else written into one
                    // is split back out rather than silently dropped.
                    else foreach (var id in items) row.Add(ToolbarSlot.Loose(id));
                }
            result.Add(row);
        }

        // A button the layout never mentioned — one a new version added — goes
        // loose at the end of the first row rather than disappearing.
        foreach (var id in AllIds)
            if (seen.Add(id)) result[0].Add(ToolbarSlot.Loose(id));

        return result;
    }
}
