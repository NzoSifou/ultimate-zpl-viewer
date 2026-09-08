using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;

namespace Ultimate_ZPL_Viewer;

// ── The command line, written out ───────────────────────────────────────────
// The application answers to a handful of switches, and until now the only way
// to learn that was to run it with --help from a terminal - which nobody does
// with an application they open by double-clicking. The same list lives here, in
// the settings, laid out to be read rather than parsed.
//
// It is the SAME list as CliRunner.HelpText and LaunchOptions.Parse. A switch
// added there and not here simply does not exist as far as anyone can tell.
public sealed partial class PreviewPage
{
    private UIElement BuildCommandLineSettings()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("commandLine"));

        panel.Children.Add(CliGroup("open", new[]
        {
            ("<fichier.zpl>", "file"),
            ("--hide <zones>", "hide"),
        }));

        panel.Children.Add(CliGroup("convert", new[]
        {
            ("--pdf <sortie.pdf>", "pdf"),
            ("--png <sortie.png>", "png"),
            ("--dpmm <n>", "dpmm"),
            ("--rotate <degres>", "rotate"),
            ("--margin <n>", "margin"),
            ("--unit <mm|cm|in>", "unit"),
            ("-o, --output", "output"),
        }));

        panel.Children.Add(CliGroup("other", new[]
        {
            ("-h, --help", "help"),
        }));

        panel.Children.Add(SubHeader(SL("commandLine.sec.examples")));
        panel.Children.Add(ExampleCard(new[]
        {
            ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl", "open"),
            ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl --hide editor", "hide"),
            ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl -o --pdf sortie.pdf", "pdf"),
            ("\"Ultimate ZPL Viewer.exe\" etiquette.zpl -o --png sortie.png --dpmm 12", "png"),
        }));

        return panel;
    }

    // One card per family of switches: the flag on the left in the font a
    // terminal uses, what it does on the right, one line each.
    private Border CliGroup(string section, (string Flag, string Key)[] options)
    {
        var rows = new StackPanel { Spacing = 0 };
        for (int i = 0; i < options.Length; i++)
        {
            if (i > 0)
                rows.Children.Add(new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(0, 10, 0, 10),
                    Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                });
            rows.Children.Add(CliRow(options[i].Flag, SL("commandLine.opt." + options[i].Key)));
        }

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = SL("commandLine.sec." + section),
            FontWeight = FontWeights.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = SL("commandLine.desc." + section),
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -6, 0, 0),
        });
        body.Children.Add(rows);
        return Card(body);
    }

    private static Grid CliRow(string flag, string what)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chip = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorSecondaryBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 3, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = flag,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Grid.SetColumn(chip, 0);
        row.Children.Add(chip);

        var text = new TextBlock
        {
            Text = what,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    // The commands, copyable: a reference sheet whose lines have to be retyped by
    // hand is a reference sheet nobody uses twice.
    private Border ExampleCard((string Command, string Key)[] examples)
    {
        var rows = new StackPanel { Spacing = 8 };
        foreach (var (command, key) in examples)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new Border
            {
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorSecondaryBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6, 10, 7),
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = SL("commandLine.example." + key),
                            FontSize = 12,
                            Opacity = 0.7,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = command,
                            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                            FontSize = 13,
                            IsTextSelectionEnabled = true,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            };
            Grid.SetColumn(box, 0);
            line.Children.Add(box);

            var copy = new Button
            {
                Width = 34, Height = 34, MinWidth = 0, Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = "", FontSize = 14 },
            };
            ToolTipService.SetToolTip(copy, TipBlock(SL("commandLine.lbl.copy")));
            var captured = command;
            copy.Click += (_, _) => CopyTextToClipboard(captured);
            Grid.SetColumn(copy, 1);
            line.Children.Add(copy);

            rows.Children.Add(line);
        }
        return Card(rows);
    }

    // The same surface MakeCard draws, without its icon / title / control row:
    // what goes in here is a table, not a setting.
    private static Border Card(FrameworkElement content) => new()
    {
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 14, 16, 14),
        Margin = new Thickness(0, 3, 0, 8),
        MaxWidth = 820,
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = content,
    };
}
