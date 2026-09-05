using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Ultimate_ZPL_Viewer;

// ── Bringing a picture onto the label ───────────────────────────────────────
// The tool is armed like the others and the click decides WHERE; everything that
// follows is about the one thing a picture cannot keep on a thermal printer — its
// shades. The dialog shows the label's own version of the image, in black and
// white at the size it will print, and every control redraws it, so the choice is
// made by looking rather than by guessing at a number.
//
// The image is embedded in the ZPL itself (^GFA). The file is read once and never
// referenced again: the label has to keep printing on a machine that has never
// heard of it.
public sealed partial class PreviewPage
{
    private static string IL(string key) => LocalizationService.Get("mode.image." + key);

    /// <summary>
    /// Asks for a file, asks how to convert it, and writes the graphic in at the
    /// point that was clicked.
    /// </summary>
    private async Task PlaceImageAsync(int x, int y, int draggedWidthDots)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, GetWindowHandle());
        foreach (var extension in ZplImageImport.Extensions) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        System.Drawing.Bitmap source;
        try { source = ZplImageImport.Load(file.Path); }
        catch
        {
            await ShowMessageAsync(IL("title"), IL("unreadable"));
            return;
        }

        using (source)
        {
            var field = await AskImageAsync(source, draggedWidthDots);
            if (field is null) return;
            InsertSnippet($"^FO{x.ToString(CultureInfo.InvariantCulture)}," +
                          $"{y.ToString(CultureInfo.InvariantCulture)}{field}^FS");
        }
    }

    // ── The dialog ──────────────────────────────────────────────────────────

    private async Task<string?> AskImageAsync(System.Drawing.Bitmap source, int draggedWidthDots)
    {
        double dpmm = SelectedDpmm > 0 ? SelectedDpmm : 8;
        double ratio = source.Height / (double)Math.Max(1, source.Width);
        var unit = _settings.Unit;

        // One dot per pixel is what ^GF does, so an untouched import prints at the
        // picture's own resolution — unless that would swallow the label, in which
        // case it opens at a size that leaves room for the rest of it.
        int labelWidth = (int)Math.Max(1, _model.Size.WidthDots);
        int width = draggedWidthDots > 4
            ? draggedWidthDots
            : Math.Min(source.Width, Math.Max(8, (int)(labelWidth * 0.4)));
        width = Math.Clamp(width, 1, ZplImageImport.MaxSide);
        int height = Math.Clamp((int)Math.Round(width * ratio), 1, ZplImageImport.MaxSide);

        bool dither = false, invert = false, locked = true;
        double threshold = 0.5;
        string field = "";

        var preview = new Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plate = new Border
        {
            Width = 300,
            Height = 220,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            Child = preview,
        };
        var info = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        // Redrawing is not free at label resolution, so the controls ask for a
        // redraw rather than doing one: a slider dragged across its range would
        // otherwise queue up a hundred conversions.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(90);
        timer.IsRepeating = false;

        void Render()
        {
            var mono = ZplImageImport.Convert(source, width, height, dither, threshold, invert);
            preview.Source = ToPreviewBitmap(mono);
            field = ZplImageImport.ToGraphicField(mono);
            info.Text = string.Format(CultureInfo.CurrentCulture, IL("info"),
                width, height, Weight(field.Length));
        }
        timer.Tick += (_, _) => Render();
        void Refresh() { timer.Stop(); timer.Start(); }

        bool filling = false;

        NumberBox Size(double dots, Action<double> apply)
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

        widthBox = Size(width, dots =>
        {
            width = Math.Clamp((int)Math.Round(dots), 1, ZplImageImport.MaxSide);
            if (locked)
            {
                height = Math.Clamp((int)Math.Round(width * ratio), 1, ZplImageImport.MaxSide);
                Show(heightBox!, height);
            }
            Refresh();
        });
        heightBox = Size(height, dots =>
        {
            height = Math.Clamp((int)Math.Round(dots), 1, ZplImageImport.MaxSide);
            if (locked)
            {
                width = Math.Clamp((int)Math.Round(height / Math.Max(ratio, 0.0001)), 1, ZplImageImport.MaxSide);
                Show(widthBox!, width);
            }
            Refresh();
        });

        var lockBox = new CheckBox { Content = IL("lock"), IsChecked = true, MinWidth = 0 };
        lockBox.Checked += (_, _) => locked = true;
        lockBox.Unchecked += (_, _) => locked = false;

        var slider = new Slider
        {
            Minimum = 5,
            Maximum = 95,
            Value = 50,
            StepFrequency = 1,
            Width = 140,
            IsThumbToolTipEnabled = true,
        };
        slider.ValueChanged += (_, _) => { threshold = slider.Value / 100.0; Refresh(); };

        var flat = new RadioButton { Content = IL("flat"), GroupName = "ZplImageMode", IsChecked = true };
        var diffuse = new RadioButton { Content = IL("dither"), GroupName = "ZplImageMode" };
        flat.Checked += (_, _) => { dither = false; Refresh(); };
        diffuse.Checked += (_, _) => { dither = true; Refresh(); };

        var invertBox = new CheckBox { Content = IL("invert"), MinWidth = 0 };
        invertBox.Checked += (_, _) => { invert = true; Refresh(); };
        invertBox.Unchecked += (_, _) => { invert = false; Refresh(); };

        var controls = new StackPanel { Spacing = 8, Width = 250 };
        controls.Children.Add(Row(IL("width"), widthBox));
        controls.Children.Add(Row(IL("height"), heightBox));
        controls.Children.Add(lockBox);
        controls.Children.Add(new TextBlock
        {
            Text = IL("rendering"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        controls.Children.Add(flat);
        controls.Children.Add(diffuse);
        controls.Children.Add(Row(IL("threshold"), slider));
        controls.Children.Add(invertBox);

        var body = new StackPanel { Spacing = 6 };
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        columns.Children.Add(plate);
        columns.Children.Add(controls);
        body.Children.Add(columns);
        body.Children.Add(info);

        Render();

        var dialog = CreateDialog(IL("title"), body, IL("add"), IL("cancel"));
        // A dialog is a column of text by default and clips anything wider. This one
        // is a picture NEXT TO its controls, which is the point of it — the preview
        // has to be readable while the settings that change it are being moved.
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;

        timer.Stop();
        // A tick may still have been pending: the picture that goes on the label is
        // the one the last change asked for, not the one that happened to be drawn.
        Render();
        return field;
    }

    /// <summary>Reads a byte count the way a person would say it.</summary>
    private static string Weight(int bytes) => bytes < 1024
        ? string.Format(CultureInfo.CurrentCulture, IL("bytes"), bytes)
        : string.Format(CultureInfo.CurrentCulture, IL("kilobytes"), Math.Round(bytes / 1024.0));

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
