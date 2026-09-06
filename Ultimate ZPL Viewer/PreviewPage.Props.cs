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
            SetMoreGlyph(open: false);
            UpdateSelectionTools();
        };
    }

    // Which way the plate is stacked over its element: the properties hang under
    // the buttons when the plate is below it, and over them when it is above, so
    // the buttons always touch the element and the panel always opens away from it.
    private bool _propsAbove;

    /// <summary>Puts the properties on the far side of the buttons from the element.</summary>
    private void SetPropsSide(bool above)
    {
        if (above == _propsAbove && SelectionStack.Children.Count > 0) return;
        _propsAbove = above;
        SelectionStack.Children.Clear();
        if (above)
        {
            SelectionStack.Children.Add(SelectionProps);
            SelectionStack.Children.Add(SelectionWarning);
            SelectionStack.Children.Add(SelectionToolsRow);
        }
        else
        {
            SelectionStack.Children.Add(SelectionToolsRow);
            SelectionStack.Children.Add(SelectionProps);
            SelectionStack.Children.Add(SelectionWarning);
        }
        SetMoreGlyph(SelectionMoreButton.IsChecked == true);
    }

    // The chevron says which way the panel will move, not which way it is: down
    // when it will unfold downwards, up when upwards, and the other way round once
    // it is open and the click would fold it back.
    private void SetMoreGlyph(bool open)
        => SelectionMoreIcon.Glyph = _propsAbove ^ open ? "" : "";

    // ── Filling the bar ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads the selected field and shows the controls that apply to it. Called on
    /// every selection change and every redraw, so it must never fight the user:
    /// the content box keeps whatever is being typed into it.
    /// </summary>
    private void RefreshSelectionProperties()
    {
        if (_selStart < 0) { _facts = null; return; }
        if (HasMultiSelection)
        {
            // Nothing in this row means anything for several elements at once.
            _facts = null;
            SelectionMoreButton.Visibility = Visibility.Collapsed;
            SelectionMoreButton.IsChecked = false;
            EditImageButton.Visibility = Visibility.Collapsed;
            ShowWarning(null);
            return;
        }
        _facts = ZplPatcher.Read(_currentText, _selStart, _selEnd);

        _fillingProps = true;
        try
        {
            var spec = BarcodeCatalog.ByCommand(_facts.Barcode, _facts.Data);

            // Only worth offering when there is something to open.
            bool expandable = _facts.FontName is not null
                || _facts.Barcode is not null || _facts.Shape is not null;
            SelectionMoreButton.Visibility = expandable ? Visibility.Visible : Visibility.Collapsed;
            if (!expandable) SelectionMoreButton.IsChecked = false;

            // A graphic has no properties to spin; it is re-imported instead.
            EditImageButton.Visibility = _facts.Graphic is not null
                ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(EditImageButton, TipBlock(LocalizationService.Get("mode.act.image")));

            // ^FR prints white where the label is already black. Over white it does
            // exactly nothing, which reads as a broken switch — so say so.
            ShowWarning((spec is null ? null : BarcodeCatalog.Validate(spec.Key, _facts.Data ?? ""))
                        ?? (NothingBlackUnder() ? PL("reverseNothing") : null));
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
        SetMoreGlyph(open: true);
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

    /// <summary>
    /// True when the field is reversed and there is nothing black beneath it to
    /// reverse out of — the one case where ^FR is on and the label shows no change.
    /// </summary>
    private bool NothingBlackUnder()
    {
        if (_facts is null || !_facts.Reverse || _selStart < 0) return false;

        Windows.Foundation.Rect field = Windows.Foundation.Rect.Empty;
        foreach (var (element, drawable) in _hitMap)
        {
            if (drawable.SourceStart != _selStart) continue;
            var b = BoundsInCanvas(element);
            if (!b.IsEmpty) field = field.IsEmpty ? b : Union(field, b);
        }
        if (field.IsEmpty) return false;

        foreach (var (element, drawable) in _hitMap)
        {
            if (drawable is not ZplBox box || box.SourceStart == _selStart) continue;
            if (box.WhiteFill || box.Reverse) continue;
            // A frame leaves its middle white; only a solid block gives ^FR anything
            // to work against.
            if (box.Thickness < Math.Min(box.Width, box.Height) / 2.0) continue;
            var b = BoundsInCanvas(element);
            if (!b.IsEmpty && Intersects(b, field)) return false;
        }
        return true;
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
        if (_dataBox is null) return;
        var value = _dataBox.Text;
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
        if (_kindBox?.SelectedItem is not ComboBoxItem { Tag: string key }) return;
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
            // Clamped before it is handed over: a NumberBox given a value outside
            // its limits corrects it, and that correction arrives as a change the
            // user never made — which is how a barcode with no height of its own
            // ended up with a height of one the moment its properties were opened.
            Value = value is null ? double.NaN : Math.Clamp(value.Value, min, max),
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

    // A ToggleSwitch keeps room for the words "On"/"Off" even when there are none,
    // and that reserved strip is what left every switch short of the right edge
    // while every other control reached it.
    private static ToggleSwitch Switch(bool on)
    {
        var box = new ToggleSwitch
        {
            IsOn = on,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // The template keeps a minimum of its own, and it does not answer to the
        // control's MinWidth. The two "content margin" resources beside it are
        // DOUBLES, not thicknesses — handing them a Thickness makes the control
        // throw the moment anything measures it.
        box.Resources["ToggleSwitchThemeMinWidth"] = 0d;
        // And it still keeps a strip to the right of the switch for words it does
        // not have. Eleven dips of it, measured on screen — pulled back so the
        // control ends where every other one in the column ends.
        box.Margin = new Thickness(0, 0, -11, 0);
        return box;
    }

    private ComboBox? _kindBox;

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

        var reverse = Switch(_facts.Reverse);
        reverse.Toggled += (_, _) =>
        {
            var edit = ZplPatcher.SetReverse(_currentText, _selStart, _selEnd, reverse.IsOn);
            if (edit is { } e) ApplyEdit(e);
        };
        panel.Children.Add(Row(PL("reverse"), reverse));
    }

    private TextBox NewDataBox()
    {
        var box = new TextBox
        {
            Width = 150,
            MinHeight = 0,
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => { if (!_fillingProps) CommitData(); };
        box.LostFocus += (_, _) => CommitData();
        return box;
    }

    /// <summary>The field, and a button that opens it out to a few lines.</summary>
    private FrameworkElement WithExpander(TextBox box)
    {
        var open = new ToggleButton
        {
            Width = 28,
            Height = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            IsChecked = _dataExpanded,
            Content = new FontIcon { Glyph = "\uE740", FontSize = 12 },
        };
        ToolTipService.SetToolTip(open, TipBlock(PL("expand")));
        void Apply(bool wide)
        {
            _dataExpanded = wide;
            box.AcceptsReturn = false;
            box.TextWrapping = wide ? TextWrapping.Wrap : TextWrapping.NoWrap;
            box.Height = wide ? 96 : double.NaN;
        }
        open.Checked += (_, _) => Apply(true);
        open.Unchecked += (_, _) => Apply(false);
        Apply(_dataExpanded);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(box);
        row.Children.Add(open);
        return row;
    }

    private TextBox? _dataBox;
    private bool _dataExpanded;

    private ComboBox NewKindBox()
    {
        var box = new ComboBox { MinWidth = 128, FontSize = 12 };
        foreach (var s in BarcodeCatalog.All)
            box.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s.Key });
        box.SelectionChanged += (_, _) => { if (!_fillingProps) ChangeSymbology(); };
        return box;
    }

    private void BuildBarcodeProperties(StackPanel panel)
    {
        var spec = BarcodeCatalog.ByCommand(_facts!.Barcode, _facts.Data);
        bool twoD = spec?.TwoD ?? false;

        panel.Children.Add(new TextBlock
        {
            Text = PL("codeSection"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        // What it prints. In here rather than in the floating strip for the same
        // reason as the symbology: that strip lies on top of the element being
        // edited, and a payload can be long. The button beside it opens the field
        // out to a few lines when one letter at a time is not enough to read.
        if (_facts.DataStart >= 0)
        {
            // Built fresh, never carried over: a control kept between rebuilds is
            // still a child of the row it was in — the panel is cleared, the row grid
            // it held is not — and adding it to a second parent throws. Detaching it
            // by hand worked until it did not; not keeping it cannot fail at all.
            _dataBox = NewDataBox();
            _fillingProps = true;
            try { if (_dataBox.FocusState == FocusState.Unfocused) _dataBox.Text = _facts.Data ?? ""; }
            finally { _fillingProps = false; }
            panel.Children.Add(Row(PL("content"), WithExpander(_dataBox)));
        }

        // Which symbology it is, where the rest of its properties are — beside the
        // content field it was one more thing crowding the strip that sits on top of
        // the element being edited.
        if (spec is not null)
        {
            var kinds = NewKindBox();
            _fillingProps = true;
            try { kinds.SelectedIndex = Array.FindIndex(BarcodeCatalog.All, s => s.Key == spec.Key); }
            finally { _fillingProps = false; }
            _kindBox = kinds;
            panel.Children.Add(Row(PL("symbology"), kinds));
        }

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
            var toggle = Switch(hrt);
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
