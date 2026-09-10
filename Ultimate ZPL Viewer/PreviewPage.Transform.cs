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
// Both are questions with a small number of answers, so they are asked as cards
// rather than as lists — the whole set is on screen, and the one that is chosen
// is the one wearing the accent. Nothing happens until the button is pressed, and
// what happens is a single change: one Ctrl+Z puts the document back.
public sealed partial class PreviewPage
{
    private static string TL(string key) => LocalizationService.Get("transform." + key);

    // The densities on offer. The list is the same one the toolbar shows, minus
    // the ones nobody prints at.
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
        double? target = null;
        int rotation = 0;
        bool roundUp = _settings.TransformRoundUp;

        var body = new StackPanel { Spacing = 6, MinWidth = 560 };

        // ── Density ─────────────────────────────────────────────────────────
        body.Children.Add(SectionTitle(TL("density.title")));
        body.Children.Add(SectionLine(TL("density.desc")));
        body.Children.Add(SectionLine(
            string.Format(TL("density.current"), new DpmmOption(current).Label)));

        var densityCards = new List<ToggleButton>();
        var densityRow = new ToolbarWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        densityCards.Add(PickCard(TL("density.none"), null));
        foreach (var d in TransformDensities)
        {
            var option = new DpmmOption(d);
            densityCards.Add(PickCard(Dpmm(d) + " dpmm", option.Dpi + " dpi"));
        }
        foreach (var card in densityCards) densityRow.Children.Add(card);
        body.Children.Add(densityRow);

        // ── Which way a half goes ───────────────────────────────────────────
        // Only ever visible on a barcode, where the half is multiplied by every
        // module of the symbol - and where neither answer is the one that was
        // there. So it is asked rather than decided.
        var halfNote = SectionLine(TL("half.desc"));
        halfNote.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(halfNote);
        var upChoice = new RadioButton { GroupName = "TransformHalf", Content = TL("half.up"), MinWidth = 0 };
        var downChoice = new RadioButton { GroupName = "TransformHalf", Content = TL("half.down"), MinWidth = 0 };
        var halfRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        halfRow.Children.Add(upChoice);
        halfRow.Children.Add(downChoice);
        body.Children.Add(halfRow);

        // ── Rotation ────────────────────────────────────────────────────────
        body.Children.Add(SectionTitle(TL("rotate.title")));
        body.Children.Add(SectionLine(TL("rotate.desc")));

        var rotationCards = new List<ToggleButton> { PickCard(TL("rotate.none"), null) };
        foreach (var angle in new[] { 90, 180, 270 })
            rotationCards.Add(PickCard(string.Format(TL("rotate.deg"), angle), null));
        var rotationRow = new ToolbarWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var card in rotationCards) rotationRow.Children.Add(card);
        body.Children.Add(rotationRow);

        // ── What it will do ─────────────────────────────────────────────────
        var outcome = new StackPanel { Spacing = 2, Margin = new Thickness(0, 14, 0, 0) };
        body.Children.Add(outcome);

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
            for (int i = 0; i < densityCards.Count; i++)
                Dress(densityCards[i], i == 0 ? target is null : target is { } t && Math.Abs(t - TransformDensities[i - 1]) < 1e-9);
            for (int i = 0; i < rotationCards.Count; i++)
                Dress(rotationCards[i], rotation == i * 90);

            upChoice.IsChecked = roundUp;
            downChoice.IsChecked = !roundUp;
            // The question only arises when a length is being recalculated.
            halfNote.Opacity = target is null ? 0.35 : 0.7;
            upChoice.IsEnabled = downChoice.IsEnabled = target is not null;

            bool acts = target is not null || rotation != 0;
            dialog.IsPrimaryButtonEnabled = acts;
            outcome.Children.Clear();
            if (!acts)
            {
                outcome.Children.Add(SectionLine(TL("msg.nothing")));
                return;
            }
            foreach (var line in Outcome(current, target, rotation, roundUp)) outcome.Children.Add(line);
        }

        for (int i = 0; i < densityCards.Count; i++)
        {
            int index = i;
            densityCards[i].Click += (_, _) =>
            {
                target = index == 0 ? null : TransformDensities[index - 1];
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

    // ── What the dialog says it will do ──────────────────────────────────────

    private IEnumerable<FrameworkElement> Outcome(double current, double? target, int rotation, bool roundUp)
    {
        double ratio = target is { } t && current > 0 ? t / current : 1;
        double dpmm = target ?? current;
        var (w, h) = ZplTransform.SizeAfter(_model, ratio, rotation);
        if (dpmm > 0)
        {
            yield return SectionLine(string.Format(TL("result"),
                Mm(w / dpmm), Mm(h / dpmm), new DpmmOption(dpmm).Label));
        }

        // Everything the engine met and left alone, named. A file it understood
        // whole says nothing, which is the answer people are hoping for.
        var notes = ZplTransform.Build(_currentText, current, target, rotation, roundUp).Notes;
        if (notes.Count == 0) yield break;

        yield return new TextBlock
        {
            Text = TL("notes.title"), FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 2),
        };
        foreach (var note in notes)
            yield return SectionLine(string.Format(TL("notes." + note.Kind), note.Command, note.Line));
    }

    private static string Mm(double value) =>
        value.ToString(value < 100 ? "0.#" : "0", CultureInfo.CurrentCulture);

    private static string Dpmm(double value) =>
        value.ToString("0.##", CultureInfo.CurrentCulture);

    // ── The cards ────────────────────────────────────────────────────────────

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 0),
    };

    private static TextBlock SectionLine(string text) => new()
    {
        Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
    };

    // A card is a toggle rather than a button, because that is what it is: the
    // framework already knows how to draw one that is on, and it keeps knowing it
    // while the pointer is over it — which a border painted on by hand does not.
    private static ToggleButton PickCard(string title, string? sub)
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

        return new ToggleButton
        {
            Content = stack,
            Width = 146,
            Height = 72,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    // Chosen or not. Nothing is painted here: the toggle's own "on" look is the
    // mark, and it is the same mark everywhere else in the application.
    private static void Dress(ToggleButton card, bool chosen)
    {
        if (card.IsChecked != chosen) card.IsChecked = chosen;
    }
}
