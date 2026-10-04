using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using System.Linq;
using Windows.System;

namespace Ultimate_ZPL_Viewer;

// ── Documents with several labels ───────────────────────────────────────────
// A file can hold one ^XA…^XZ format after another — a batch, a stream from a
// carrier's API — and a printer prints every one of them. The preview shows one
// at a time, like the pages of a PDF: a bar along the bottom of the preview with
// the two arrows, a track to jump anywhere in the batch, and the number of the
// label on screen, which can be typed. It is there only when there is more than
// one label to go through.
//
// The caret in the code leads as well: put it in the third label and the third
// label is what the preview shows.
public sealed partial class PreviewPage
{
    private Button? _labelPrev, _labelNext;
    private Slider? _labelSlider;
    private TextBox? _labelBox;
    private TextBlock? _labelTotal;
    private bool _fillingPager;

    private static string LabL(string key) => LocalizationService.Get("labels." + key);

    private void InitLabelPager()
    {
        Button Arrow(string glyph)
        {
            var b = new Button
            {
                Width = 32, Height = 30, MinWidth = 0, Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            };
            return b;
        }

        _labelPrev = Arrow("");
        _labelNext = Arrow("");
        _labelPrev.Click += (_, _) => GoToLabel(CurrentLabel - 1);
        _labelNext.Click += (_, _) => GoToLabel(CurrentLabel + 1);

        // A click anywhere on the track goes straight there — half-way along a
        // batch of ten is the fifth label — and dragging the thumb runs through
        // them. The value is the label number, as people count: from 1.
        _labelSlider = new Slider
        {
            Width = 220,
            Minimum = 1, Maximum = 2, Value = 1,
            StepFrequency = 1, SmallChange = 1, LargeChange = 1,
            SnapsTo = Microsoft.UI.Xaml.Controls.Primitives.SliderSnapsTo.StepValues,
            IsThumbToolTipEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        _labelSlider.ValueChanged += (_, e) =>
        {
            if (_fillingPager) return;
            GoToLabel((int)Math.Round(e.NewValue) - 1);
        };

        _labelBox = new TextBox
        {
            Width = 56, MinWidth = 0,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 5, 4, 5),
        };
        ToolTipService.SetToolTip(_labelBox, TipBlock(LabL("numberTip")));
        _labelBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case VirtualKey.Enter: CommitLabelBox(); e.Handled = true; break;
                case VirtualKey.Up: GoToLabel(CurrentLabel - 1); e.Handled = true; break;
                case VirtualKey.Down: GoToLabel(CurrentLabel + 1); e.Handled = true; break;
                case VirtualKey.Escape: FillLabelPager(); e.Handled = true; break;
            }
        };
        _labelBox.LostFocus += (_, _) => CommitLabelBox();
        _labelBox.GotFocus += (_, _) => _labelBox.SelectAll();

        _labelTotal = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(_labelPrev);
        row.Children.Add(_labelSlider);
        row.Children.Add(_labelNext);
        row.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 1, Height = 18, Margin = new Thickness(6, 0, 8, 0), Opacity = 0.5,
            Fill = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = LabL("label"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0), Opacity = 0.8,
        });
        row.Children.Add(_labelBox);
        row.Children.Add(_labelTotal);

        // The frame is in the XAML, where its brushes follow the theme.
        LabelPager.Child = row;
        LabelPager.SizeChanged += (_, _) => PlaceLabelPager();
        PreviewSurface.SizeChanged += (_, _) => PlaceLabelPager();

        // Page Up / Page Down turn the labels when the preview has the keyboard.
        PreviewCursorHost.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (_model.LabelCount < 2) return;
            if (e.Key == VirtualKey.PageDown) { GoToLabel(CurrentLabel + 1); e.Handled = true; }
            else if (e.Key == VirtualKey.PageUp) { GoToLabel(CurrentLabel - 1); e.Handled = true; }
            else if (e.Key == VirtualKey.Home && IsCtrlDown()) { GoToLabel(0); e.Handled = true; }
            else if (e.Key == VirtualKey.End && IsCtrlDown()) { GoToLabel(_model.LabelCount - 1); e.Handled = true; }
        }), true);
    }

    private static bool IsCtrlDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
               .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private int CurrentLabel => _model.LabelIndex;

    /// <summary>Shows another label of the document (0-based, clamped).</summary>
    private void GoToLabel(int index)
    {
        if (_activeTab is null || _model.LabelCount < 2) return;
        index = Math.Clamp(index, 0, _model.LabelCount - 1);
        if (index == _model.LabelIndex) { FillLabelPager(); return; }
        _activeTab.LabelIndex = index;
        // The selection belongs to the label that was on screen.
        ClearInspectSelection();
        // Labels of one batch can be different sizes: the new one is sized as if
        // it had just been opened.
        RefreshPreview(SizeUpdate.DocumentLoaded);
        RevealLabelInEditor();
    }

    private void CommitLabelBox()
    {
        if (_labelBox is null) return;
        if (int.TryParse(_labelBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n))
            GoToLabel(n - 1);
        else FillLabelPager();
    }

    /// <summary>Puts the bar in step with the model just drawn. Called after every redraw.</summary>
    private void UpdateLabelPager()
    {
        if (_activeTab is not null && _activeTab.LabelIndex != _model.LabelIndex)
            _activeTab.LabelIndex = _model.LabelIndex;   // asked past the end: clamped
        bool many = _model.LabelCount > 1 && !_homeVisible;
        LabelPager.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        if (many) FillLabelPager();
    }

    // Centred at the bottom — unless the preview is narrow (a split pane) and the
    // bar would run into the size caption in the corner: it then rises above it.
    private void PlaceLabelPager()
    {
        double w = PreviewSurface.ActualWidth;
        double barRight = (w + LabelPager.ActualWidth) / 2;
        double captionLeft = w - CaptionHost.ActualWidth - CaptionHost.Margin.Right;
        bool clash = barRight > captionLeft - 8;
        LabelPager.Margin = new Thickness(0, 0, 0, clash ? 14 + CaptionHost.ActualHeight + 10 : 14);
    }

    private void FillLabelPager()
    {
        if (_labelSlider is null || _labelBox is null || _labelTotal is null) return;
        _fillingPager = true;
        try
        {
            int count = Math.Max(2, _model.LabelCount);
            _labelSlider.Maximum = count;
            _labelSlider.Value = _model.LabelIndex + 1;
            if (_labelBox.FocusState == FocusState.Unfocused)
                _labelBox.Text = (_model.LabelIndex + 1).ToString(CultureInfo.CurrentCulture);
            _labelTotal.Text = "/ " + _model.LabelCount.ToString(CultureInfo.CurrentCulture);
            _labelPrev!.IsEnabled = _model.LabelIndex > 0;
            _labelNext!.IsEnabled = _model.LabelIndex < _model.LabelCount - 1;
            ToolTipService.SetToolTip(_labelPrev, TipBlock(LabL("previous")));
            ToolTipService.SetToolTip(_labelNext, TipBlock(LabL("next")));
        }
        finally { _fillingPager = false; }
    }

    /// <summary>
    /// The caret leads: moved into another label's code, it brings that label up.
    /// </summary>
    private void FollowCaretToLabel(int offset)
    {
        if (_activeTab is null || _model.LabelSpans.Count < 2) return;
        for (int i = 0; i < _model.LabelSpans.Count; i++)
        {
            var (start, end) = _model.LabelSpans[i];
            if (offset < start || offset > end) continue;
            if (i != _model.LabelIndex)
            {
                _activeTab.LabelIndex = i;
                ClearInspectSelection();
                RefreshPreview(SizeUpdate.DocumentLoaded);
            }
            return;
        }
    }

    // The code follows the other way too: the label just brought up is scrolled
    // into view in the editor, without moving the caret.
    private void RevealLabelInEditor()
    {
        if (_model.LabelIndex < 0 || _model.LabelIndex >= _model.LabelSpans.Count) return;
        PostToEditor("{\"type\":\"revealOffset\",\"offset\":" + _model.LabelSpans[_model.LabelIndex].Start + "}");
    }
}
