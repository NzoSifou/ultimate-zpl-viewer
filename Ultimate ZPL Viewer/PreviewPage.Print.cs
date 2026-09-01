using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace Ultimate_ZPL_Viewer;

// The print flow: one dialog that shows what is about to come out of the printer
// next to the handful of settings that change it.
//
// It replaces a printer dropdown in the toolbar and a yes/no confirmation, neither
// of which showed the user what they were about to get.
public sealed partial class PreviewPage
{
    // ── Entry point ──────────────────────────────────────────────────────────

    private async Task StartPrintAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentText))
        {
            await ShowMessageAsync(SL("print.msg.title"), SL("print.msg.empty"));
            return;
        }

        var printers = GetInstalledPrinters().ToList();
        if (printers.Count == 0)
        {
            await ShowMessageAsync(SL("print.msg.title"), SL("print.msg.noPrinter"));
            return;
        }

        var job = DefaultJob(printers);

        // Quick print only holds while every default is a fixed value; the settings
        // screen keeps the switch off otherwise, and this is the second guard.
        if (_settings.QuickPrint && DefaultsAreFixed)
        {
            await RunPrintAsync(job);
            return;
        }

        var chosen = await ShowPrintDialogAsync(printers, job);
        if (chosen is not null) await RunPrintAsync(chosen);
    }

    private bool DefaultsAreFixed =>
        _settings.CopiesMode == "fixed" && _settings.LayoutMode == "fixed"
        && _settings.MarginsMode == "fixed" && _settings.PerPageMode == "fixed";

    // The values the dialog opens on: each one either a fixed default or whatever
    // the last print used.
    private PrintJob DefaultJob(IReadOnlyList<string> printers)
    {
        var wanted = _settings.DefaultPrinter == "last" ? _settings.LastPrinter : _settings.DefaultPrinter;
        var printer = printers.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                      ?? printers[0];

        return new PrintJob(
            printer,
            PrintJobService.ModeFor(_settings, printer),
            _settings.CopiesMode == "last" ? _settings.LastCopies : _settings.DefaultCopies,
            PrintJobService.LayoutFromKey(_settings.LayoutMode == "last" ? _settings.LastLayout : _settings.DefaultLayout),
            PaperSize: "",   // the printer own default until the dialog says otherwise
            _settings.MarginsMode == "last" ? _settings.LastMarginsMm : _settings.DefaultMarginsMm,
            _settings.PerPageMode == "last" ? _settings.LastPerPage : _settings.DefaultPerPage);
    }

    // ── The dialog ───────────────────────────────────────────────────────────

    private async Task<PrintJob?> ShowPrintDialogAsync(IReadOnlyList<string> printers, PrintJob initial)
    {
        if (XamlRoot is null) return null;

        var job = initial;
        double available = XamlRoot.Size.Width;
        double width = Math.Clamp(available - 120, 560, 1180);

        // 75 / 25: the preview is the point of the dialog, the settings only steer it.
        var grid = new Grid { ColumnSpacing = 20, Width = width, Height = Math.Clamp(XamlRoot.Size.Height - 260, 320, 620) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var previewHost = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
        };
        grid.Children.Add(previewHost);

        var right = new StackPanel { Spacing = 14 };
        var rightScroller = new ScrollViewer
        {
            Content = right,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(rightScroller, 1);
        grid.Children.Add(rightScroller);

        // ── Right column ────────────────────────────────────────────────────
        var printerBox = new ComboBox { ItemsSource = printers, HorizontalAlignment = HorizontalAlignment.Stretch };
        printerBox.SelectedItem = job.Printer;

        var modeBox = new ComboBox
        {
            ItemsSource = new[] { SL("print.send.raw"), SL("print.send.image") },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var copiesBox = new NumberBox
        {
            Minimum = 1, Maximum = 999, SmallChange = 1, Value = job.Copies,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var perPageBox = new NumberBox
        {
            Minimum = 1, Maximum = 20, SmallChange = 1, Value = job.PerPage,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var layoutBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var paperBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };

        // Margins are typed in whichever of the two units suits the user; the job
        // itself only ever carries millimetres.
        bool cm = _settings.MarginsUnit == "cm";
        var marginBox = new NumberBox
        {
            Minimum = 0, Maximum = 100, SmallChange = cm ? 0.5 : 1,
            Value = cm ? job.MarginsMm / 10.0 : job.MarginsMm,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var marginUnit = new ComboBox
        {
            ItemsSource = new[] { "mm", "cm" },
            SelectedIndex = cm ? 1 : 0,
            MinWidth = 74,
        };
        var marginRow = new Grid { ColumnSpacing = 6 };
        marginRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        marginRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        marginRow.Children.Add(marginBox);
        Grid.SetColumn(marginUnit, 1);
        marginRow.Children.Add(marginUnit);

        right.Children.Add(Field(SL("print.field.printer"), printerBox));
        right.Children.Add(Field(SL("print.field.send"), modeBox));
        right.Children.Add(Field(SL("print.field.copies"), copiesBox));
        right.Children.Add(Field(SL("print.field.perPage"), perPageBox));
        right.Children.Add(Field(SL("print.field.layout"), layoutBox));
        right.Children.Add(Field(SL("print.field.paper"), paperBox));
        right.Children.Add(Field(SL("print.field.margins"), marginRow));

        // ── Wiring ──────────────────────────────────────────────────────────
        bool loading = true;

        // Raw ZPL can express the copy count (^PQ) and an upside-down label (^POI),
        // and nothing else: there is no command to turn a label sideways or resize
        // it. Rather than accept a value and quietly drop it, the choices that the
        // language cannot carry are withdrawn on that road.
        void ApplyModeConstraints()
        {
            bool raw = job.Mode == SendMode.Raw;
            var layouts = raw
                ? new[] { PrintLayout.Portrait, PrintLayout.PortraitFlipped }
                : new[] { PrintLayout.Portrait, PrintLayout.Landscape,
                          PrintLayout.PortraitFlipped, PrintLayout.LandscapeFlipped };

            layoutBox.ItemsSource = layouts.Select(PrintJobService.NameOf).ToList();
            int index = Array.IndexOf(layouts, job.Layout);
            if (index < 0) { index = 0; job = job with { Layout = layouts[0] }; }
            layoutBox.SelectedIndex = index;
            layoutBox.Tag = layouts;

            var papers = raw ? new List<(string Name, double WMm, double HMm)>()
                             : PrintJobService.PaperSizes(job.Printer);
            paperBox.ItemsSource = papers.Select(p => string.Format("{0}  ({1:0.#} x {2:0.#} mm)", p.Name, p.WMm, p.HMm)).ToList();
            paperBox.Tag = papers;
            paperBox.IsEnabled = !raw && papers.Count > 0;
            if (papers.Count > 0)
            {
                int keep = papers.FindIndex(p => p.Name == job.PaperSize);
                if (keep < 0)
                {
                    var current = PrintJobService.PaperSizeMm(job.Printer);
                    keep = current is null ? 0 : Math.Max(0, papers.FindIndex(
                        p => Math.Abs(p.WMm - current.Value.W) < 0.6 && Math.Abs(p.HMm - current.Value.H) < 0.6));
                }
                paperBox.SelectedIndex = keep;
                job = job with { PaperSize = papers[keep].Name };
            }
            else job = job with { PaperSize = "" };

            marginBox.IsEnabled = !raw;
            marginUnit.IsEnabled = !raw;

            // A thermal printer feeds one label at a time; there is no sheet to
            // share, so repeating it on a page means nothing there.
            perPageBox.IsEnabled = !raw;
            if (raw && job.PerPage != 1)
            {
                job = job with { PerPage = 1 };
                perPageBox.Value = 1;
            }
        }

        void Refresh()
        {
            if (loading) return;
            previewHost.Child = BuildPrintPreview(job);
        }

        printerBox.SelectionChanged += (_, _) =>
        {
            if (printerBox.SelectedItem is not string p) return;
            job = job with { Printer = p, Mode = PrintJobService.ModeFor(_settings, p) };
            modeBox.SelectedIndex = job.Mode == SendMode.Raw ? 0 : 1;
            ApplyModeConstraints();
            Refresh();
        };
        modeBox.SelectionChanged += (_, _) =>
        {
            job = job with { Mode = modeBox.SelectedIndex == 0 ? SendMode.Raw : SendMode.Image };
            ApplyModeConstraints();
            Refresh();
        };
        copiesBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(copiesBox.Value)) return;
            job = job with { Copies = (int)Math.Clamp(copiesBox.Value, 1, 999) };
            Refresh();
        };
        layoutBox.SelectionChanged += (_, _) =>
        {
            if (layoutBox.Tag is PrintLayout[] set && layoutBox.SelectedIndex >= 0)
                job = job with { Layout = set[layoutBox.SelectedIndex] };
            Refresh();
        };
        perPageBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(perPageBox.Value)) return;
            job = job with { PerPage = (int)Math.Clamp(perPageBox.Value, 1, 20) };
            Refresh();
        };
        paperBox.SelectionChanged += (_, _) =>
        {
            if (paperBox.Tag is List<(string Name, double WMm, double HMm)> set
                && paperBox.SelectedIndex >= 0 && paperBox.SelectedIndex < set.Count)
                job = job with { PaperSize = set[paperBox.SelectedIndex].Name };
            Refresh();
        };
        void MarginsChanged()
        {
            if (double.IsNaN(marginBox.Value)) return;
            bool inCm = marginUnit.SelectedIndex == 1;
            job = job with { MarginsMm = Math.Max(0, inCm ? marginBox.Value * 10 : marginBox.Value) };
            Refresh();
        }
        marginBox.ValueChanged += (_, _) => MarginsChanged();
        marginUnit.SelectionChanged += (_, _) =>
        {
            // Changing the unit re-expresses the same distance, it does not change it.
            bool inCm = marginUnit.SelectedIndex == 1;
            _settings.MarginsUnit = inCm ? "cm" : "mm";
            _settings.Save();
            marginBox.SmallChange = inCm ? 0.5 : 1;
            marginBox.Value = inCm ? job.MarginsMm / 10.0 : job.MarginsMm;
        };

        modeBox.SelectedIndex = job.Mode == SendMode.Raw ? 0 : 1;
        ApplyModeConstraints();
        loading = false;
        Refresh();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title = SL("print.dialog.title"),
            Content = grid,
            PrimaryButtonText = SL("print.dialog.print"),
            CloseButtonText = SL("print.dialog.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = width + 120;

        // Enter must NOT print. Typing a number and pressing Enter to validate the
        // field is the natural gesture, and a NumberBox lets the key bubble on to
        // the dialog's default button: the job left for the printer, unprompted and
        // unrecoverable. The key still commits the field on its way through — it is
        // swallowed here, one level below the dialog, so only a real click prints.
        grid.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter) e.Handled = true;
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;
        PrintJobService.RememberMode(_settings, job.Printer, job.Mode);
        return job;
    }

    private static StackPanel Field(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(control);
        return panel;
    }

    // ── The preview column ───────────────────────────────────────────────────

    // What will actually come out. On the classic road that means the label filling
    // the sheet inside its margins, so the layout, the paper and the margins are
    // all visible as themselves rather than as numbers. On the thermal road the
    // printer decides the media, so there is no sheet to draw - only the label.
    private FrameworkElement BuildPrintPreview(PrintJob job)
    {
        // The rendered road prints a snapshot of the main preview, so it must be
        // shown the same way round here - including the Tourner rotation. The raw
        // road hands the printer the ZPL, which carries no such rotation, so it is
        // drawn upright whatever the preview is doing.
        double previewAngle = job.Mode == SendMode.Raw ? 0 : _rotationDegrees;
        var (labelWmm, labelHmm) = LabelSizeMm(previewAngle);
        var canvas = new Canvas();
        ZplRenderer.Draw(canvas, _model, SelectedDpmm, previewAngle);

        double flip = job.Layout is PrintLayout.PortraitFlipped or PrintLayout.LandscapeFlipped ? 180 : 0;
        var label = new Viewbox
        {
            Child = canvas,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = flip },
        };

        if (job.Mode == SendMode.Raw || PrintJobService.PaperSizeMm(job.Printer, job.PaperSize) is not { } paper)
        {
            return new Viewbox { Child = Sheet(label, labelWmm, labelHmm, null), Stretch = Stretch.Uniform };
        }

        bool sideways = job.Layout is PrintLayout.Landscape or PrintLayout.LandscapeFlipped;
        double pageW = sideways ? paper.H : paper.W;
        double pageH = sideways ? paper.W : paper.H;
        // The label fills what the margins leave - split into one cell per copy -
        // proportions kept. The cell maths comes from the printing code itself, so
        // this really is what will come out rather than a lookalike.
        double availW = Math.Max(1, pageW - 2 * job.MarginsMm);
        double availH = Math.Max(1, pageH - 2 * job.MarginsMm);
        var cells = PrintJobService.Cells((float)job.MarginsMm, (float)job.MarginsMm,
                                          (float)availW, (float)availH, job,
                                          (float)labelWmm, (float)labelHmm);

        // One millimetre is one unit here; the Viewbox around it does the fitting.
        var page = new Grid { Width = pageW, Height = pageH, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };

        // The margin boundary is drawn even when nothing overflows it, so moving the
        // setting always shows something - otherwise a small label would make the
        // field look broken.
        if (job.MarginsMm > 0.01)
        {
            page.Children.Add(new Rectangle
            {
                Width = availW,
                Height = availH,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                StrokeThickness = 0.4,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Fill = null,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            double factor = Math.Min(cell.Width / Math.Max(0.1, labelWmm),
                                     cell.Height / Math.Max(0.1, labelHmm));
            // The first cell holds the live render; the others show the same picture,
            // so the canvas is not rebuilt once per copy.
            FrameworkElement copy = i == 0 ? label : CloneLabel(previewAngle, flip);
            var placed = new Border
            {
                Child = copy,
                Width = labelWmm * factor,
                Height = labelHmm * factor,
                Margin = new Thickness(cell.X + (cell.Width - labelWmm * factor) / 2,
                                       cell.Y + (cell.Height - labelHmm * factor) / 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            page.Children.Add(placed);
        }

        var framed = new Border
        {
            Child = page,
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
        };
        return new Viewbox { Child = framed, Stretch = Stretch.Uniform };
    }

    // A second view of the same drawing. A Canvas can only have one parent, so a
    // repeated label is redrawn into its own canvas rather than shared.
    private FrameworkElement CloneLabel(double angle, double flip)
    {
        var canvas = new Canvas();
        ZplRenderer.Draw(canvas, _model, SelectedDpmm, angle);
        return new Viewbox
        {
            Child = canvas,
            Stretch = Stretch.Uniform,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = flip },
        };
    }

    private static FrameworkElement Sheet(FrameworkElement content, double wMm, double hMm, Brush? background)
        => new Border
        {
            Child = content,
            Width = Math.Max(1, wMm),
            Height = Math.Max(1, hMm),
            Background = background ?? new SolidColorBrush(Microsoft.UI.Colors.White),
        };

    // The label's real size in millimetres. A quarter turn swaps the two, so the
    // sheet is measured against what will actually be laid on it.
    private (double W, double H) LabelSizeMm(double angleDegrees)
    {
        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        double w = _model.Size.WidthDots / dpmm, h = _model.Size.HeightDots / dpmm;
        double a = ((angleDegrees % 360) + 360) % 360;
        bool quarterTurn = Math.Abs(a - 90) < 0.5 || Math.Abs(a - 270) < 0.5;
        return quarterTurn ? (h, w) : (w, h);
    }

    // ── Doing it ─────────────────────────────────────────────────────────────

    private async Task RunPrintAsync(PrintJob job)
    {
        try
        {
            if (job.Mode == SendMode.Raw)
            {
                var zpl = PrintJobService.BuildRawZpl(_currentText, job);
                await Task.Run(() => RawPrinterService.SendRaw(job.Printer, zpl, "Ultimate ZPL Viewer"));
            }
            else
            {
                var snapshot = await RenderSnapshotAsync();
                var (wMm, hMm) = LabelSizeMm(_rotationDegrees);
                await Task.Run(() => PrintJobService.PrintImage(job, snapshot, wMm, hMm));
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(SL("print.msg.failedTitle"),
                string.Format(SL("print.msg.failedBody"), job.Printer, ex.Message));
            return;
        }

        RememberJob(job);
        await ShowMessageAsync(SL("print.msg.title"),
            string.Format(SL("print.msg.sent"), job.Printer));
    }

    // Feeds the "reuse the last value" modes.
    private void RememberJob(PrintJob job)
    {
        _settings.LastPrinter = job.Printer;
        _settings.LastCopies = job.Copies;
        _settings.LastLayout = PrintJobService.KeyOf(job.Layout);
        _settings.LastMarginsMm = job.MarginsMm;
        _settings.LastPerPage = job.PerPage;
        _settings.Save();
        ApplyPrintButtonTooltip();
    }

    // ── Toolbar button ───────────────────────────────────────────────────────

    // With quick print on, the button acts without asking - so it says beforehand
    // what it is about to do.
    internal void ApplyPrintButtonTooltip()
    {
        if (_settings.QuickPrint && DefaultsAreFixed)
        {
            var printers = GetInstalledPrinters().ToList();
            if (printers.Count > 0)
            {
                var job = DefaultJob(printers);
                var perPage = job.PerPage > 1
                    ? ", " + string.Format(SL("print.perPage.summary"), job.PerPage) : "";
                ToolTipService.SetToolTip(PrintButton,
                    string.Format("{0}, x{1}{2}, {3}, {4}", job.Printer, job.Copies, perPage,
                                  PrintJobService.NameOf(job.Layout), MarginsLabel(job.MarginsMm)));
                return;
            }
        }
        ToolTipService.SetToolTip(PrintButton, LocalizationService.Get("toolbar.print"));
    }

    // The margin as the user would write it, in the unit they last chose.
    private string MarginsLabel(double mm)
    {
        if (mm <= 0.01) return SL("print.margins.none");
        return _settings.MarginsUnit == "cm"
            ? (mm / 10.0).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " cm"
            : mm.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + " mm";
    }

    // ── Settings section ─────────────────────────────────────────────────────

    private UIElement BuildPrintSettingsSection()
    {
        var panel = SettingsPanel();
        panel.Children.Add(LocalizedSettingsHeader("print"));

        var printers = GetInstalledPrinters().ToList();

        // Quick print first: it is the switch that decides whether the rest is even
        // consulted without asking.
        var quick = MakeToggle(_settings.QuickPrint);
        var quickCard = MakeCard("", SL("print.cards.quick.title"), SL("print.cards.quick.desc"), quick);
        quick.Toggled += (_, _) => { _settings.QuickPrint = quick.IsOn; _settings.Save(); ApplyPrintButtonTooltip(); };

        void RefreshQuickAvailability()
        {
            bool ok = DefaultsAreFixed;
            quick.IsEnabled = ok;
            if (!ok && quick.IsOn)
            {
                quick.IsOn = false;   // raises Toggled, which persists the change
            }
            ToolTipService.SetToolTip(quickCard, ok ? null : SL("print.cards.quick.blocked"));
            ApplyPrintButtonTooltip();
        }

        panel.Children.Add(quickCard);

        // Default printer (already existed).
        var printerBox = new ComboBox { MinWidth = 240 };
        printerBox.Items.Add(SL("print.lbl.lastPrinter"));
        foreach (var p in printers) printerBox.Items.Add(p);
        printerBox.SelectedIndex = _settings.DefaultPrinter == "last"
            ? 0 : Math.Max(0, printerBox.Items.IndexOf(_settings.DefaultPrinter));
        printerBox.SelectionChanged += (_, _) =>
        {
            _settings.DefaultPrinter = printerBox.SelectedIndex <= 0
                ? "last" : printerBox.SelectedItem?.ToString() ?? "last";
            _settings.Save();
            ApplyPrintButtonTooltip();
        };
        panel.Children.Add(MakeCard("", SL("print.cards.printer.title"),
            SL("print.cards.printer.desc"), printerBox));

        // The three dual-mode defaults.
        var copies = new NumberBox
        {
            Minimum = 1, Maximum = 999, SmallChange = 1, Value = _settings.DefaultCopies,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 96,
        };
        copies.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(copies.Value)) return;
            _settings.DefaultCopies = (int)Math.Clamp(copies.Value, 1, 999);
            _settings.Save(); ApplyPrintButtonTooltip();
        };
        panel.Children.Add(DualModeCard("", "copies", copies,
            () => _settings.CopiesMode, m => _settings.CopiesMode = m, RefreshQuickAvailability));

        var perPage = new NumberBox
        {
            Minimum = 1, Maximum = 20, SmallChange = 1, Value = _settings.DefaultPerPage,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 96,
        };
        perPage.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(perPage.Value)) return;
            _settings.DefaultPerPage = (int)Math.Clamp(perPage.Value, 1, 20);
            _settings.Save(); ApplyPrintButtonTooltip();
        };
        panel.Children.Add(DualModeCard("", "perPage", perPage,
            () => _settings.PerPageMode, m => _settings.PerPageMode = m, RefreshQuickAvailability));

        var layout = new ComboBox { MinWidth = 180 };
        var layoutValues = new[] { PrintLayout.Portrait, PrintLayout.Landscape,
                                   PrintLayout.PortraitFlipped, PrintLayout.LandscapeFlipped };
        layout.ItemsSource = layoutValues.Select(PrintJobService.NameOf).ToList();
        layout.SelectedIndex = Math.Max(0, Array.IndexOf(layoutValues,
            PrintJobService.LayoutFromKey(_settings.DefaultLayout)));
        layout.SelectionChanged += (_, _) =>
        {
            if (layout.SelectedIndex < 0) return;
            _settings.DefaultLayout = PrintJobService.KeyOf(layoutValues[layout.SelectedIndex]);
            _settings.Save(); ApplyPrintButtonTooltip();
        };
        panel.Children.Add(DualModeCard("", "layout", layout,
            () => _settings.LayoutMode, m => _settings.LayoutMode = m, RefreshQuickAvailability));

        // Margins are stored in millimetres whatever unit is on show.
        var margins = new NumberBox
        {
            Minimum = 0, Maximum = 100, SmallChange = _settings.MarginsUnit == "cm" ? 0.5 : 1,
            Value = _settings.MarginsUnit == "cm" ? _settings.DefaultMarginsMm / 10.0 : _settings.DefaultMarginsMm,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 96,
        };
        var marginsUnit = new ComboBox { ItemsSource = new[] { "mm", "cm" }, MinWidth = 74,
            SelectedIndex = _settings.MarginsUnit == "cm" ? 1 : 0 };
        void SaveMargins()
        {
            if (double.IsNaN(margins.Value)) return;
            bool inCm = marginsUnit.SelectedIndex == 1;
            _settings.DefaultMarginsMm = Math.Max(0, inCm ? margins.Value * 10 : margins.Value);
            _settings.Save(); ApplyPrintButtonTooltip();
        }
        margins.ValueChanged += (_, _) => SaveMargins();
        marginsUnit.SelectionChanged += (_, _) =>
        {
            bool inCm = marginsUnit.SelectedIndex == 1;
            _settings.MarginsUnit = inCm ? "cm" : "mm";
            margins.SmallChange = inCm ? 0.5 : 1;
            margins.Value = inCm ? _settings.DefaultMarginsMm / 10.0 : _settings.DefaultMarginsMm;
            _settings.Save(); ApplyPrintButtonTooltip();
        };
        var marginsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center };
        marginsRow.Children.Add(margins);
        marginsRow.Children.Add(marginsUnit);
        panel.Children.Add(DualModeCard("", "margins", marginsRow,
            () => _settings.MarginsMode, m => _settings.MarginsMode = m, RefreshQuickAvailability));

        RefreshQuickAvailability();
        return panel;
    }

    // A default that is either "whatever was used last" or a value typed here. The
    // value control is only reachable in the second mode - in the first there is
    // nothing to type, the last print decides.
    // A default that is either "whatever was used last" or a value typed here.
    //
    // Both sit in the card's control column, stacked and pinned right, so the pair
    // faces the description and stays centred against it - rather than the value
    // dropping underneath the whole text block, which is what a card's expanded
    // area does.
    //
    // In the first mode there is nothing to type, so the value control is not shown
    // at all rather than shown greyed: an empty disabled box is furniture that
    // invites a click it will refuse.
    private Border DualModeCard(string glyph, string key, FrameworkElement valueControl,
                                Func<string> get, Action<string> set, Action onChanged)
    {
        var mode = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = new[] { SL("print.mode.last"), SL("print.mode.fixed") },
            SelectedIndex = get() == "last" ? 0 : 1,
        };

        var valueHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = get() == "last" ? Visibility.Collapsed : Visibility.Visible,
        };
        valueHost.Children.Add(valueControl);

        var column = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        column.Children.Add(mode);
        column.Children.Add(valueHost);

        mode.SelectionChanged += (_, _) =>
        {
            set(mode.SelectedIndex == 0 ? "last" : "fixed");
            _settings.Save();
            valueHost.Visibility = mode.SelectedIndex == 0
                ? Visibility.Collapsed : Visibility.Visible;
            onChanged();
        };

        return MakeCard(glyph, SL("print.cards." + key + ".title"),
                        SL("print.cards." + key + ".desc"), column);
    }
}
