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

    // Every answer the print dialog would ask for has to be settled in advance,
    // the printer included: "the last one used" is not an answer on the first run
    // of a fresh installation, and printing straight to a printer nobody has named
    // is not something to do without asking.
    private bool DefaultsAreFixed =>
        HasNamedPrinter
        && _settings.CopiesMode == "fixed" && _settings.LayoutMode == "fixed"
        && _settings.MarginsMode == "fixed" && _settings.PerPageMode == "fixed";

    /// <summary>
    /// Whether the default printer names one that is actually there. A name is not
    /// enough on its own: a printer chosen once and unplugged since names nothing
    /// any more, and quick print would send a job into the dark.
    /// </summary>
    private bool HasNamedPrinter
    {
        get
        {
            var chosen = _settings.DefaultPrinter;
            if (string.IsNullOrWhiteSpace(chosen) || chosen == "last") return false;
            try
            {
                return GetInstalledPrinters()
                    .Any(p => string.Equals(p, chosen, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
    }

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
            DefaultMargins(_settings),
            _settings.PerPageMode == "last" ? _settings.LastPerPage : _settings.DefaultPerPage);
    }

    /// <summary>The margins a print starts from: the fixed default, or the last print's.</summary>
    internal static Margins DefaultMargins(AppSettings s) => s.MarginsMode == "last"
        ? Margins.From(s.LastMarginSidesMm, s.LastMarginsMm)
        : s.MarginsPerSide ? Margins.From(s.DefaultMarginSidesMm, s.DefaultMarginsMm)
                           : Margins.Uniform(s.DefaultMarginsMm);

    /// <summary>Whether the margins field opens on its four sides.</summary>
    private static bool DefaultMarginsPerSide(AppSettings s)
        => s.MarginsMode == "last" ? s.LastMarginSidesMm is not null : s.MarginsPerSide;

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
        // The sheet the preview draws on, from the list itself: asking the driver
        // for it at each refresh is what made the spin buttons drag.
        (double W, double H)? paperMm = null;
        var defaultPaper = new Dictionary<string, (double W, double H)?>();

        // Margins are typed in whichever of the two units suits the user; the job
        // itself only ever carries millimetres.
        var margins = new MarginEditor(_settings, job.Margins, DefaultMarginsPerSide(_settings));

        // The type is a guess until somebody settles it. This button settles it for
        // this printer, once: the combo then reads the answer rather than asking the
        // question again at every print. The same button takes it back, so the
        // choice is never a one-way door.
        var pinType = new HyperlinkButton { Padding = new Thickness(0, 2, 0, 0), FontSize = 12 };

        right.Children.Add(Field(SL("print.field.printer"), printerBox));
        var sendField = Field(SL("print.field.send"), modeBox);
        sendField.Children.Add(pinType);
        right.Children.Add(sendField);
        right.Children.Add(Field(SL("print.field.copies"), copiesBox));
        right.Children.Add(Field(SL("print.field.perPage"), perPageBox));
        right.Children.Add(Field(SL("print.field.layout"), layoutBox));
        right.Children.Add(Field(SL("print.field.paper"), paperBox));
        var marginsField = Field(SL("print.field.margins"), margins.Body);
        var marginsHeader = new Grid();
        var marginsLabel = marginsField.Children[0];
        marginsField.Children.RemoveAt(0);
        marginsHeader.Children.Add(marginsLabel);
        marginsHeader.Children.Add(margins.Switch);
        ((FrameworkElement)marginsLabel).VerticalAlignment = VerticalAlignment.Center;
        marginsField.Children.Insert(0, marginsHeader);
        right.Children.Add(marginsField);

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

            var papers = raw ? new List<Paper>() : PrintJobService.PaperSizes(job.Printer);
            var items = FillPaperBox(paperBox, papers);
            paperBox.IsEnabled = !raw && papers.Count > 0;
            if (papers.Count > 0)
            {
                var keep = papers.FirstOrDefault(p => p.Name == job.PaperSize);
                if (keep is null)
                {
                    var current = PrinterDefaultPaper(job.Printer);
                    keep = (current is { } c ? papers.FirstOrDefault(
                        p => Math.Abs(p.WMm - c.W) < 0.6 && Math.Abs(p.HMm - c.H) < 0.6) : null) ?? papers[0];
                }
                paperBox.SelectedItem = items.First(i => ReferenceEquals(i.Tag, keep));
                job = job with { PaperSize = keep.Name };
                paperMm = (keep.WMm, keep.HMm);
            }
            else
            {
                job = job with { PaperSize = "" };
                paperMm = raw ? null : PrinterDefaultPaper(job.Printer);
            }

            margins.IsEnabled = !raw;

            // A thermal printer feeds one label at a time; there is no sheet to
            // share, so repeating it on a page means nothing there.
            perPageBox.IsEnabled = !raw;
            if (raw && job.PerPage != 1)
            {
                job = job with { PerPage = 1 };
                perPageBox.Value = 1;
            }
        }

        // The printer's own default sheet, asked once per printer.
        (double W, double H)? PrinterDefaultPaper(string printer)
        {
            if (!defaultPaper.TryGetValue(printer, out var size))
                defaultPaper[printer] = size = PrintJobService.PaperSizeMm(printer);
            return size;
        }

        // One preview for the whole dialog: its drawings are kept and only moved
        // about, so a value stepped up and down redraws at most the copy it adds.
        var preview = new PrintPreview(this);
        void Refresh()
        {
            if (loading) return;
            using var perf = PerfLog.Time("print preview");
            var view = preview.Update(job, paperMm);
            if (!ReferenceEquals(previewHost.Child, view)) previewHost.Child = view;
        }

        // A pinned printer answers the question itself: the combo shows the answer
        // and refuses to be argued with, and the button offers to take it back.
        void ApplyPinLook()
        {
            bool pinned = PrintJobService.IsPinned(_settings, job.Printer);
            modeBox.IsEnabled = !pinned;
            pinType.Content = SL(pinned ? "print.lbl.clearDefault" : "print.lbl.setDefault");
            ToolTipService.SetToolTip(modeBox, pinned ? SL("print.lbl.pinned") : null);
        }
        pinType.Click += (_, _) =>
        {
            if (PrintJobService.IsPinned(_settings, job.Printer))
                PrintJobService.ForgetMode(_settings, job.Printer);
            else
                PrintJobService.RememberMode(_settings, job.Printer, job.Mode);
            ApplyPinLook();
        };

        printerBox.SelectionChanged += (_, _) =>
        {
            if (printerBox.SelectedItem is not string p) return;
            job = job with { Printer = p, Mode = PrintJobService.ModeFor(_settings, p) };
            modeBox.SelectedIndex = job.Mode == SendMode.Raw ? 0 : 1;
            ApplyPinLook();
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
        object? lastPaperItem = paperBox.SelectedItem;
        paperBox.SelectionChanged += (_, _) =>
        {
            if (paperBox.SelectedItem is ComboBoxItem { Tag: Paper p })
            {
                lastPaperItem = paperBox.SelectedItem;
                job = job with { PaperSize = p.Name };
                paperMm = (p.WMm, p.HMm);
                Refresh();
            }
            else if (paperBox.SelectedItem is not null && lastPaperItem is not null)
            {
                paperBox.SelectedItem = lastPaperItem;   // a heading is not a size
            }
        };
        margins.Changed += () =>
        {
            job = job with { Margins = margins.Value };
            Refresh();
        };

        modeBox.SelectedIndex = job.Mode == SendMode.Raw ? 0 : 1;
        ApplyPinLook();
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

        // WinUI caps a dialog at ~756 px high and clips whatever is below, with no
        // scrolling: on a large window the bottom of the settings column - the
        // margins' unit box - was cut off. The grid is already sized to the window.
        dialog.Resources["ContentDialogMaxHeight"] = Math.Max(756, XamlRoot.Size.Height - 40);

        // Enter must NOT print. Typing a number and pressing Enter to validate the
        // field is the natural gesture, and a NumberBox lets the key bubble on to
        // the dialog's default button: the job left for the printer, unprompted and
        // unrecoverable. The key still commits the field on its way through — it is
        // swallowed here, one level below the dialog, so only a real click prints.
        grid.KeyDown += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter) e.Handled = true;
        };

        // Printing does NOT pin the type. It used to, which made a one-off choice
        // permanent without saying so; the "set as default" button is now the only
        // thing that writes the mapping, and it says so before it does.
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;
        return job;
    }

    private static StackPanel Field(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(control);
        return panel;
    }

    // ── The paper list ───────────────────────────────────────────────────────

    // The sizes under headings, the most common first. A heading is an item that
    // cannot be picked; the sizes carry their Paper in Tag.
    private static List<ComboBoxItem> FillPaperBox(ComboBox box, List<Paper> papers)
    {
        var sizes = new List<ComboBoxItem>();
        var all = new List<object>();
        bool first = true;
        foreach (var (group, list) in PrintJobService.GroupPapers(papers))
        {
            all.Add(new ComboBoxItem
            {
                IsEnabled = false,
                IsTabStop = false,
                Padding = new Thickness(11, first ? 6 : 14, 11, 4),
                Content = new TextBlock
                {
                    Text = SL("print.paperGroup." + char.ToLowerInvariant(group.ToString()[0]) + group.ToString()[1..]),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                },
            });
            first = false;
            foreach (var p in list)
            {
                var item = new ComboBoxItem
                {
                    Tag = p,
                    // Some drivers write the size into the name already: once is enough.
                    Content = System.Text.RegularExpressions.Regex.IsMatch(p.Name, @"\d\s*[x×]\s*\d")
                        ? p.Name.Trim()
                        : string.Format("{0}  ({1:0.#} x {2:0.#} mm)", p.Name.Trim(), p.WMm, p.HMm),
                    Padding = new Thickness(23, 5, 11, 7),
                };
                sizes.Add(item);
                all.Add(item);
            }
        }
        box.ItemsSource = all;
        return sizes;
    }

    // ── The preview column ───────────────────────────────────────────────────

    // What will actually come out. On the classic road that means the label filling
    // the sheet inside its margins, so the layout, the paper and the margins are
    // all visible as themselves rather than as numbers. On the thermal road the
    // printer decides the media, so there is no sheet to draw - only the label.
    //
    // One instance serves the whole dialog. Drawing a label is what costs, and a
    // value stepped one at a time used to redraw the page from nothing - every copy
    // of the label included, the driver asked for the sheet size on top. The
    // drawings are now kept, one per copy shown, and each refresh only moves them:
    // going from five copies to six draws one label.
    private sealed class PrintPreview
    {
        private readonly PreviewPage _owner;
        private readonly List<Border> _copies = new();   // each holds its own drawing for good
        private double _angle = double.NaN;
        private readonly Grid _page = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
        private readonly Rectangle _marginFrame = new()
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            StrokeThickness = 0.4,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        private readonly Viewbox _pageView;

        public PrintPreview(PreviewPage owner)
        {
            _owner = owner;
            _page.Children.Add(_marginFrame);
            _pageView = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Child = new Border
                {
                    Child = _page,
                    BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                    BorderThickness = new Thickness(1),
                },
            };
        }

        public FrameworkElement Update(PrintJob job, (double W, double H)? paper)
        {
            // The rendered road prints a snapshot of the main preview, so it must be
            // shown the same way round here - including the Tourner rotation. The raw
            // road hands the printer the ZPL, which carries no such rotation, so it is
            // drawn upright whatever the preview is doing.
            double angle = job.Mode == SendMode.Raw ? 0 : _owner._rotationDegrees;
            if (angle != _angle)
            {
                foreach (var c in _copies) Detach(c);
                _copies.Clear();
                _angle = angle;
            }
            var (labelWmm, labelHmm) = _owner.LabelSizeMm(angle);
            double flip = job.Layout is PrintLayout.PortraitFlipped or PrintLayout.LandscapeFlipped ? 180 : 0;

            if (job.Mode == SendMode.Raw || paper is not { } sheet)
            {
                var only = Copy(0, flip);
                Detach(only);
                return new Viewbox { Child = Sheet(Unframed(only), labelWmm, labelHmm, null), Stretch = Stretch.Uniform };
            }

            bool sideways = job.Layout is PrintLayout.Landscape or PrintLayout.LandscapeFlipped;
            double pageW = sideways ? sheet.H : sheet.W;
            double pageH = sideways ? sheet.W : sheet.H;
            _page.Width = pageW;
            _page.Height = pageH;

            // The label fills what the margins leave - split into one cell per copy -
            // proportions kept. The cell maths comes from the printing code itself, so
            // this really is what will come out rather than a lookalike.
            var m = job.Margins;
            double availW = Math.Max(1, pageW - m.Left - m.Right);
            double availH = Math.Max(1, pageH - m.Top - m.Bottom);
            var cells = PrintJobService.Cells((float)m.Left, (float)m.Top, (float)availW, (float)availH,
                                              job, (float)labelWmm, (float)labelHmm);

            // The margin boundary is drawn even when nothing overflows it, so moving the
            // setting always shows something - otherwise a small label would make the
            // field look broken.
            _marginFrame.Visibility = m.IsZero ? Visibility.Collapsed : Visibility.Visible;
            _marginFrame.Width = availW;
            _marginFrame.Height = availH;
            _marginFrame.Margin = new Thickness(m.Left, m.Top, 0, 0);

            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                double factor = Math.Min(cell.Width / Math.Max(0.1, labelWmm),
                                         cell.Height / Math.Max(0.1, labelHmm));
                var copy = Copy(i, flip);
                if (!ReferenceEquals(copy.Parent, _page)) { Detach(copy); _page.Children.Add(copy); }
                copy.Width = labelWmm * factor;
                copy.Height = labelHmm * factor;
                copy.Margin = new Thickness(cell.X + (cell.Width - labelWmm * factor) / 2,
                                            cell.Y + (cell.Height - labelHmm * factor) / 2, 0, 0);
            }
            // Copies no longer shown wait off the page, drawing kept.
            for (int i = cells.Count; i < _copies.Count; i++) _page.Children.Remove(_copies[i]);

            return _pageView;
        }

        // The n-th copy, drawn the first time it is needed and kept from then on.
        private Border Copy(int index, double flip)
        {
            while (_copies.Count <= index)
            {
                var canvas = new Canvas();
                ZplRenderer.Draw(canvas, _owner._model, _owner.SelectedDpmm, _angle);
                _copies.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new Viewbox
                    {
                        Child = canvas,
                        Stretch = Stretch.Uniform,
                        RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(),
                    },
                });
            }
            var copy = _copies[index];
            ((RotateTransform)((Viewbox)copy.Child).RenderTransform).Angle = flip;
            return copy;
        }

        private static void Detach(FrameworkElement element)
        {
            if (element.Parent is Panel panel) panel.Children.Remove(element);
            else if (element.Parent is Border border) border.Child = null;
        }

        // The thermal road shows the label alone, at its own size.
        private static Border Unframed(Border copy)
        {
            copy.Width = double.NaN;
            copy.Height = double.NaN;
            copy.Margin = new Thickness(0);
            return copy;
        }
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
        // Between the click and the spooler taking the job there is a rasterisation
        // and, on the graphical path, one page composed per copy. Nothing to count,
        // so the strip carries the destination and no bar.
        var sending = BeginStatus(
            LocalizationService.Get("status.printing").Replace("{printer}", job.Printer));
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
        finally { EndStatus(sending); }

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
        // Four different sides are remembered as such; four equal ones are one margin.
        _settings.LastMarginsMm = job.Margins.Top;
        _settings.LastMarginSidesMm = job.Margins.IsUniform ? null : job.Margins.ToArray();
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
                                  PrintJobService.NameOf(job.Layout), MarginsLabel(job.Margins)));
                return;
            }
        }
        ToolTipService.SetToolTip(PrintButton, LocalizationService.Get("toolbar.print"));
    }

    // The margins as the user would write them, in the unit they last chose: one
    // number, or top / right / bottom / left.
    private string MarginsLabel(Margins m)
    {
        if (m.IsZero) return SL("print.margins.none");
        bool cm = _settings.MarginsUnit == "cm";
        string N(double mm) => (cm ? mm / 10.0 : mm).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        string unit = cm ? " cm" : " mm";
        return m.IsUniform
            ? N(m.Top) + unit
            : string.Format(SL("print.margins.sides"), N(m.Top), N(m.Right), N(m.Bottom), N(m.Left)) + unit;
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
            // Quick print depends on this one as much as on the four below it, and
            // nothing was telling it so: the switch was worked out once when the
            // page was built and never again, so whatever it said when the page
            // opened is what it went on saying.
            RefreshQuickAvailability();
            ApplyPrintButtonTooltip();
        };
        panel.Children.Add(MakeCard("", SL("print.cards.printer.title"),
            SL("print.cards.printer.desc"), printerBox));

        panel.Children.Add(PrinterTypeCard(printers));

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

        // Margins are stored in millimetres whatever unit is on show: one for the
        // four sides, or one per side.
        var margins = new MarginEditor(_settings,
            _settings.MarginsPerSide ? Margins.From(_settings.DefaultMarginSidesMm, _settings.DefaultMarginsMm)
                                     : Margins.Uniform(_settings.DefaultMarginsMm),
            _settings.MarginsPerSide, minBoxWidth: 96);
        margins.Changed += () =>
        {
            var value = margins.Value;
            _settings.MarginsPerSide = margins.PerSide;
            if (margins.PerSide) _settings.DefaultMarginSidesMm = value.ToArray();
            else _settings.DefaultMarginsMm = value.Top;
            _settings.Save(); ApplyPrintButtonTooltip();
        };
        var marginsRow = new StackPanel { Spacing = 8, Width = 300 };
        marginsRow.Children.Add(margins.Switch);
        marginsRow.Children.Add(margins.Body);
        panel.Children.Add(DualModeCard("", "margins", marginsRow,
            () => _settings.MarginsMode, m => _settings.MarginsMode = m, RefreshQuickAvailability));

        RefreshQuickAvailability();
        return panel;
    }


    // ── One type per printer ─────────────────────────────────────────────────

    // The type's name written to sit inside a bracket. The usual name for the
    // thermal road carries brackets of its own, and one pair inside another reads
    // as a typo.
    private static string PlainTypeName(SendMode mode)
        => SL(mode == SendMode.Raw ? "print.send.rawPlain" : "print.send.image");


    /// <summary>
    /// The type of every printer on the machine, listed. Windows never says "this
    /// one is a label printer", so the application reads the driver name and
    /// guesses; this is where a guess is overruled, printer by printer, and the
    /// answer then holds for every print.
    /// <para>
    /// Both choices stay on show and are greyed until a printer is picked, rather
    /// than appearing on selection: the card keeps one height, and what is on offer
    /// can be read before anything is chosen.
    /// </para>
    /// </summary>
    private FrameworkElement PrinterTypeCard(IReadOnlyList<string> printers)
    {
        string TypeLine(string printer)
        {
            var mode = PrintJobService.ModeFor(_settings, printer);
            var kind = SL(mode == SendMode.Raw ? "print.send.raw" : "print.send.image");
            return PrintJobService.IsPinned(_settings, printer)
                ? kind
                : string.Format(SL("print.types.detected"), kind);
        }

        FrameworkElement body;
        if (printers.Count == 0)
        {
            body = new TextBlock
            {
                Text = SL("print.msg.noPrinter"), Opacity = 0.6, FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            return MakeCard("", SL("print.cards.types.title"),
                            SL("print.cards.types.desc"), null, expanded: body);
        }

        // Single selection, no checkboxes: the WinUI list already marks the chosen
        // row with the accent bar down its left edge.
        var kinds = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 176 };
        foreach (var printer in printers)
        {
            var kind = new TextBlock
            {
                Text = TypeLine(printer), FontSize = 12, Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            kinds[printer] = kind;
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = printer, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(kind);
            list.Items.Add(new ListViewItem
            {
                Content = stack, Tag = printer,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }

        var frame = new Border
        {
            Child = list,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
        };

        var chosen = new TextBlock
        {
            Text = SL("print.types.pick"), Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2),
        };
        var rawChoice = new RadioButton
        {
            GroupName = "PrinterSendMode", Content = SL("print.send.raw"),
            IsEnabled = false, MinWidth = 0,
        };
        var imageChoice = new RadioButton
        {
            GroupName = "PrinterSendMode", Content = SL("print.send.image"),
            IsEnabled = false, MinWidth = 0,
        };
        // The third choice is the state the printer starts in, so it belongs beside
        // the other two rather than off to one side: it carries the guess in its own
        // label, which is the only place the guess is ever spelled out.
        var autoChoice = new RadioButton
        {
            GroupName = "PrinterSendMode", Content = SL("print.types.autoPlain"),
            IsEnabled = false, MinWidth = 0,
        };

        bool syncing = false;
        string? current = null;

        void ShowChoice()
        {
            chosen.Text = current ?? SL("print.types.pick");
            chosen.Opacity = current is null ? 0.6 : 1;
            chosen.FontWeight = current is null ? FontWeights.Normal : FontWeights.SemiBold;
            rawChoice.IsEnabled = imageChoice.IsEnabled = autoChoice.IsEnabled = current is not null;
            autoChoice.Content = current is null
                ? SL("print.types.autoPlain")
                : string.Format(SL("print.types.auto"), PlainTypeName(PrintJobService.DetectMode(current)));

            syncing = true;
            bool pinned = current is not null && PrintJobService.IsPinned(_settings, current);
            var mode = pinned ? PrintJobService.ModeFor(_settings, current!) : (SendMode?)null;
            rawChoice.IsChecked = mode == SendMode.Raw;
            imageChoice.IsChecked = mode == SendMode.Image;
            autoChoice.IsChecked = current is not null && !pinned;
            syncing = false;
        }

        void Pin(SendMode mode)
        {
            if (syncing || current is null) return;
            PrintJobService.RememberMode(_settings, current, mode);
            kinds[current].Text = TypeLine(current);
        }
        rawChoice.Checked += (_, _) => Pin(SendMode.Raw);
        imageChoice.Checked += (_, _) => Pin(SendMode.Image);
        autoChoice.Checked += (_, _) =>
        {
            if (syncing || current is null) return;
            PrintJobService.ForgetMode(_settings, current);
            kinds[current].Text = TypeLine(current);
        };
        list.SelectionChanged += (_, _) =>
        {
            current = (list.SelectedItem as ListViewItem)?.Tag as string;
            ShowChoice();
        };

        var choice = new StackPanel { Spacing = 2 };
        choice.Children.Add(chosen);
        choice.Children.Add(rawChoice);
        choice.Children.Add(imageChoice);
        choice.Children.Add(autoChoice);

        var stackBody = new StackPanel { Spacing = 12 };
        stackBody.Children.Add(frame);
        stackBody.Children.Add(choice);
        body = stackBody;

        return MakeCard("", SL("print.cards.types.title"),
                        SL("print.cards.types.desc"), null, expanded: body);
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
