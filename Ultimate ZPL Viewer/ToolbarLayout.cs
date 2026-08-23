using System.Collections.Generic;

namespace Ultimate_ZPL_Viewer;

// The toolbar layout is simply an ordered list of group ids; the toolbar wraps
// automatically. This static helper holds the known ids + list repair.
public static class ToolbarItems
{
    public static readonly string[] AllIds =
        { "file", "density", "size", "rotate", "zoom", "inspect", "download", "print" };

    // Removes unknown/duplicate ids and appends any missing ones (e.g. a group
    // added by an app update), preserving the user's order.
    public static List<string> Normalize(List<string>? order)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        if (order is not null)
            foreach (var id in order)
                if (System.Array.IndexOf(AllIds, id) >= 0 && seen.Add(id))
                    result.Add(id);
        foreach (var id in AllIds)
            if (seen.Add(id))
                result.Add(id);
        return result;
    }

    // Number of virtual rows the toolbar designer offers.
    public const int RowCount = 3;

    // Repairs the row layout: always exactly RowCount rows, every known id present
    // exactly once (unknown/duplicate ids dropped, missing ones appended to the
    // first row), preserving the user's arrangement.
    public static List<List<string>> NormalizeRows(List<List<string>>? rows)
    {
        var result = new List<List<string>>();
        var seen = new HashSet<string>();
        for (int i = 0; i < RowCount; i++)
        {
            var row = new List<string>();
            if (rows is not null && i < rows.Count && rows[i] is not null)
                foreach (var id in rows[i])
                    if (System.Array.IndexOf(AllIds, id) >= 0 && seen.Add(id))
                        row.Add(id);
            result.Add(row);
        }
        foreach (var id in AllIds)
            if (seen.Add(id))
                result[0].Add(id);
        return result;
    }
}
