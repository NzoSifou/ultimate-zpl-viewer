using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ultimate_ZPL_Viewer;

// ── Updater ─────────────────────────────────────────────────────────────────
// Asks GitHub whether a newer build exists, and installs it without sending the
// user to a browser. The deciding is in UpdateService; this file is the part the
// user sees: when to ask, what to show, and how not to be in the way.
public sealed partial class PreviewPage
{
    private bool _updateBusy;

    // The startup check. Quiet by design: it never reports being up to date and
    // never reports a failure, because nobody asked. Only an available update is
    // worth interrupting for — and only once per release, since a dismissed one
    // is remembered.
    private void ScheduleStartupUpdateCheck()
    {
        if (!_settings.CheckUpdatesOnStartup) return;
        if (OnboardingState.Load().Completed is false) return;
        // Only the first window, and only after the app has settled: an update
        // prompt on top of a window still drawing itself is jarring.
        if (WindowManager.Windows.Count > 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            DispatcherQueue.TryEnqueue(() => _ = CheckForUpdatesAsync(silent: true));
        });
    }

    /// <summary>
    /// Runs a check. <paramref name="silent"/> is the startup pass; the settings
    /// button passes false and then every outcome is reported, including "you are
    /// up to date", which is the whole point of pressing it.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            var check = await UpdateService.CheckAsync();
            _settings.LastUpdateCheck = DateTime.Now.ToString("s");
            _settings.Save();

            if (check.Error is not null)
            {
                if (!silent)
                    await ShowMessageAsync(SL("update.failed.title"),
                        SL("update.failed.desc").Replace("{error}", check.Error));
                return;
            }

            if (!check.Available)
            {
                if (!silent)
                    await ShowMessageAsync(SL("update.upToDate.title"),
                        SL("update.upToDate.desc").Replace("{version}", UpdateService.CurrentVersion()));
                return;
            }

            // A release the user chose to ignore stays ignored on startup, but a
            // deliberate check always shows it again — and the test switch ignores
            // the ignore list, or a single click would end the testing.
            if (silent && !UpdateService.ForceUpdatePrompt
                && string.Equals(_settings.SkippedUpdate, UpdateKey(check), StringComparison.OrdinalIgnoreCase))
                return;

            await ShowUpdateDialogAsync(check);
        }
        finally { _updateBusy = false; }
    }

    // What "this release" means for the ignore list: the asset's hash when we have
    // it (immune to renaming), the label otherwise.
    private static string UpdateKey(UpdateCheck check) => check.Asset?.Sha256 ?? check.Label;

    private async Task ShowUpdateDialogAsync(UpdateCheck check)
    {
        var body = new StackPanel { Spacing = 18, Width = 520 };

        // ── Header: the release, then everything secondary on one muted line ──
        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        header.Children.Add(new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = "",   // download
                FontSize = 20,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        });

        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = check.Label,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        var facts = new List<string>
        {
            SL("update.current").Replace("{version}", UpdateService.CurrentVersion()),
        };
        if (check.Asset is { Size: > 0 } sized) facts.Add(UpdateService.FormatSize(sized.Size));
        if (check.PublishedAt is { } when)
            facts.Add(SL("update.published").Replace("{date}", when.LocalDateTime.ToString("d MMMM yyyy")));
        titles.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", facts),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        body.Children.Add(header);

        // ── Release notes, presented as a fenced code block ──────────────────
        if (!string.IsNullOrWhiteSpace(check.Notes))
        {
            var notes = new StackPanel { Spacing = 6 };
            notes.Children.Add(new TextBlock
            {
                Text = SL("update.notes"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            notes.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 12, 14, 12),
                Child = new ScrollViewer
                {
                    MaxHeight = 240,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = PlainNotes(check.Notes),
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                        FontSize = 12,
                        LineHeight = 18,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            });
            body.Children.Add(notes);
        }

        // ── Progress, revealed in place once something starts ────────────────
        var progressText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0 };
        var progressPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        progressPanel.Children.Add(progressText);
        progressPanel.Children.Add(progressBar);
        body.Children.Add(progressPanel);

        // ── Four actions — one more than a ContentDialog can hold, so the footer
        //    is ours. The two that commit sit on the right, the one that walks away
        //    beside them, and "ignore" stays the quietest of the four.
        var footer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var skipButton = new Button
        {
            Content = SL("update.skip"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Padding = new Thickness(2, 6, 2, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        footer.Children.Add(skipButton);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var laterButton = new Button { Content = SL("update.later") };
        var onExitButton = new Button { Content = SL("update.onExit") };
        ToolTipService.SetToolTip(onExitButton, SL("update.onExitHint"));
        var installButton = new Button
        {
            Content = SL("update.install"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        actions.Children.Add(laterButton);
        if (check.Asset is not null) actions.Children.Add(onExitButton);
        actions.Children.Add(installButton);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        body.Children.Add(footer);

        // No built-in buttons: the dialog is only the frame around the layout above.
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title = SL("update.title"),
            Content = body,
        };
        // A ContentDialog is capped at 548 DIP by default, and four actions plus the
        // padding do not fit in that: the primary button was clipped at the edge.
        dialog.Resources["ContentDialogMaxWidth"] = 640d;

        string? downloaded = null;
        bool armForExit = false;
        var cancellation = new CancellationTokenSource();

        // Once something is under way, the four choices collapse to one exit.
        void LeaveOnly(string label)
        {
            skipButton.Visibility = Visibility.Collapsed;
            onExitButton.Visibility = Visibility.Collapsed;
            installButton.Visibility = Visibility.Collapsed;
            laterButton.Content = label;
        }

        skipButton.Click += (_, _) =>
        {
            _settings.SkippedUpdate = UpdateKey(check);
            _settings.Save();
            dialog.Hide();
        };
        laterButton.Click += (_, _) => { cancellation.Cancel(); dialog.Hide(); };

        // A release with no installer attached (it can carry anything): the page is
        // all that can honestly be offered.
        if (check.Asset is null)
        {
            installButton.Content = SL("update.openPage");
            installButton.Click += (_, _) => { OpenUrl(check.PageUrl); dialog.Hide(); };
        }
        else
        {
            installButton.Click += async (_, _) =>
            {
                progressPanel.Visibility = Visibility.Visible;
                LeaveOnly(SL("update.cancel"));
                var progress = new Progress<double>(fraction =>
                {
                    progressBar.Value = fraction;
                    progressText.Text = SL("update.downloading")
                        .Replace("{size}", UpdateService.FormatSize(check.Asset.Size))
                        + $"   {fraction * 100:0} %";
                });
                try
                {
                    downloaded = await UpdateService.DownloadAsync(check.Asset, progress, cancellation.Token);
                    // Handing over happens once this dialog is gone: closing the
                    // documents may need dialogs of its own, and WinUI allows only
                    // one at a time.
                    dialog.Hide();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    progressBar.Visibility = Visibility.Collapsed;
                    progressText.Text = SL("update.failed.desc").Replace("{error}", ex.Message);
                    laterButton.Content = SL("update.close");
                }
            };

            // "On exit": nothing may get in the way now. The download runs
            // unattended and only the closing of the application installs it.
            onExitButton.Click += (_, _) =>
            {
                armForExit = true;
                progressPanel.Visibility = Visibility.Visible;
                progressBar.Visibility = Visibility.Collapsed;
                progressText.Text = SL("update.armed");
                LeaveOnly(SL("update.close"));
            };
        }

        await ShowDialogAsync(dialog);

        if (downloaded is not null) { await HandOverToInstallerAsync(downloaded); return; }
        if (armForExit) _ = DownloadForExitAsync(check.Asset!);
    }

    /// <summary>
    /// Fetches the installer without a window of its own — the status strip carries
    /// it — and leaves it armed for the last window closing. A session that ends
    /// before the download does installs nothing and offers the release again next
    /// time, which beats holding a shutdown hostage to a progress bar.
    /// </summary>
    private async Task DownloadForExitAsync(ReleaseAsset asset)
    {
        var cancellation = new CancellationTokenSource();
        var job = BeginStatus(LocalizationService.Get("status.downloadingUpdate"), cancellation.Cancel);
        try
        {
            var progress = new Progress<double>(fraction => UpdateStatus(job, fraction));
            var path = await UpdateService.DownloadAsync(asset, progress, cancellation.Token);
            UpdateService.ArmForExit(path);
        }
        catch { }
        finally { EndStatus(job); }
    }

    /// <summary>
    /// Closes the application the way the user closing it would — every window gets
    /// its usual chance to save — then starts the installer. Cancelling any of those
    /// prompts cancels the update: the file stays downloaded, nothing is touched.
    /// </summary>
    private async Task HandOverToInstallerAsync(string setupPath)
    {
        foreach (var window in WindowManager.Windows.ToList())
            if (window.Page is { } page && !await page.PrepareAppCloseAsync(SL("update.closeTitle")))
                return;

        UpdateService.LaunchInstaller(setupPath);
        Application.Current.Exit();
    }

    /// <summary>
    /// Release notes are Markdown; this is not a Markdown renderer, it just takes
    /// the marks off so the text reads as text inside the code block.
    /// </summary>
    private static string PlainNotes(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);   // headings
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1", RegexOptions.Singleline);
        text = Regex.Replace(text, @"(?<!\w)[*_](?=\S)(.+?)(?<=\S)[*_](?!\w)", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");              // links
        text = Regex.Replace(text, @"^\s*(-{3,}|\*{3,}|_{3,})\s*$", "", RegexOptions.Multiline); // rules
        text = Regex.Replace(text, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
