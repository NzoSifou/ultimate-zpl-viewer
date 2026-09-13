using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Ultimate_ZPL_Viewer;

// "Transformer": the two changes that touch every line of a document at once.
//
// The window is two questions and one answer. Each question is a row of cards —
// the whole set on screen, the chosen one wearing the accent — and the answer is
// a single block underneath saying what will come out. Anything that needs more
// words than a line is a tooltip: the window is read every time, the explanation
// once.
//
// The rounding choice lives in that answer block rather than among the questions,
// because it only ever changes one line of it, and a setting half a window away
// from what it governs is a setting nobody connects to anything.
public sealed partial class PreviewPage
{
    private static string TL(string key) => LocalizationService.Get("transform." + key);

    // The four densities a Zebra prints at, the same list the toolbar offers.
    private static readonly double[] TransformDensities = { 6, 8, 12, 24 };

    private async void TransformButton_Click(object sender, RoutedEventArgs e)
        => await ShowTransformDialogAsync();

    private async Task ShowTransformDialogAsync()
    {
        if (XamlRoot is null) return;
        if (string.IsNullOrWhiteSpace(_currentText))
        {
            await ShowMessageAsync(TL("title"), TL("msg.empty"));
            return;
        }

        // A document that declares its own density (^JM) has already answered the
        // question; otherwise it is written for whatever the toolbar says.
        double current = _model.DeclaredDpmm ?? SelectedDpmm;
        double? target = null;          // null = the density it already has
        int rotation = 0;
        bool roundUp = _settings.TransformRoundUp;

        var body = new StackPanel { Spacing = 4, MinWidth = 608 };

        // ── Density ─────────────────────────────────────────────────────────
        // No "leave it alone" card: the density it already has IS one of the four,
        // so that one carries the badge and starts chosen. Four cards here, four
        // across the row below.
        var densities = TransformDensities.ToList();
        if (!densities.Any(d => Math.Abs(d - current) < 1e-9)) { densities.Add(current); densities.Sort(); }

        body.Children.Add(SectionHead(TL("density.title"), TL("density.info")));
        body.Children.Add(SectionLine(TL("density.desc")));

        var densityCards = new List<ToggleButton>();
        foreach (var d in densities)
        {
            bool keep = Math.Abs(d - current) < 1e-9;
            var name = Dpmm(d) + " dpmm";
            densityCards.Add(PickCard(
                keep ? string.Format(TL("density.keep"), name) : name,
                new DpmmOption(d).Dpi + " dpi",
                keep ? TL("density.tag") : null));
        }
        body.Children.Add(CardRow(densityCards));

        // ── Rotation ────────────────────────────────────────────────────────
        body.Children.Add(SectionHead(TL("rotate.title"), TL("rotate.info")));
        body.Children.Add(SectionLine(TL("rotate.desc")));

        var rotationCards = new List<ToggleButton> { PickCard(TL("rotate.none"), null) };
        foreach (var angle in new[] { 90, 180, 270 })
            rotationCards.Add(PickCard(string.Format(TL("rotate.deg"), angle), null));
        body.Children.Add(CardRow(rotationCards));

        // ── What will come out ──────────────────────────────────────────────
        body.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            Margin = new Thickness(0, 18, 0, 14),
        });
        var outcome = new StackPanel { Spacing = 3 };
        body.Children.Add(outcome);

        // The rounding choice, built once and shown inside the outcome only when
        // something actually lands between two dots.
        var upChoice = new RadioButton { GroupName = "TransformHalf", Content = TL("half.up"), MinWidth = 0 };
        var downChoice = new RadioButton { GroupName = "TransformHalf", Content = TL("half.down"), MinWidth = 0 };
        var halfRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Margin = new Thickness(0, 6, 0, 0),
        };
        halfRow.Children.Add(new TextBlock
        {
            Text = TL("half.label"), FontSize = 12, Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        halfRow.Children.Add(upChoice);
        halfRow.Children.Add(downChoice);
        halfRow.Children.Add(MakeInfoIcon(TL("half.info")));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title = TL("title"),
            Content = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(320, XamlRoot.Size.Height - 260),
            },
            PrimaryButtonText = TL("apply"),
            CloseButtonText = TL("cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        void Refresh()
        {
            double shown = target ?? current;
            for (int i = 0; i < densityCards.Count; i++)
                Dress(densityCards[i], Math.Abs(densities[i] - shown) < 1e-9);
            for (int i = 0; i < rotationCards.Count; i++)
                Dress(rotationCards[i], rotation == i * 90);

            upChoice.IsChecked = roundUp;
            downChoice.IsChecked = !roundUp;

            bool acts = target is not null || rotation != 0;
            dialog.IsPrimaryButtonEnabled = acts;

            outcome.Children.Clear();
            if (!acts)
            {
                outcome.Children.Add(SectionLine(TL("msg.nothing")));
                return;
            }

            double ratio = target is { } wanted && current > 0 ? wanted / current : 1;
            double dpmm = target ?? current;
            var (w, h) = ZplTransform.SizeAfter(_model, ratio, rotation);
            outcome.Children.Add(new TextBlock
            {
                Text = string.Format(TL("result"), Mm(w / dpmm), Mm(h / dpmm), new DpmmOption(dpmm).Label),
                TextWrapping = TextWrapping.Wrap,
            });

            // Everything the engine could not convert exactly, named with its line.
            // A document it understood whole says nothing, which is the answer
            // people are hoping for.
            var notes = ZplTransform.Build(_currentText, current, target, rotation, roundUp).Notes;
            if (notes.Count > 0)
            {
                outcome.Children.Add(SectionHead(TL("notes.title"), TL("notes.info"), small: true));
                foreach (var note in notes)
                    outcome.Children.Add(SectionLine(
                        string.Format(TL("notes." + note.Kind), note.Command, note.Line)));
            }
            // Right under the line it governs.
            if (notes.Any(n => n.Kind == "module")) outcome.Children.Add(halfRow);
        }

        for (int i = 0; i < densityCards.Count; i++)
        {
            double picked = densities[i];
            densityCards[i].Click += (_, _) =>
            {
                target = Math.Abs(picked - current) < 1e-9 ? null : picked;
                Refresh();
            };
        }
        for (int i = 0; i < rotationCards.Count; i++)
        {
            int index = i;
            rotationCards[i].Click += (_, _) => { rotation = index * 90; Refresh(); };
        }
        void Half(bool up)
        {
            if (roundUp == up) return;
            roundUp = up;
            _settings.TransformRoundUp = up;
            _settings.Save();
            Refresh();
        }
        upChoice.Checked += (_, _) => Half(true);
        downChoice.Checked += (_, _) => Half(false);

        Refresh();
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        ApplyTransform(current, target, rotation, roundUp);
    }

    // ── Doing it ─────────────────────────────────────────────────────────────

    private void ApplyTransform(double current, double? target, int rotation, bool roundUp)
    {
        var plan = ZplTransform.Build(_currentText, current, target, rotation, roundUp);
        if (plan.Empty) return;

        ClearInspectSelection();
        ApplyEdits(plan.Edits);

        // A density conversion keeps the label the size it is in the real world, so
        // the preview has to be told the dots are a different size now — otherwise
        // the same label would appear to have grown.
        if (target is { } wanted) ShowDensity(wanted);

        // A quarter turn stands the label on its side, and the size shown in the
        // toolbar has to turn with it. An ordinary edit leaves that size alone, on
        // purpose — a typed size must survive typing — but this is not an ordinary
        // edit: the whole document changed shape, so the size is read off it again.
        if (rotation != 0) RefreshPreview(SizeUpdate.DocumentLoaded);
    }

    // Moves the toolbar's density to the one the document was just written for.
    //
    // Changing that list by hand rewrites ^PW/^LL on its own, so that a label keeps
    // its real size when the resolution changes. The conversion has ALREADY done
    // that, to every length in the document rather than just those two, so the list
    // is told to hold its tongue - as it is when a file is opened.
    private void ShowDensity(double dpmm)
    {
        foreach (var item in DensityComboBox.Items)
            if (item is DpmmOption option && Math.Abs(option.Dpmm - dpmm) < 1e-9)
            {
                if (ReferenceEquals(DensityComboBox.SelectedItem, item)) return;
                _suppressDensityRescale = true;
                DensityComboBox.SelectedItem = item;
                _lastDpmm = SelectedDpmm;
                _suppressDensityRescale = false;
                RefreshPreview(SizeUpdate.KeepCurrent);
                return;
            }
    }

    private static string Mm(double value) =>
        value.ToString(value < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);

    private static string Dpmm(double value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);

    // ── The pieces ───────────────────────────────────────────────────────────

    // A heading with the long version of itself behind an "i". What has to be read
    // every time stays on the line; what has to be read once stays out of the way.
    private static StackPanel SectionHead(string title, string tooltip, bool small = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Margin = new Thickness(0, small ? 10 : 16, 0, 0),
        };
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = small ? 13 : 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(MakeInfoIcon(tooltip));
        return row;
    }

    private static TextBlock SectionLine(string text) => new()
    {
        Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
    };

    private static ToolbarWrapPanel CardRow(IEnumerable<ToggleButton> cards)
    {
        var row = new ToolbarWrapPanel
        {
            HorizontalSpacing = 8, VerticalSpacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var card in cards) row.Children.Add(card);
        return row;
    }

    // A card is a toggle rather than a button, because that is what it is: the
    // framework already knows how to draw one that is on, and it keeps knowing it
    // while the pointer is over it — which a border painted on by hand does not.
    //
    // The badge in the corner says which card the document is already on. It is
    // text and nothing else: a chip would need a background that works over the
    // accent as well as over the card face, and the word alone reads on both.
    private static ToggleButton PickCard(string title, string? sub, string? tag = null)
    {
        var stack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (sub is not null)
            stack.Children.Add(new TextBlock
            {
                Text = sub, FontSize = 12, Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

        FrameworkElement content = stack;
        if (tag is not null)
        {
            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(new TextBlock
            {
                Text = tag, FontSize = 10, FontWeight = FontWeights.SemiBold, Opacity = 0.75,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            });
            content = grid;
        }

        return new ToggleButton
        {
            Content = content,
            Width = 146,
            Height = 76,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
    }

    // Chosen or not. Nothing is painted here: the toggle's own "on" look is the
    // mark, and it is the same mark everywhere else in the application.
    private static void Dress(ToggleButton card, bool chosen)
    {
        if (card.IsChecked != chosen) card.IsChecked = chosen;
    }
}
