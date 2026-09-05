using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Ultimate_ZPL_Viewer;

// ── Bringing a picture onto the label ───────────────────────────────────────
// The tool is armed like the others and the click decides WHERE; everything that
// follows happens in one window. It opens on an empty plate asking for a file,
// and once there is one it shows the LABEL'S version of it — black and white, at
// the size it will print — redrawn on every change. A picture cannot keep its
// shades on a thermal printer, so the choice is made by looking at what survives.
//
// The same window reopens from the "..." beside a selected graphic. Re-importing
// is the only way to change one: a ^GF carries pixels, not settings.
public sealed partial class PreviewPage
{
    private static string IL(string key) => LocalizationService.Get("mode.image." + key);

    // Everything the dialog decides. It is remembered per graphic so reopening
    // shows the settings that produced what is on the label, not a fresh guess.
    private sealed class ImageChoice
    {
        public System.Drawing.Bitmap? Source;
        public int Width = 1, Height = 1;
        public bool Dither;
        public bool Invert;
        public double Threshold = 0.5;
        public ZplImageImport.GraphicFormat Format = ZplImageImport.GraphicFormat.AcsHex;

        /// <summary>The width is settled — dragged out, or carried over from an
        /// earlier import — so the next picture must not overrule it.</summary>
        public bool SizeFixed;
    }

    // The file a graphic came from, kept for as long as the app runs so a second
    // pass at it starts from the real picture rather than from the dots left on the
    // label. Keyed by the ^GF payload itself: it changes when the graphic does, and
    // the entry is re-keyed with it.
    private readonly Dictionary<string, ImageChoice> _imageMemory = new();
    private const int RememberedImages = 8;

    private void InitImageEditing()
    {
        EditImageButton.Click += (_, _) => _ = EditImageAsync();
    }

    // ── Putting one down ────────────────────────────────────────────────────

    /// <summary>
    /// Marks the spot on the label and opens the import. Nothing is written until
    /// the window is accepted — including the file, which is asked for inside it.
    /// </summary>
    private async Task PlaceImageAsync(int x, int y, int draggedWidthDots, int draggedHeightDots)
    {
        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        bool dragged = draggedWidthDots > 4;
        var choice = new ImageChoice
        {
            Width = dragged ? draggedWidthDots : (int)Math.Round(DefaultBoxWidthMm * dpmm),
            Height = draggedHeightDots > 4 ? draggedHeightDots : (int)Math.Round(DefaultBoxHeightMm * dpmm),
            SizeFixed = dragged,
        };

        // A picture that has not been chosen yet still has a place on the label, and
        // the window that asks for it covers half the screen: the outline says where
        // the thing being discussed is going to land.
        ShowImagePlaceholder(x, y, choice.Width, choice.Height);
        try
        {
            var (field, made) = await ShowImageDialogAsync(choice, dpmm, editing: false);
            if (field is null) return;
            Remember(field, made);
            InsertSnippet($"^FO{x.ToString(CultureInfo.InvariantCulture)}," +
                          $"{y.ToString(CultureInfo.InvariantCulture)}{field}^FS");
        }
        finally { HideImagePlaceholder(); }
    }

    // ── Going back to one ───────────────────────────────────────────────────

    /// <summary>
    /// Reopens the import for the selected graphic and swaps the ^GF for what comes
    /// back. The ^FO beside it is untouched, so the picture stays where it was.
    /// </summary>
    private async Task EditImageAsync()
    {
        if (_selStart < 0 || _facts?.Graphic is null) return;
        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;

        var choice = RecallChoice(_facts.Graphic);
        if (choice is null) return;

        string graphic = _facts.Graphic;
        var (field, made) = await ShowImageDialogAsync(choice, dpmm, editing: true);
        if (field is null) return;

        var edit = ZplPatcher.SetGraphic(_currentText, _selStart, _selEnd, field);
        if (edit is not { } value) return;
        Forget(graphic);
        Remember(field, made);
        ApplyEdit(value);
        PreviewCursorHost.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The settings that made the selected graphic — from memory when this session
    /// imported it, and otherwise reconstructed from the dots on the label.
    /// </summary>
    private ImageChoice? RecallChoice(string graphicArgs)
    {
        if (_imageMemory.TryGetValue(Key(graphicArgs), out var remembered))
            return new ImageChoice
            {
                Source = remembered.Source,
                Width = remembered.Width,
                Height = remembered.Height,
                Dither = remembered.Dither,
                Invert = remembered.Invert,
                Threshold = remembered.Threshold,
                Format = remembered.Format,
                SizeFixed = true,
            };

        // No memory of it: the file is gone, or the document was opened from disk.
        // What is left is the printed bitmap, which is enough to resize, invert and
        // re-encode — and the plate still offers to pick a new file.
        var drawn = _hitMap.Values.OfType<ZplImage>()
            .FirstOrDefault(image => image.SourceStart == _selStart);
        if (drawn is null) return null;

        return new ImageChoice
        {
            Source = ZplImageImport.FromBits(drawn.PixelWidth, drawn.PixelHeight, drawn.Bits),
            Width = drawn.PixelWidth,
            Height = drawn.PixelHeight,
            Format = ZplImageImport.FormatOf(graphicArgs),
            SizeFixed = true,
        };
    }

    // What identifies a graphic is the picture, not how the command in front of it
    // was spelled: ^GF and ^GFA split their arguments differently, and both reach
    // here. Everything past the four leading parameters is the picture.
    private static string Key(string graphic)
    {
        int at = graphic.IndexOf("^GF", StringComparison.Ordinal);
        string rest = at >= 0 ? graphic[(at + 3)..] : graphic;
        var parts = rest.Split(',', 5);
        string payload = parts.Length >= 5 ? parts[4] : rest;
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private void Remember(string field, ImageChoice choice)
    {
        if (choice.Source is null) return;
        if (_imageMemory.Count >= RememberedImages)
            _imageMemory.Remove(_imageMemory.Keys.First());
        _imageMemory[Key(field)] = choice;
    }

    private void Forget(string graphicArgs) => _imageMemory.Remove(Key(graphicArgs));

    // ── The window ──────────────────────────────────────────────────────────

    private async Task<(string? Field, ImageChoice Choice)> ShowImageDialogAsync(
        ImageChoice choice, double dpmm, bool editing)
    {
        var unit = _settings.Unit;
        string field = "";
        double ratio = choice.Source is null
            ? 0
            : choice.Source.Height / (double)Math.Max(1, choice.Source.Width);

        // ── the left-hand plate ─────────────────────────────────────────────
        var preview = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var empty = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        empty.Children.Add(new FontIcon
        {
            Glyph = "",                      // an arrow into a tray: upload
            FontSize = 30,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
        });
        empty.Children.Add(new TextBlock
        {
            Text = IL("pick"),
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 200,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
        });

        var plateContent = new Grid();
        plateContent.Children.Add(empty);
        plateContent.Children.Add(preview);

        // The white card the picture sits on. It is inside the button rather than
        // being the button's own Background: a Button repaints that from its visual
        // states, and the plate would go grey the moment the pointer left it.
        var card = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(6),
            Child = plateContent,
        };

        // A button, not a picture: this plate is how a file gets chosen, both the
        // first time and every time after — clicking what is on it swaps it.
        var plate = new Button
        {
            Width = 300,
            Height = 220,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            // A button centres its content; this one IS its content, and a card
            // floating in the middle of a grey rectangle is not the plate we want.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = card,
        };

        var info = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        // ── redrawing ───────────────────────────────────────────────────────
        // Not free at label resolution, so the controls ask for a redraw rather than
        // doing one: a slider dragged across its range would otherwise queue up a
        // hundred conversions.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(90);
        timer.IsRepeating = false;

        var accept = new List<Control>();
        ContentDialog? shown = null;
        void Render()
        {
            bool has = choice.Source is not null;
            // Nothing to add until there is a picture — including through the
            // dialog's own button, which is the one people reach for first.
            if (shown is not null) shown.IsPrimaryButtonEnabled = has;
            empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            preview.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            info.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            foreach (var control in accept) control.IsEnabled = has;
            if (!editing) UpdatePlaceholderSize(choice.Width, choice.Height);
            if (!has) { field = ""; return; }

            var mono = ZplImageImport.Convert(choice.Source!, choice.Width, choice.Height,
                                              choice.Dither, choice.Threshold, choice.Invert);
            preview.Source = ToPreviewBitmap(mono);
            field = ZplImageImport.ToGraphicField(mono, choice.Format);
            info.Text = string.Format(CultureInfo.CurrentCulture, IL("info"),
                choice.Width, choice.Height, Weight(field.Length));
        }
        timer.Tick += (_, _) => Render();
        void Refresh() { timer.Stop(); timer.Start(); }

        // ── size ────────────────────────────────────────────────────────────
        bool filling = false;
        bool locked = true;

        NumberBox Size(int dots, Action<double> apply)
        {
            var box = new NumberBox
            {
                Value = Math.Round(UnitConverter.FromMillimeters(dots / dpmm, unit), 2),
                Minimum = 0.1,
                Maximum = UnitConverter.FromMillimeters(ZplImageImport.MaxSide / dpmm, unit),
                SmallChange = 1,
                LargeChange = 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                MinWidth = 120,
            };
            box.ValueChanged += (_, _) =>
            {
                if (filling || double.IsNaN(box.Value)) return;
                apply(Math.Max(1, UnitConverter.ToMillimeters(box.Value, unit) * dpmm));
            };
            return box;
        }

        NumberBox? widthBox = null, heightBox = null;
        void Show(NumberBox box, int dots)
        {
            filling = true;
            box.Value = Math.Round(UnitConverter.FromMillimeters(dots / dpmm, unit), 2);
            filling = false;
        }

        widthBox = Size(choice.Width, dots =>
        {
            choice.Width = Math.Clamp((int)Math.Round(dots), 1, ZplImageImport.MaxSide);
            if (locked && ratio > 0)
            {
                choice.Height = Math.Clamp((int)Math.Round(choice.Width * ratio), 1, ZplImageImport.MaxSide);
                Show(heightBox!, choice.Height);
            }
            Refresh();
        });
        heightBox = Size(choice.Height, dots =>
        {
            choice.Height = Math.Clamp((int)Math.Round(dots), 1, ZplImageImport.MaxSide);
            if (locked && ratio > 0)
            {
                choice.Width = Math.Clamp((int)Math.Round(choice.Height / ratio), 1, ZplImageImport.MaxSide);
                Show(widthBox!, choice.Width);
            }
            Refresh();
        });

        var lockBox = new CheckBox { Content = IL("lock"), IsChecked = true, MinWidth = 0 };
        lockBox.Checked += (_, _) => locked = true;
        lockBox.Unchecked += (_, _) => locked = false;

        // ── black and white ─────────────────────────────────────────────────
        var slider = new Slider
        {
            Minimum = 5,
            Maximum = 95,
            Value = Math.Round(choice.Threshold * 100),
            StepFrequency = 1,
            Width = 140,
            IsThumbToolTipEnabled = true,
        };
        slider.ValueChanged += (_, _) => { choice.Threshold = slider.Value / 100.0; Refresh(); };

        var flat = new RadioButton
        {
            Content = IL("flat"),
            GroupName = "ZplImageMode",
            IsChecked = !choice.Dither,
        };
        var diffuse = new RadioButton
        {
            Content = IL("dither"),
            GroupName = "ZplImageMode",
            IsChecked = choice.Dither,
        };
        flat.Checked += (_, _) => { choice.Dither = false; Refresh(); };
        diffuse.Checked += (_, _) => { choice.Dither = true; Refresh(); };

        var invertBox = new CheckBox
        {
            Content = IL("invert"),
            IsChecked = choice.Invert,
            MinWidth = 0,
        };
        invertBox.Checked += (_, _) => { choice.Invert = true; Refresh(); };
        invertBox.Unchecked += (_, _) => { choice.Invert = false; Refresh(); };

        // ── the encoding ────────────────────────────────────────────────────
        var hex = new RadioButton
        {
            Content = IL("fmtHex"),
            GroupName = "ZplImageFormat",
            IsChecked = choice.Format == ZplImageImport.GraphicFormat.AcsHex,
        };
        var z64 = new RadioButton
        {
            Content = IL("fmtZ64"),
            GroupName = "ZplImageFormat",
            IsChecked = choice.Format == ZplImageImport.GraphicFormat.Z64,
        };
        hex.Checked += (_, _) => { choice.Format = ZplImageImport.GraphicFormat.AcsHex; Refresh(); };
        z64.Checked += (_, _) => { choice.Format = ZplImageImport.GraphicFormat.Z64; Refresh(); };

        // ── laying it out ───────────────────────────────────────────────────
        var controls = new StackPanel { Spacing = 8, Width = 260 };
        controls.Children.Add(Row(IL("width"), widthBox));
        controls.Children.Add(Row(IL("height"), heightBox));
        controls.Children.Add(lockBox);
        controls.Children.Add(Heading(IL("rendering")));
        controls.Children.Add(flat);
        controls.Children.Add(diffuse);
        controls.Children.Add(Row(IL("threshold"), slider));
        controls.Children.Add(invertBox);
        controls.Children.Add(Heading(IL("fmtSection")));
        controls.Children.Add(WithInfo(hex, IL("fmtHexInfo"), null, null));
        controls.Children.Add(WithInfo(z64, IL("fmtZ64Info"), IL("fmtZ64Link"), Z64DocUrl));

        accept.AddRange(new Control[]
        {
            widthBox, heightBox, lockBox, slider, flat, diffuse, invertBox, hex, z64,
        });

        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        columns.Children.Add(plate);
        columns.Children.Add(controls);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(columns);
        body.Children.Add(info);

        var dialog = CreateDialog(IL(editing ? "editTitle" : "title"), body,
                                  IL(editing ? "apply" : "add"), IL("cancel"));
        // A dialog is a column of text by default and clips anything wider. This one
        // is a picture NEXT TO its controls, which is the point of it — the preview
        // has to be readable while the settings that change it are being moved.
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        shown = dialog;

        // Choosing a file, first time or fifth. The window stays open around it.
        plate.Click += async (_, _) =>
        {
            var loaded = await PickImageAsync();
            if (loaded is null) return;
            // Not disposed: the picture being replaced may be the one another
            // remembered import still points at.
            choice.Source = loaded;
            ratio = loaded.Height / (double)Math.Max(1, loaded.Width);

            // The first picture sizes itself: its own resolution, unless that would
            // swallow the label. A width already dragged out, or carried over from
            // the import being redone, is left alone — only the proportions follow.
            if (!choice.SizeFixed)
            {
                int labelWidth = (int)Math.Max(1, _model.Size.WidthDots);
                choice.Width = Math.Clamp(Math.Min(loaded.Width, Math.Max(8, (int)(labelWidth * 0.4))),
                                          1, ZplImageImport.MaxSide);
                choice.SizeFixed = true;
            }
            choice.Height = Math.Clamp((int)Math.Round(choice.Width * ratio), 1, ZplImageImport.MaxSide);
            Show(widthBox!, choice.Width);
            Show(heightBox!, choice.Height);
            Render();
        };

        Render();

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return (null, choice);

        timer.Stop();
        // A tick may still have been pending: what goes on the label is what the last
        // change asked for, not what happened to be drawn.
        Render();
        return (string.IsNullOrEmpty(field) ? null : field, choice);
    }

    // Zebra's own page on the ZB64 encodings, which is where the firmware question
    // is answered properly. Linked rather than paraphrased: what a given printer
    // accepts is Zebra's to say, and it changes.
    private const string Z64DocUrl =
        "https://docs.zebra.com/us/en/printers/software/zpl-pg/c-zpl-zb64-encoding-zb64-encoding-compression.html";

    private async Task<System.Drawing.Bitmap?> PickImageAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        foreach (var extension in ZplImageImport.Extensions) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;
        try { return ZplImageImport.Load(file.Path); }
        catch
        {
            await ShowMessageAsync(IL("title"), IL("unreadable"));
            return null;
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0),
    };

    // A choice with an "i" beside it. What separates the two encodings is not
    // visible in the preview — one is read by every printer ever made, the other is
    // four times smaller — so it has to be written down somewhere, and a line of
    // small print under each radio button would crowd out the picture.
    private static Grid WithInfo(RadioButton choice, string explanation, string? linkText, string? url)
    {
        var text = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var panel = new StackPanel { Spacing = 4, Width = 260 };
        panel.Children.Add(text);
        if (linkText is not null && url is not null)
            panel.Children.Add(new HyperlinkButton
            {
                Content = new TextBlock { Text = linkText, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                NavigateUri = new Uri(url),
                Padding = new Thickness(0),
            });

        var button = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Flyout = new Flyout { Content = panel },
        };
        ToolTipService.SetToolTip(button, explanation);

        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(choice, 0);
        Grid.SetColumn(button, 1);
        choice.VerticalAlignment = VerticalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(choice);
        grid.Children.Add(button);
        return grid;
    }

    /// <summary>Reads a byte count the way a person would say it.</summary>
    private static string Weight(int bytes) => bytes < 1024
        ? string.Format(CultureInfo.CurrentCulture, IL("bytes"), bytes)
        : string.Format(CultureInfo.CurrentCulture, IL("kilobytes"), Math.Round(bytes / 1024.0));

    // ── The outline on the label ────────────────────────────────────────────

    private Rectangle? _imagePlaceholder;
    private FontIcon? _imagePlaceholderIcon;
    private double _placeholderX, _placeholderY;

    private void ShowImagePlaceholder(int x, int y, int width, int height)
    {
        _placeholderX = x;
        _placeholderY = y;
        _imagePlaceholder ??= new Rectangle
        {
            Fill = null,
            IsHitTestVisible = false,
            StrokeDashArray = new DoubleCollection { 6, 4 },
        };
        _imagePlaceholder.Stroke = new SolidColorBrush(AccentColor());
        double zoom = PreviewScrollViewer.ZoomFactor;
        _imagePlaceholder.StrokeThickness = zoom > 0 ? Math.Max(0.5, 2 / zoom) : 2;

        _imagePlaceholderIcon ??= new FontIcon
        {
            Glyph = "",                      // a framed picture
            IsHitTestVisible = false,
        };
        _imagePlaceholderIcon.Foreground = new SolidColorBrush(AccentColor());

        foreach (UIElement element in new UIElement[] { _imagePlaceholder!, _imagePlaceholderIcon! })
            if (!PreviewCanvas.Children.Contains(element))
            {
                PreviewCanvas.Children.Add(element);
                Canvas.SetZIndex(element, 1002);
            }
        UpdatePlaceholderSize(width, height);
    }

    private void UpdatePlaceholderSize(int width, int height)
    {
        if (_imagePlaceholder is null || _imagePlaceholderIcon is null) return;
        _imagePlaceholder.Width = Math.Max(1, width);
        _imagePlaceholder.Height = Math.Max(1, height);
        Canvas.SetLeft(_imagePlaceholder, _placeholderX);
        Canvas.SetTop(_imagePlaceholder, _placeholderY);

        // The glyph is drawn in label dots like everything on this canvas, so it is
        // sized from the box rather than in points.
        double size = Math.Max(8, Math.Min(width, height) * 0.4);
        _imagePlaceholderIcon.FontSize = size;
        Canvas.SetLeft(_imagePlaceholderIcon, _placeholderX + (width - size) / 2);
        Canvas.SetTop(_imagePlaceholderIcon, _placeholderY + (height - size * 1.2) / 2);
    }

    private void HideImagePlaceholder()
    {
        foreach (var element in new FrameworkElement?[] { _imagePlaceholder, _imagePlaceholderIcon })
            if (element?.Parent is Canvas parent) parent.Children.Remove(element);
    }

    // ── The picture in the dialog ───────────────────────────────────────────

    // Black on white, at one screen pixel per dot — reduced by whole steps when the
    // graphic is larger than the plate, because a WriteableBitmap the size of a
    // 300 dpi label costs tens of megabytes and is rebuilt on every keystroke.
    private const int PreviewMaxSide = 900;

    private static WriteableBitmap ToPreviewBitmap(ZplImageImport.Mono mono)
    {
        int step = 1;
        while (mono.Width / step > PreviewMaxSide || mono.Height / step > PreviewMaxSide) step++;
        int w = Math.Max(1, mono.Width / step), h = Math.Max(1, mono.Height / step);

        var bitmap = new WriteableBitmap(w, h);
        var pixels = new byte[w * h * 4];
        int bytesPerRow = (mono.Width + 7) / 8;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = x * step, sy = y * step;
                int index = sy * bytesPerRow + sx / 8;
                bool black = index < mono.Bits.Length
                             && ((mono.Bits[index] >> (7 - (sx & 7))) & 1) == 1;
                int p = (y * w + x) * 4;
                byte value = black ? (byte)0 : (byte)255;
                pixels[p] = value; pixels[p + 1] = value; pixels[p + 2] = value; pixels[p + 3] = 255;
            }

        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        return bitmap;
    }
}
