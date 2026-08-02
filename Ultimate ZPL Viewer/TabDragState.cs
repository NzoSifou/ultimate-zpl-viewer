using Microsoft.UI.Xaml.Controls;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// The tab currently being dragged. Every window lives in the same process, so the
/// document travels through this hand-off rather than through the clipboard-style
/// data package — the package only carries a marker so the drop targets can tell
/// one of our tabs from anything else being dragged around the desktop.
/// </summary>
internal static class TabDragState
{
    public const string Key = "UltimateZplViewer.Tab";

    public static PreviewPage? SourcePage { get; private set; }
    public static TabViewItem? Item { get; private set; }
    public static DocTab? Tab { get; private set; }

    public static void Begin(PreviewPage page, TabViewItem item, DocTab tab)
    {
        SourcePage = page;
        Item = item;
        Tab = tab;
    }

    public static void Clear()
    {
        SourcePage = null;
        Item = null;
        Tab = null;
    }
}
