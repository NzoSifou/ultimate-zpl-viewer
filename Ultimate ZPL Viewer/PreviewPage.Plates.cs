using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace Ultimate_ZPL_Viewer;

// ── Where the floating plates sit ───────────────────────────────────────────
// Two plates float over the preview: the view/edit switch, and — in edit mode —
// the row of tools. Both are laid out the same way, so both are described the
// same way: an ANCHOR (one of the eight edges and corners of the preview) or a
// FREE position the plate was dragged to, plus the direction it stacks in.
//
// A free plate carries two grips, one at each end of its stack, and is dragged
// by them rather than by its whole body: the plate is made of buttons, and a
// press anywhere on it has to stay a press on the button underneath. Locking
// keeps the position and takes the grips away.
public sealed partial class PreviewPage
{
    /// <summary>The eight places a plate can be pinned to, read left to right, top to bottom.</summary>
    internal static readonly string[] PlateAnchors =
    {
        "topLeft", "topCenter", "topRight",
        "middleLeft", "middleRight",
        "bottomLeft", "bottomCenter", "bottomRight",
    };

    // Air between a plate and the edge it hugs. Wider left/right than top/bottom:
    // those are window edges, and the preview's vertical scrollbar lives in that
    // same strip.
    private const double PlateEdgeX = 24;
    private const double PlateEdgeY = 10;

    // Between two plates sharing one spot.
    private const double PlateStackGap = 8;

    private readonly Dictionary<Border, List<UIElement>> _plateGrips = new();
    private readonly Dictionary<Border, (bool Show, bool Horizontal)> _gripState = new();

    /// <summary>Applies both plates' position and orientation from the settings.</summary>
    private void ApplyPlatePlacement()
    {
        // How each plate LOOKS first, both of them, and only then where it goes:
        // two plates pinned to the same place stand one above the other, and how
        // far the lower one has to come down is the upper one's height - which is
        // not known until the upper one has been turned the way it is going to be.
        ApplyPlateLook(mode: true);
        ApplyPlateLook(mode: false);
        ApplyPlatePosition(mode: true);
        ApplyPlatePosition(mode: false);
    }

    private void ApplyPlateLook(bool mode)
    {
        var plate = mode ? ModeSwitch : EditToolbar;
        var stack = mode ? ModePlateStack : ToolPlateStack;
        bool horizontal = mode ? _settings.ModePlateHorizontal : _settings.ToolPlateHorizontal;
        bool free = mode ? _settings.ModePlateFree : _settings.ToolPlateFree;
        bool locked = mode ? _settings.ModePlateLocked : _settings.ToolPlateLocked;

        stack.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        if (!mode) ApplyToolSeparator(horizontal);
        ApplyGrips(plate, stack, mode, show: free && !locked, horizontal);
    }

    private void ApplyPlatePosition(bool mode)
    {
        var plate = mode ? ModeSwitch : EditToolbar;

        if (mode ? _settings.ModePlateFree : _settings.ToolPlateFree)
        {
            plate.HorizontalAlignment = HorizontalAlignment.Left;
            plate.VerticalAlignment = VerticalAlignment.Top;
            var (x, y) = FreePlatePosition(mode);
            plate.Margin = new Thickness(x, y, 0, 0);
            return;
        }

        string anchor = PlateAnchor(mode);

        // The rulers are drawn ON TOP of the preview rather than beside it, so a
        // plate pinned to an edge they cover has to step over the band itself.
        double top = PlateEdgeY + (_settings.ShowRulerHorizontal ? RulerBandDip : 0);
        double left = PlateEdgeX + (_settings.ShowRulerVertical ? RulerBandDip : 0);

        plate.HorizontalAlignment = anchor.EndsWith("Left", StringComparison.Ordinal)
            ? HorizontalAlignment.Left
            : anchor.EndsWith("Right", StringComparison.Ordinal)
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Center;
        plate.VerticalAlignment = anchor.StartsWith("top", StringComparison.Ordinal)
            ? VerticalAlignment.Top
            : anchor.StartsWith("bottom", StringComparison.Ordinal)
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Center;

        double mLeft = plate.HorizontalAlignment == HorizontalAlignment.Left ? left : 0;
        double mTop = plate.VerticalAlignment == VerticalAlignment.Top ? top : 0;
        double mRight = plate.HorizontalAlignment == HorizontalAlignment.Right ? PlateEdgeX : 0;
        double mBottom = plate.VerticalAlignment == VerticalAlignment.Bottom ? PlateEdgeY : 0;

        // Sharing the spot: the one below is pushed down by the other's height.
        // Against the top edge the lower plate takes the extra room above it;
        // against the bottom edge the upper one takes it below; in the middle both
        // lean away from the centre, half of it each, which is what a margin does
        // to something that is centred.
        if (SharesAnchor(mode, out bool onTop, out double otherHeight))
        {
            double step = otherHeight + PlateStackGap;
            if (plate.VerticalAlignment == VerticalAlignment.Top) { if (!onTop) mTop += step; }
            else if (plate.VerticalAlignment == VerticalAlignment.Bottom) { if (onTop) mBottom += step; }
            else if (onTop) mBottom += step; else mTop += step;
        }

        plate.Margin = new Thickness(mLeft, mTop, mRight, mBottom);
    }

    private string PlateAnchor(bool mode)
    {
        string anchor = mode ? _settings.ModePlateAnchor : _settings.ToolPlateAnchor;
        return PlateAnchors.Contains(anchor) ? anchor : mode ? "topRight" : "topLeft";
    }

    /// <summary>
    /// Whether the two plates are pinned to the same place - and if they are,
    /// which of them is on top and how tall the other one is. Only ever true when
    /// both are pinned AND both are on screen: a plate that is not shown is not in
    /// anyone's way, and the tool plate is only there in edit mode.
    /// </summary>
    private bool SharesAnchor(bool mode, out bool onTop, out double otherHeight)
    {
        onTop = true;
        otherHeight = 0;
        if (_settings.ModePlateFree || _settings.ToolPlateFree) return false;
        if (PlateAnchor(true) != PlateAnchor(false)) return false;
        if (ModeSwitch.Visibility != Visibility.Visible
            || EditToolbar.Visibility != Visibility.Visible) return false;

        onTop = mode ? _settings.ModePlateFirst : !_settings.ModePlateFirst;
        var other = mode ? EditToolbar : ModeSwitch;
        // Measured rather than read off the screen, so a plate that has just been
        // turned on its side is already the height it is about to be. Its MARGIN
        // comes back with it - DesiredSize includes it - and that margin is where
        // the last answer to this same question was written down: leave it in and
        // the two plates walk further apart on every pass.
        other.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        otherHeight = other.DesiredSize.Height - other.Margin.Top - other.Margin.Bottom;
        if (otherHeight <= 0) otherHeight = other.ActualHeight;
        return otherHeight > 0;
    }

    /// <summary>
    /// The free position, clamped inside the preview. -1 means the plate has never
    /// been dragged: it keeps the spot its anchor last put it on, so switching to
    /// a free position moves nothing until the grips are used.
    /// </summary>
    private (double X, double Y) FreePlatePosition(bool mode)
    {
        var plate = mode ? ModeSwitch : EditToolbar;
        double x = mode ? _settings.ModePlateX : _settings.ToolPlateX;
        double y = mode ? _settings.ModePlateY : _settings.ToolPlateY;
        if (x < 0 || y < 0)
        {
            var here = PlateOrigin(plate);
            x = here.X; y = here.Y;
        }
        return ClampPlate(plate, x, y);
    }

    /// <summary>Where the plate currently draws, in the preview's own coordinates.</summary>
    private Point PlateOrigin(Border plate)
    {
        try { return plate.TransformToVisual(PreviewLayoutGrid).TransformPoint(new Point(0, 0)); }
        catch { return AnchoredOrigin(plate == ModeSwitch); }
    }

    /// <summary>
    /// Where the plate's anchor puts it, worked out rather than measured. Used to
    /// start a free position off: a plate that has never been dragged has to keep
    /// the spot it was already in, and asking the visual tree for it is a question
    /// that can go unanswered.
    /// </summary>
    private Point AnchoredOrigin(bool mode)
    {
        var plate = mode ? ModeSwitch : EditToolbar;
        string anchor = mode ? _settings.ModePlateAnchor : _settings.ToolPlateAnchor;
        if (!PlateAnchors.Contains(anchor)) anchor = mode ? "topRight" : "topLeft";

        double w = plate.ActualWidth > 0 ? plate.ActualWidth : plate.DesiredSize.Width;
        double h = plate.ActualHeight > 0 ? plate.ActualHeight : plate.DesiredSize.Height;
        double gw = PreviewLayoutGrid.ActualWidth, gh = PreviewLayoutGrid.ActualHeight;
        double top = PlateEdgeY + (_settings.ShowRulerHorizontal ? RulerBandDip : 0);
        double left = PlateEdgeX + (_settings.ShowRulerVertical ? RulerBandDip : 0);

        double x = anchor.EndsWith("Left", StringComparison.Ordinal) ? left
                 : anchor.EndsWith("Right", StringComparison.Ordinal) ? gw - PlateEdgeX - w
                 : (gw - w) / 2;
        double y = anchor.StartsWith("top", StringComparison.Ordinal) ? top
                 : anchor.StartsWith("bottom", StringComparison.Ordinal) ? gh - PlateEdgeY - h
                 : (gh - h) / 2;
        return new Point(Math.Max(0, x), Math.Max(0, y));
    }

    private (double X, double Y) ClampPlate(Border plate, double x, double y)
    {
        // Before the first layout pass nothing has a size yet, and clamping against
        // a preview of width zero would file every plate away in the top-left
        // corner. The SizeChanged pass puts them right as soon as there is a size.
        if (PreviewLayoutGrid.ActualWidth <= 0 || PreviewLayoutGrid.ActualHeight <= 0)
            return (Math.Max(0, x), Math.Max(0, y));

        double w = plate.ActualWidth > 0 ? plate.ActualWidth : plate.DesiredSize.Width;
        double h = plate.ActualHeight > 0 ? plate.ActualHeight : plate.DesiredSize.Height;
        double maxX = Math.Max(0, PreviewLayoutGrid.ActualWidth - w);
        double maxY = Math.Max(0, PreviewLayoutGrid.ActualHeight - h);
        return (Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }

    // A hairline across the plate, so it has to turn with it: a horizontal rule
    // between two buttons standing side by side separates nothing.
    private void ApplyToolSeparator(bool horizontal)
    {
        ToolPlateSeparator.Width = horizontal ? 1 : double.NaN;
        ToolPlateSeparator.Height = horizontal ? double.NaN : 1;
        ToolPlateSeparator.Margin = horizontal ? new Thickness(2, 5, 2, 5) : new Thickness(5, 2, 5, 2);
        ToolPlateSeparator.HorizontalAlignment = horizontal
            ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ToolPlateSeparator.VerticalAlignment = horizontal
            ? VerticalAlignment.Stretch : VerticalAlignment.Center;
    }

    // ── The grips ───────────────────────────────────────────────────────────

    private void ApplyGrips(Border plate, StackPanel stack, bool mode, bool show, bool horizontal)
    {
        // Left alone when they are already what they should be. Placement is
        // re-applied on every resize, and a resize can happen mid-drag: tearing the
        // grip out of the tree and building a new one loses the pointer capture,
        // and with it the rest of the gesture.
        if (_gripState.TryGetValue(plate, out var was) && was == (show, horizontal)) return;
        _gripState[plate] = (show, horizontal);

        if (_plateGrips.TryGetValue(plate, out var old))
        {
            foreach (var grip in old) stack.Children.Remove(grip);
            _plateGrips.Remove(plate);
        }
        if (!show) return;

        var grips = new List<UIElement> { NewGrip(plate, mode, horizontal), NewGrip(plate, mode, horizontal) };
        stack.Children.Insert(0, (UIElement)grips[0]);
        stack.Children.Add((UIElement)grips[1]);
        _plateGrips[plate] = grips;
    }

    private CursorGrid NewGrip(Border plate, bool mode, bool horizontal)
    {
        var bar = new Border
        {
            Width = horizontal ? 4 : 20,
            Height = horizontal ? 20 : 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grip = new CursorGrid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Width = horizontal ? 12 : double.NaN,
            Height = horizontal ? double.NaN : 12,
        };
        grip.Children.Add(bar);
        try { grip.SetCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeAll)); } catch { }
        ToolTipService.SetToolTip(grip, TipBlock(SL("editMode.lbl.dragPlate")));

        grip.PointerPressed += (_, e) =>
        {
            _dragGrip = grip;
            _dragPlate = plate;
            _dragPlateIsMode = mode;
            _gripMoved = false;
            var at = e.GetCurrentPoint(PreviewLayoutGrid).Position;
            var origin = PlateOrigin(plate);
            _dragPlateAt = origin;
            _dragPlateGrab = new Point(at.X - origin.X, at.Y - origin.Y);
            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_dragGrip != grip || _dragPlate is null) return;
            var at = e.GetCurrentPoint(PreviewLayoutGrid).Position;
            var (x, y) = ClampPlate(_dragPlate, at.X - _dragPlateGrab.X, at.Y - _dragPlateGrab.Y);
            _dragPlate.HorizontalAlignment = HorizontalAlignment.Left;
            _dragPlate.VerticalAlignment = VerticalAlignment.Top;
            _dragPlate.Margin = new Thickness(x, y, 0, 0);
            // What the drag WORKED OUT, kept as it goes. Reading the position back
            // off the screen at the end asks the visual tree a question it cannot
            // always answer - and the answer it gives when it cannot is the
            // top-left corner, which is where the plate then went.
            _dragPlateAt = new Point(x, y);
            _gripMoved = true;
            e.Handled = true;
        };
        void Drop(PointerRoutedEventArgs e)
        {
            if (_dragGrip != grip || _dragPlate is null) return;
            grip.ReleasePointerCapture(e.Pointer);
            bool moved = _gripMoved;
            var at = _dragPlateAt;
            _dragGrip = null;
            _dragPlate = null;
            _gripMoved = false;
            // A press that went nowhere is not a move: it must not write a position.
            if (!moved) return;
            if (_dragPlateIsMode) { _settings.ModePlateX = at.X; _settings.ModePlateY = at.Y; }
            else { _settings.ToolPlateX = at.X; _settings.ToolPlateY = at.Y; }
            _settings.Save();
        }
        grip.PointerReleased += (_, e) => Drop(e);
        grip.PointerCaptureLost += (_, e) => Drop(e);
        return grip;
    }

    private CursorGrid? _dragGrip;
    private Border? _dragPlate;
    private bool _dragPlateIsMode;
    private bool _gripMoved;
    private Point _dragPlateGrab;
    private Point _dragPlateAt;

    // ── The settings page ───────────────────────────────────────────────────

    private UIElement BuildEditModeSettings()
    {
        // The page is rebuilt whenever the language changes; the redraw actions of
        // the pictures that went with the old one belong to controls nobody can
        // see any more.
        _anchorScreens.Clear();
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("editMode"));

        panel.Children.Add(SubHeader(SL("editMode.sec.modePlate")));
        panel.Children.Add(MakeCard("\uE8B9", SL("editMode.cards.modePos.title"),
            SL("editMode.cards.modePos.desc"), null, PlacementEditor(mode: true)));
        panel.Children.Add(MakeCard("\uE745", SL("editMode.cards.modeDir.title"),
            SL("editMode.cards.modeDir.desc"), OrientationBox(mode: true)));

        panel.Children.Add(SubHeader(SL("editMode.sec.toolPlate")));
        panel.Children.Add(MakeCard("\uE8B9", SL("editMode.cards.toolPos.title"),
            SL("editMode.cards.toolPos.desc"), null, PlacementEditor(mode: false)));
        panel.Children.Add(MakeCard("\uE745", SL("editMode.cards.toolDir.title"),
            SL("editMode.cards.toolDir.desc"), OrientationBox(mode: false)));

        panel.Children.Add(SubHeader(SL("editMode.sec.elementPlate")));
        panel.Children.Add(MakeCard("\uE8A1", SL("editMode.cards.elementSide.title"),
            SL("editMode.cards.elementSide.desc"), null, ElementSideEditor()));

        return panel;
    }

    private ComboBox OrientationBox(bool mode)
    {
        bool horizontal = mode ? _settings.ModePlateHorizontal : _settings.ToolPlateHorizontal;
        var box = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = SA("editMode.opt.orientation"),
            SelectedIndex = horizontal ? 1 : 0,
        };
        box.SelectionChanged += (_, _) =>
        {
            bool wide = box.SelectedIndex == 1;
            if (mode) _settings.ModePlateHorizontal = wide; else _settings.ToolPlateHorizontal = wide;
            _settings.Save();
            ApplyPlatePlacement();
        };
        return box;
    }

    /// <summary>
    /// Fixed or free, with both sets of sub-options shown at all times — greyed
    /// out rather than hidden, so what the other choice offers can be read before
    /// it is made, and the card never changes height under the pointer.
    /// </summary>
    private FrameworkElement PlacementEditor(bool mode)
    {
        string group = mode ? "ModePlatePlacement" : "ToolPlatePlacement";
        bool free = mode ? _settings.ModePlateFree : _settings.ToolPlateFree;

        var fixedChoice = new RadioButton { GroupName = group, Content = SL("editMode.lbl.fixed"), IsChecked = !free };
        var freeChoice = new RadioButton { GroupName = group, Content = SL("editMode.lbl.free"), IsChecked = free };

        var screen = AnchorScreen(mode, () => fixedChoice.IsChecked = true);

        bool locked = mode ? _settings.ModePlateLocked : _settings.ToolPlateLocked;
        string lockGroup = group + "Lock";
        var unlocked = new RadioButton { GroupName = lockGroup, Content = SL("editMode.lbl.unlocked"), IsChecked = !locked };
        var lockedChoice = new RadioButton { GroupName = lockGroup, Content = SL("editMode.lbl.locked"), IsChecked = locked };
        void SetLock(bool on)
        {
            if (mode) _settings.ModePlateLocked = on; else _settings.ToolPlateLocked = on;
            _settings.Save();
            ApplyPlatePlacement();
        }
        unlocked.Checked += (_, _) => SetLock(false);
        lockedChoice.Checked += (_, _) => SetLock(true);

        var lockBox = new StackPanel { Margin = new Thickness(26, 2, 0, 0), Spacing = 2 };
        lockBox.Children.Add(new TextBlock
        {
            Text = SL("editMode.lbl.handles"), FontSize = 12, Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
        });
        lockBox.Children.Add(unlocked);
        lockBox.Children.Add(lockedChoice);

        void Enable()
        {
            // Both halves stay on the card, whichever one is live: greyed out
            // rather than hidden, so what the other choice offers can be read
            // before it is made and the card never changes height under the
            // pointer. IsEnabled belongs to Control; a Border and a StackPanel
            // are not Controls, so being unreachable IS being disabled here.
            bool isFree = freeChoice.IsChecked == true;
            screen.IsHitTestVisible = !isFree;
            screen.Opacity = isFree ? 0.4 : 1;
            lockBox.IsHitTestVisible = isFree;
            lockBox.Opacity = isFree ? 1 : 0.4;
        }
        fixedChoice.Checked += (_, _) =>
        {
            if (mode) _settings.ModePlateFree = false; else _settings.ToolPlateFree = false;
            _settings.Save(); ApplyPlatePlacement(); Enable(); RedrawAnchorScreens();
        };
        freeChoice.Checked += (_, _) =>
        {
            if (mode) _settings.ModePlateFree = true; else _settings.ToolPlateFree = true;
            // Only one of them pinned is no longer a shared spot: the other plate's
            // picture has a split button in it that has to go away.
            _settings.Save(); ApplyPlatePlacement(); Enable(); RedrawAnchorScreens();
        };

        var box = new StackPanel { Spacing = 4 };
        box.Children.Add(fixedChoice);
        box.Children.Add(new Border { Margin = new Thickness(26, 2, 0, 6), Child = screen, HorizontalAlignment = HorizontalAlignment.Left });
        box.Children.Add(freeChoice);
        box.Children.Add(lockBox);
        Enable();
        return box;
    }

    // Both pictures are redrawn whenever either plate moves: the two of them
    // landing on the same square is what splits a button in half, and that is a
    // fact about the pair, not about one of them.
    private readonly List<Action> _anchorScreens = new();

    private void RedrawAnchorScreens()
    {
        foreach (var redraw in _anchorScreens.ToList()) redraw();
    }

    // A picture of the preview with a button in each of its eight places: the
    // position is chosen by pointing at it rather than by reading a list of
    // compass directions.
    //
    // The square the OTHER plate is pinned to is split in two, top half and
    // bottom half, taking between them exactly the room one button had. That is
    // where they would have sat on top of each other; picking a half says which
    // one goes above. Choosing a half for this plate necessarily gives the other
    // half to the other plate - there are two of them and two halves.
    private FrameworkElement AnchorScreen(bool mode, Action pinned)
    {
        var grid = new Grid { Width = 236, Height = 132, Padding = new Thickness(8) };
        for (int i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        void Pick(string key, bool? first)
        {
            if (mode) _settings.ModePlateAnchor = key; else _settings.ToolPlateAnchor = key;
            // Pointing at a place is also how the fixed position is chosen.
            if (mode) _settings.ModePlateFree = false; else _settings.ToolPlateFree = false;
            if (first is { } top) _settings.ModePlateFirst = mode ? top : !top;
            pinned();
            _settings.Save();
            ApplyPlatePlacement();
            RedrawAnchorScreens();
        }

        void Draw()
        {
            var on = (Style)Application.Current.Resources["AccentButtonStyle"];
            var off = (Style)Application.Current.Resources["DefaultButtonStyle"];
            string mine = PlateAnchor(mode);
            string theirs = PlateAnchor(!mode);
            bool bothPinned = !_settings.ModePlateFree && !_settings.ToolPlateFree;
            bool iAmFirst = mode ? _settings.ModePlateFirst : !_settings.ModePlateFirst;

            grid.Children.Clear();
            foreach (var key in PlateAnchors)
            {
                int column = key.EndsWith("Left", StringComparison.Ordinal) ? 0
                           : key.EndsWith("Right", StringComparison.Ordinal) ? 2 : 1;
                int row = key.StartsWith("top", StringComparison.Ordinal) ? 0
                        : key.StartsWith("bottom", StringComparison.Ordinal) ? 2 : 1;

                FrameworkElement cell;
                if (bothPinned && key == theirs)
                {
                    // Two buttons of half the height, the same width, in the room
                    // of one - so the picture still reads as eight places.
                    var halves = new StackPanel { Spacing = 2 };
                    var top = Half(PlaceName(key) + " \u2014 " + SL("editMode.lbl.stackTop"),
                                   new CornerRadius(4, 4, 1, 1));
                    var bottom = Half(PlaceName(key) + " \u2014 " + SL("editMode.lbl.stackBottom"),
                                      new CornerRadius(1, 1, 4, 4));
                    top.Style = key == mine && iAmFirst ? on : off;
                    bottom.Style = key == mine && !iAmFirst ? on : off;
                    var captured = key;
                    top.Click += (_, _) => Pick(captured, first: true);
                    bottom.Click += (_, _) => Pick(captured, first: false);
                    halves.Children.Add(top);
                    halves.Children.Add(bottom);
                    cell = halves;
                }
                else
                {
                    var button = new Button
                    {
                        Width = 40, Height = 22, MinWidth = 0, Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(4),
                        Style = key == mine ? on : off,
                    };
                    ToolTipService.SetToolTip(button, TipBlock(PlaceName(key)));
                    var captured = key;
                    button.Click += (_, _) => Pick(captured, first: null);
                    cell = button;
                }

                cell.HorizontalAlignment = column == 0 ? HorizontalAlignment.Left
                                         : column == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                cell.VerticalAlignment = row == 0 ? VerticalAlignment.Top
                                       : row == 2 ? VerticalAlignment.Bottom : VerticalAlignment.Center;
                Grid.SetColumn(cell, column);
                Grid.SetRow(cell, row);
                grid.Children.Add(cell);
            }
        }

        Draw();
        _anchorScreens.Add(Draw);

        return new Border
        {
            // The preview's OWN brush, taken from the preview: PreviewSurfaceBrush
            // lives in the page's theme dictionaries, not the application's, and
            // asking Application.Current for it throws.
            Background = PreviewSurface.Background,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = grid,
        };
    }

    /// <summary>What one of the eight places is called, for the button that is it.</summary>
    private static string PlaceName(string anchor) => SL("editMode.pos." + anchor);

    private static Button Half(string tip, CornerRadius corners)
    {
        var button = new Button
        {
            Width = 40, Height = 10, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = corners,
        };
        ToolTipService.SetToolTip(button, TipBlock(tip));
        return button;
    }

    private FrameworkElement ElementSideEditor()
    {
        var below = MakeSideRadio(SL("editMode.lbl.below"), SL("editMode.lbl.belowInfo"),
                                  !_settings.ElementPlateAbove);
        var above = MakeSideRadio(SL("editMode.lbl.above"), SL("editMode.lbl.aboveInfo"),
                                  _settings.ElementPlateAbove);
        void Set(bool up)
        {
            _settings.ElementPlateAbove = up;
            _settings.Save();
            UpdateSelectionTools();
        }
        below.Checked += (_, _) => Set(false);
        above.Checked += (_, _) => Set(true);

        var box = new StackPanel { Spacing = 2 };
        box.Children.Add(below);
        box.Children.Add(above);
        box.Children.Add(new TextBlock
        {
            Text = SL("editMode.lbl.sideFallback"), FontSize = 12, Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        return box;
    }

    // Same shape as MakeInfoRadio, in its own group: two of those on one page
    // would answer each other.
    private static RadioButton MakeSideRadio(string label, string tooltip, bool isChecked)
    {
        var icon = new FontIcon { Glyph = "\uE946", FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(icon, new ToolTip
        {
            Content = new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
        });
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(icon);
        return new RadioButton { GroupName = "ElementPlateSide", Content = content, IsChecked = isChecked };
    }
}
