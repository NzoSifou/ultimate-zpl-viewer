using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// The margins field of the print dialog and of the print settings: one margin
/// for the four sides, or - with the "per side" switch - top, right, bottom and
/// left, two by two. Values are typed in mm or cm; <see cref="Value"/> is always
/// millimetres. Each mode keeps its own numbers, so flipping the switch back and
/// forth loses nothing.
/// </summary>
internal sealed class MarginEditor
{
    private readonly AppSettings _settings;
    private readonly NumberBox _all;
    private readonly ComboBox _unit;
    private readonly NumberBox[] _sides = new NumberBox[4];      // top, right, bottom, left
    private readonly TextBlock[] _sideUnits = new TextBlock[4];
    private readonly Grid _uniformRow;
    private readonly StackPanel _sidesPanel;
    private readonly ComboBox _sidesUnit;
    private bool _updating;

    /// <summary>The switch, for the caller to place next to the field's name.</summary>
    public FrameworkElement Switch { get; }
    /// <summary>The boxes themselves.</summary>
    public FrameworkElement Body { get; }

    private readonly ToggleSwitch _perSide;

    public bool PerSide => _perSide.IsOn;

    /// <summary>The margins on show, in millimetres.</summary>
    public Margins Value => PerSide
        ? new Margins(Mm(_sides[0].Value), Mm(_sides[1].Value), Mm(_sides[2].Value), Mm(_sides[3].Value))
        : Margins.Uniform(Mm(_all.Value));

    /// <summary>Raised whenever <see cref="Value"/> may have changed.</summary>
    public event Action? Changed;

    private bool Cm => _settings.MarginsUnit == "cm";

    private static string L(string key) => LocalizationService.Get("settings.print.margins." + key);

    public MarginEditor(AppSettings settings, Margins initial, bool perSide, double minBoxWidth = 0)
    {
        _settings = settings;
        perSide |= !initial.IsUniform;

        _all = Box(initial.Top);
        if (minBoxWidth > 0) _all.MinWidth = minBoxWidth;
        _unit = UnitBox();
        _uniformRow = new Grid { ColumnSpacing = 6 };
        _uniformRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _uniformRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _uniformRow.Children.Add(_all);
        Grid.SetColumn(_unit, 1);
        _uniformRow.Children.Add(_unit);

        // Two by two, in the order the user reads them: top, right / bottom, left.
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] names = { "top", "right", "bottom", "left" };
        double[] values = initial.ToArray();
        for (int i = 0; i < 4; i++)
        {
            var box = Box(values[i]);
            box.Header = L(names[i]);
            _sides[i] = box;
            _sideUnits[i] = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 7),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            };
            var cell = new Grid { ColumnSpacing = 4 };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 24 });
            cell.Children.Add(box);
            Grid.SetColumn(_sideUnits[i], 1);
            cell.Children.Add(_sideUnits[i]);
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        _sidesUnit = UnitBox();
        var unitRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        unitRow.Children.Add(new TextBlock { Text = L("unit"), VerticalAlignment = VerticalAlignment.Center });
        unitRow.Children.Add(_sidesUnit);
        _sidesPanel = new StackPanel { Spacing = 8 };
        _sidesPanel.Children.Add(grid);
        _sidesPanel.Children.Add(unitRow);

        var body = new Grid();
        body.Children.Add(_uniformRow);
        body.Children.Add(_sidesPanel);
        Body = body;

        _perSide = new ToggleSwitch { IsOn = perSide, OnContent = null, OffContent = null, MinWidth = 0 };
        _perSide.Resources["ToggleSwitchThemeMinWidth"] = 0.0;
        _perSide.Resources["ToggleSwitchPreContentMargin"] = 0.0;
        _perSide.Resources["ToggleSwitchPostContentMargin"] = 0.0;
        var label = new TextBlock { Text = L("perSide"), VerticalAlignment = VerticalAlignment.Center };
        var switchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        switchRow.Children.Add(label);
        switchRow.Children.Add(_perSide);
        ToolTipService.SetToolTip(switchRow, L("perSideTip"));
        Switch = switchRow;

        _perSide.Toggled += (_, _) =>
        {
            // Going per side starts from the single margin, so nothing jumps.
            if (_perSide.IsOn && !_updating && SidesAllZeroOrEqual())
            {
                _updating = true;
                foreach (var s in _sides) s.Value = _all.Value;
                _updating = false;
            }
            ApplyMode();
            Changed?.Invoke();
        };
        _all.ValueChanged += (_, _) => { if (!_updating) Changed?.Invoke(); };
        foreach (var s in _sides) s.ValueChanged += (_, _) => { if (!_updating) Changed?.Invoke(); };
        _unit.SelectionChanged += (_, _) => SetUnit(_unit.SelectedIndex == 1);
        _sidesUnit.SelectionChanged += (_, _) => SetUnit(_sidesUnit.SelectedIndex == 1);

        ApplyMode();
        ApplyUnitLabels();
    }

    public bool IsEnabled
    {
        set
        {
            _all.IsEnabled = value; _unit.IsEnabled = value; _sidesUnit.IsEnabled = value;
            _perSide.IsEnabled = value;
            foreach (var s in _sides) s.IsEnabled = value;
        }
    }

    private bool SidesAllZeroOrEqual()
    {
        double first = _sides[0].Value;
        foreach (var s in _sides) if (Math.Abs(s.Value - first) > 1e-9) return false;
        return true;
    }

    private void ApplyMode()
    {
        _uniformRow.Visibility = PerSide ? Visibility.Collapsed : Visibility.Visible;
        _sidesPanel.Visibility = PerSide ? Visibility.Visible : Visibility.Collapsed;
    }

    // The same distances, written in the other unit. The unit is a display
    // choice shared by every margin field, so it is saved straight away.
    private void SetUnit(bool cm)
    {
        if (_updating || cm == Cm) return;
        var all = Mm(_all.Value);
        var sides = new double[4];
        for (int i = 0; i < 4; i++) sides[i] = Mm(_sides[i].Value);

        _settings.MarginsUnit = cm ? "cm" : "mm";
        _settings.Save();

        _updating = true;
        _all.Value = Show(all);
        _all.SmallChange = cm ? 0.5 : 1;
        for (int i = 0; i < 4; i++) { _sides[i].Value = Show(sides[i]); _sides[i].SmallChange = cm ? 0.5 : 1; }
        _updating = false;
        ApplyUnitLabels();
        Changed?.Invoke();
    }

    private void ApplyUnitLabels()
    {
        _updating = true;
        _unit.SelectedIndex = Cm ? 1 : 0;
        _sidesUnit.SelectedIndex = Cm ? 1 : 0;
        _updating = false;
        foreach (var t in _sideUnits) t.Text = Cm ? "cm" : "mm";
    }

    private NumberBox Box(double mm) => new()
    {
        Minimum = 0, Maximum = 100, SmallChange = Cm ? 0.5 : 1, Value = Show(mm),
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static ComboBox UnitBox() => new() { ItemsSource = new[] { "mm", "cm" }, MinWidth = 74 };

    private double Show(double mm) => Cm ? mm / 10.0 : mm;
    private double Mm(double shown) => double.IsNaN(shown) ? 0 : Math.Max(0, Cm ? shown * 10 : shown);
}
