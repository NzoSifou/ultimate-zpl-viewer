using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ultimate_ZPL_Viewer;

// The Ctrl+/ cheat sheet: every shortcut the application answers to, grouped by
// theme, with the keys drawn as key caps on the right of each line.
//
// This is the ONLY place the list is written down for the user, so it has to stay
// in step with PreviewPage.RegisterShortcuts and its twin in editor.html — a
// shortcut added there and not here simply does not exist as far as anyone can tell.
internal static class ShortcutsHelp
{
    // A single line: what it does, and one or more key combinations that do it
    // (two when a shortcut has a natural opposite, e.g. next / previous tab).
    private sealed record Row(string LabelKey, string[][] Combos);

    private sealed record Section(string TitleKey, Row[] Rows);

    private static string[] K(params string[] keys) => keys;
    private static Row R(string labelKey, params string[][] combos) => new(labelKey, combos);

    // Key caps that have a name in the user's language ("Maj", "Échap"…). Anything
    // else — letters, digits, F11, punctuation — is shown as written here.
    private static readonly string[] Named = { "ctrl", "shift", "alt", "esc", "tab" };

    private static readonly Section[] Catalogue =
    {
        new("tabs", new[]
        {
            R("newTab",       K("ctrl", "T")),
            R("newWindow",    K("ctrl", "N")),
            R("duplicateTab", K("ctrl", "D")),
            R("closeTab",     K("ctrl", "W")),
            R("closeWindow",  K("ctrl", "shift", "W")),
            R("reopenClosed", K("ctrl", "shift", "T")),
            R("stepTab",      K("ctrl", "tab"), K("ctrl", "shift", "tab")),
            R("goToTab",      K("ctrl", "1…9")),
            R("lastTab",      K("ctrl", "0")),
        }),
        new("file", new[]
        {
            R("open",      K("ctrl", "O")),
            R("save",      K("ctrl", "S")),
            R("saveAs",    K("ctrl", "shift", "S")),
            R("exportPdf", K("ctrl", "shift", "E")),
            R("exportPng", K("ctrl", "shift", "I")),
            R("print",     K("ctrl", "P")),
        }),
        new("view", new[]
        {
            R("settings",     K("ctrl", ",")),
            R("leaveSettings", K("esc")),
            R("fullScreen",   K("F11")),
            R("toolbar",      K("ctrl", "B")),
            R("editor",       K("ctrl", "E")),
            R("grid",         K("ctrl", "G")),
            R("lineNumbers",  K("ctrl", "L")),
            R("help",         K("ctrl", "/")),
        }),
        new("preview", new[]
        {
            R("zoom",    K("ctrl", "+"), K("ctrl", "−")),
            R("zoom100", K("ctrl", "shift", "1")),
            R("zoomFit", K("ctrl", "shift", "9")),
            R("rotate",  K("ctrl", "shift", "R")),
        }),
        new("editor", new[]
        {
            R("fontSize",  K("ctrl", "+"), K("ctrl", "−")),
            R("wordWrap",  K("alt", "Z")),
            R("minimap",   K("ctrl", "M")),
            R("find",      K("ctrl", "F")),
            R("replace",   K("ctrl", "H")),
            R("undoRedo",  K("ctrl", "Z"), K("ctrl", "Y")),
            R("gotoLine",  K("alt", "G")),
            R("selectLine", K("alt", "L")),
            R("nextMatch", K("alt", "D")),
        }),
    };

    private static string L(string key) => LocalizationService.Get("shortcuts." + key);

    /// <summary>
    /// Builds the cheat-sheet dialog. <paramref name="availableWidth"/> and
    /// <paramref name="availableHeight"/> are the window's, so the sheet drops to a
    /// single column and stops growing rather than being clipped on a small window.
    /// </summary>
    public static ContentDialog Create(XamlRoot root, ElementTheme theme,
                                       double availableWidth, double availableHeight)
    {
        bool twoColumns = availableWidth >= 900;
        double contentWidth = twoColumns ? 840 : Math.Max(360, Math.Min(520, availableWidth - 140));

        var body = twoColumns ? BuildTwoColumns() : BuildOneColumn();
        body.Width = contentWidth;

        var scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Clamp(availableHeight - 220, 280, 640),
            Padding = new Thickness(0, 0, 12, 0),   // room for the scrollbar
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = theme,
            Title = BuildHeader(),
            Content = scroller,
            CloseButtonText = L("close"),
            // No accent-coloured default button: nothing here is a decision to
            // confirm, it is a reference sheet. Escape and the button still close it.
            DefaultButton = ContentDialogButton.None,
        };
        // The default ContentDialog is far too narrow for two columns of key caps.
        dialog.Resources["ContentDialogMaxWidth"] = contentWidth + 120;
        return dialog;
    }

    // Title on the left, and nothing else: no paging arrows, no decoration.
    private static UIElement BuildHeader()
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = L("title"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = L("subtitle"),
            FontSize = 13,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static Panel BuildOneColumn()
    {
        var stack = new StackPanel { Spacing = 12 };
        foreach (var section in Catalogue) stack.Children.Add(BuildSection(section));
        return stack;
    }

    // Two columns, filled so that both end up about as tall as each other — the
    // sections have very different lengths, so alternating them would leave one
    // side half empty.
    private static Panel BuildTwoColumns()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Spacing = 12 };
        var right = new StackPanel { Spacing = 12 };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        // Each section lands in whichever column is currently the shorter one. The
        // sections differ a lot in length, so anything simpler (alternating, or
        // splitting the list in half) leaves one side hanging well below the other.
        int leftWeight = 0, rightWeight = 0;
        foreach (var section in Catalogue)
        {
            int weight = section.Rows.Length + 2;   // +2 ≈ the header and the padding
            if (leftWeight <= rightWeight) { left.Children.Add(BuildSection(section)); leftWeight += weight; }
            else                           { right.Children.Add(BuildSection(section)); rightWeight += weight; }
        }
        return grid;
    }

    private static Border BuildSection(Section section)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = L("section." + section.TitleKey),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 60,
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var row in section.Rows) panel.Children.Add(BuildRow(row));

        return new Border
        {
            Child = panel,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }

    // Description on the left, key caps pushed to the right edge.
    private static Grid BuildRow(Row row)
    {
        var grid = new Grid { MinHeight = 32, Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = L("row." + row.LabelKey),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        grid.Children.Add(label);

        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        for (int c = 0; c < row.Combos.Length; c++)
        {
            // The alternative separator has to read louder than the "+" inside a
            // combination, or "Ctrl+Tab / Ctrl+Maj+Tab" turns into one long run.
            if (c > 0) keys.Children.Add(Joiner("/", 14, 6));
            var combo = row.Combos[c];
            for (int k = 0; k < combo.Length; k++)
            {
                if (k > 0) keys.Children.Add(Joiner("+"));
                keys.Children.Add(Cap(combo[k]));
            }
        }
        Grid.SetColumn(keys, 1);
        grid.Children.Add(keys);
        return grid;
    }

    private static TextBlock Joiner(string glyph, double size = 12, double sideMargin = 0) => new()
    {
        Text = glyph,
        FontSize = size,
        Opacity = 0.45,
        Margin = new Thickness(sideMargin, 0, sideMargin, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Cap(string key) => new()
    {
        Child = new TextBlock
        {
            Text = Named.Contains(key) ? L("key." + key) : key,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        },
        MinWidth = 26,
        Padding = new Thickness(8, 4, 8, 5),
        CornerRadius = new CornerRadius(5),
        Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
        BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorSecondaryBrush"],
        VerticalAlignment = VerticalAlignment.Center,
    };
}
