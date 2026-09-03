using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ultimate_ZPL_Viewer;

// ── What the selected element is made of ────────────────────────────────────
// The bar floating over the selection carries the two things that get changed
// constantly — what the field prints, and which symbology a code uses — and hides
// the rest behind the "…" button. Everything else in one row would make a strip
// wider than most labels, sitting on top of the very thing being edited.
//
// Nothing is regenerated here either: each control rewrites the one command that
// holds its value (see ZplPatcher), and the change travels the usual path.
public sealed partial class PreviewPage
{
    private ZplPatcher.FieldFacts? _facts;
    private bool _fillingProps;          // guards the controls against their own events
    private bool _aspectLocked = true;
    private int _propsBuiltFor = -1;     // the selection the open panel was built for

    private void InitSelectionProperties()
    {
        // Every keystroke goes straight to the label. It was held back for a
        // moment to keep the undo history tidy, and that read as lag on the one
        // thing that should feel immediate; one undo step per letter is the price,
        // and it is the right way round.
        SelectionData.TextChanged += (_, _) => { if (!_fillingProps) CommitData(); };
        SelectionData.LostFocus += (_, _) => CommitData();

        SelectionKind.SelectionChanged += (_, _) =>
        {
            if (_fillingProps) return;
            ChangeSymbology();
        };

        SelectionMoreButton.Checked += (_, _) =>
        {
            _propsBuiltFor = -1;
            RefreshPropertiesPanel();
            // The plate just got taller: it has to be lifted by that much, or it
            // grows down over the element it belongs to.
            UpdateSelectionTools();
        };
        SelectionMoreButton.Unchecked += (_, _) =>
        {
            SelectionProps.Children.Clear();
            SelectionProps.Visibility = Visibility.Collapsed;
            SelectionMoreIcon.Glyph = "";
            UpdateSelectionTools();
        };
    }

    // ── Filling the bar ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads the selected field and shows the controls that apply to it. Called on
    /// every selection change and every redraw, so it must never fight the user:
    /// the content box keeps whatever is being typed into it.
    /// </summary>
    private void RefreshSelectionProperties()
    {
        if (_selStart < 0) { _facts = null; return; }
        _facts = ZplPatcher.Read(_currentText, _selStart, _selEnd);

        _fillingProps = true;
        try
        {
            bool hasData = _facts.DataStart >= 0;
            SelectionData.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
            if (hasData && SelectionData.FocusState == FocusState.Unfocused)
                SelectionData.Text = _facts.Data ?? "";

            var spec = BarcodeCatalog.ByCommand(_facts.Barcode, _facts.Data);
            SelectionKind.Visibility = spec is null ? Visibility.Collapsed : Visibility.Visible;
            if (spec is not null)
            {
                if (SelectionKind.Items.Count == 0)
                    foreach (var s in BarcodeCatalog.All)
                        SelectionKind.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s.Key });
                SelectionKind.SelectedIndex = Array.FindIndex(BarcodeCatalog.All, s => s.Key == spec.Key);
            }

            // Only worth offering when there is something to open.
            bool expandable = _facts.FontName is not null
                || _facts.Barcode is not null || _facts.Shape is not null;
            SelectionMoreButton.Visibility = expandable ? Visibility.Visible : Visibility.Collapsed;
            if (!expandable) SelectionMoreButton.IsChecked = false;

            ShowWarning(spec is null ? null : BarcodeCatalog.Validate(spec.Key, _facts.Data ?? ""));
        }
        finally { _fillingProps = false; }

        RefreshPropertiesPanel();
    }

    /// <summary>
    /// Builds the second row when it is open. Rebuilt only when the SELECTION
    /// changes, not on every redraw: an edit made from a spin button would
    /// otherwise tear the control out from under the pointer.
    /// </summary>
    private void RefreshPropertiesPanel()
    {
        if (SelectionMoreButton.IsChecked != true || _facts is null || _selStart < 0)
            return;
        SelectionMoreIcon.Glyph = "";
        if (_propsBuiltFor == _selStart && SelectionProps.Children.Count > 0)
        {
            SelectionProps.Visibility = Visibility.Visible;
            return;
        }

        _propsBuiltFor = _selStart;
        SelectionProps.Children.Clear();
        if (_facts.FontName is not null) BuildTextProperties(SelectionProps);
        if (_facts.Barcode is not null) BuildBarcodeProperties(SelectionProps);
        if (_facts.Shape is not null) BuildShapeProperties(SelectionProps);
        SelectionProps.Visibility = SelectionProps.Children.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowWarning(string? message)
    {
        SelectionWarning.Text = message ?? "";
        SelectionWarning.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── The two inline controls ─────────────────────────────────────────────

    private void CommitData()
    {
        if (_facts is null || _selStart < 0) return;
        var value = SelectionData.Text;
        if (value == (_facts.Data ?? "")) return;
        var edit = ZplPatcher.SetData(_currentText, _selStart, _selEnd, value);
        if (edit is { } e) ApplyEdit(e);
    }

    /// <summary>
    /// Swaps the symbology. The parameter layouts do not line up between them, so
    /// the selector is rewritten whole; and when the content would not survive the
    /// change — letters in an EAN-13 — it is replaced by that symbology's sample
    /// rather than left to draw nothing.
    /// </summary>
    private void ChangeSymbology()
    {
        if (_facts is null || _selStart < 0) return;
        if (SelectionKind.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        var spec = BarcodeCatalog.ByKey(key);
        if (spec is null) return;

        var current = BarcodeCatalog.ByCommand(_facts.Barcode, _facts.Data);
        if (current?.Key == spec.Key) return;

        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        int height = (int)Math.Round(_facts.BarcodeHeight is > 0 && current?.TwoD == false
            ? _facts.BarcodeHeight.Value : 12 * dpmm);
        int module = Math.Max(2, (int)Math.Round(dpmm / 2));

        var edits = new List<ZplPatcher.Edit>();
        if (ZplPatcher.SetSymbology(_currentText, _selStart, _selEnd, spec.Command, spec.Args(height, module))
            is { } swap) edits.Add(swap);

        // The data only changes when it has to: a payload the new symbology accepts
        // is the user's, and replacing it would be rude.
        string data = _facts.Data ?? "";
        if (BarcodeCatalog.Validate(spec.Key, data) is not null
            && ZplPatcher.SetData(_currentText, _selStart, _selEnd, spec.Sample) is { } retext)
            edits.Add(retext);

        if (edits.Count > 0) ApplyEdits(edits);
    }

    // ── Everything else, behind the "…" ─────────────────────────────────────

    // A label on the left, its control on the right — the settings-card shape,
    // shrunk to fit over a label.
    private static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    // Not static: it has to see _fillingProps, so a value the code puts in — the
    // width following a locked height — does not come back as a second edit.
    private NumberBox Num(double? value, double min, double max, Action<double> apply)
    {
        var box = new NumberBox
        {
            Value = value ?? double.NaN,
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 100,
        };
        box.ValueChanged += (_, _) =>
        {
            if (_fillingProps || double.IsNaN(box.Value)) return;
            apply(Math.Clamp(box.Value, min, max));
        };
        return box;
    }

    private static string PL(string key) => LocalizationService.Get("mode.props." + key);

    private void BuildTextProperties(StackPanel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = PL("textSection"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // The printer's resident fonts. 0 is the scalable one everything else is
        // measured against; A to H are the bitmap cells.
        var fonts = new ComboBox { MinWidth = 100 };
        var names = new[] { "0", "A", "B", "C", "D", "E", "F", "G", "H" };
        foreach (var n in names)
            fonts.Items.Add(new ComboBoxItem
            {
                Content = n == "0" ? PL("fontScalable") : n,
                Tag = n,
            });
        fonts.SelectedIndex = Math.Max(0, Array.IndexOf(names, _facts!.FontName));
        fonts.SelectionChanged += (_, _) =>
        {
            if (fonts.SelectedItem is not ComboBoxItem { Tag: string name }) return;
            var edit = ZplPatcher.SetFont(_currentText, _selStart, _selEnd, name, null, null);
            if (edit is { } e) ApplyEdit(e);
        };
        panel.Children.Add(Row(PL("font"), fonts));

        NumberBox? widthBox = null;
        var heightBox = Num(_facts.FontHeight, 4, 4000, h =>
        {
            double? w = _aspectLocked ? h : null;
            var edit = ZplPatcher.SetFont(_currentText, _selStart, _selEnd, null, h, w);
            if (edit is { } e) ApplyEdit(e);
            if (_aspectLocked && widthBox is not null)
            {
                // Shown, not applied: the single edit above already carried it.
                _fillingProps = true;
                widthBox.Value = h;
                _fillingProps = false;
            }
        });
        panel.Children.Add(Row(PL("height"), heightBox));

        widthBox = Num(_facts.FontWidth ?? _facts.FontHeight, 4, 4000, w =>
        {
            var edit = ZplPatcher.SetFont(_currentText, _selStart, _selEnd, null, null, w);
            if (edit is { } e) ApplyEdit(e);
        });
        var lockToggle = new ToggleButton
        {
            IsChecked = _aspectLocked,
            Width = 32, Height = 32, MinWidth = 0, Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 13 },
        };
        ToolTipService.SetToolTip(lockToggle, TipBlock(PL("lockAspect")));
        lockToggle.Checked += (_, _) => { _aspectLocked = true; widthBox.IsEnabled = false; };
        lockToggle.Unchecked += (_, _) => { _aspectLocked = false; widthBox.IsEnabled = true; };
        widthBox.IsEnabled = !_aspectLocked;

        var widthRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        widthRow.Children.Add(widthBox);
        widthRow.Children.Add(lockToggle);
        panel.Children.Add(Row(PL("width"), widthRow));

        var reverse = new ToggleSwitch { IsOn = _facts.Reverse, OnContent = "", OffContent = "" };
        reverse.Toggled += (_, _) =>
        {
            var edit = ZplPatcher.SetReverse(_currentText, _selStart, _selEnd, reverse.IsOn);
            if (edit is { } e) ApplyEdit(e);
        };
        panel.Children.Add(Row(PL("reverse"), reverse));
    }

    private void BuildBarcodeProperties(StackPanel panel)
    {
        var spec = BarcodeCatalog.ByCommand(_facts!.Barcode, _facts.Data);
        bool twoD = spec?.TwoD ?? false;

        panel.Children.Add(new TextBlock
        {
            Text = PL("codeSection"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // A 1D symbol is sized by the height of its bars, a 2D one by the size of
        // its modules: the same control, a different meaning.
        panel.Children.Add(Row(twoD ? PL("module") : PL("barHeight"),
            Num(_facts.BarcodeHeight, 1, twoD ? 30 : 4000, v =>
            {
                var edit = ZplPatcher.SetBarcodeSize(_currentText, _selStart, _selEnd, v);
                if (edit is { } e) ApplyEdit(e);
            })));

        if (twoD) return;

        panel.Children.Add(Row(PL("moduleWidth"),
            Num(_facts.ModuleWidth ?? 2, 1, 10, v =>
            {
                var edit = ZplPatcher.SetModuleWidth(_currentText, _selStart, _selEnd, v);
                if (edit is { } e) ApplyEdit(e);
            })));

        if (_facts.HumanReadable is { } hrt)
        {
            var toggle = new ToggleSwitch { IsOn = hrt, OnContent = "", OffContent = "" };
            toggle.Toggled += (_, _) =>
            {
                var edit = ZplPatcher.SetHumanReadable(_currentText, _selStart, _selEnd, toggle.IsOn);
                if (edit is { } e) ApplyEdit(e);
            };
            panel.Children.Add(Row(PL("humanReadable"), toggle));
        }
    }

    private void BuildShapeProperties(StackPanel panel)
    {
        var args = _facts!.ShapeArgs ?? Array.Empty<double>();
        bool circle = _facts.Shape == "GC";

        panel.Children.Add(new TextBlock
        {
            Text = PL("shapeSection"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        double At(int i) => i < args.Length ? args[i] : 0;

        panel.Children.Add(Row(circle ? PL("diameter") : PL("shapeWidth"),
            Num(At(0), 1, 32000, v =>
            {
                var edit = ZplPatcher.SetShape(_currentText, _selStart, _selEnd, v, null, null);
                if (edit is { } e) ApplyEdit(e);
            })));

        if (!circle)
            panel.Children.Add(Row(PL("shapeHeight"),
                Num(At(1), 0, 32000, v =>
                {
                    var edit = ZplPatcher.SetShape(_currentText, _selStart, _selEnd, null, v, null);
                    if (edit is { } e) ApplyEdit(e);
                })));

        panel.Children.Add(Row(PL("thickness"),
            Num(At(circle ? 1 : 2), 1, 32000, v =>
            {
                var edit = ZplPatcher.SetShape(_currentText, _selStart, _selEnd, null, null, v);
                if (edit is { } e) ApplyEdit(e);
            })));
    }
}
