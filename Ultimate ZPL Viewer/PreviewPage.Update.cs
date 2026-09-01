using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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
        var body = new StackPanel { Spacing = 12, Width = 460 };

        body.Children.Add(new TextBlock
        {
            Text = SL("update.available").Replace("{version}", check.Label),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = SL("update.current").Replace("{version}", UpdateService.CurrentVersion()),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(check.Notes))
        {
            body.Children.Add(new TextBlock
            {
                Text = SL("update.notes"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = PlainNotes(check.Notes),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            });
        }

        // Filled in when the download starts; the dialog swaps to it in place
        // rather than opening a second window on top of the first.
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0 };
        var progressPanel = new StackPanel { Spacing = 12, Width = 460 };
        progressPanel.Children.Add(progressText);
        progressPanel.Children.Add(progressBar);

        var host = new ContentControl { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        string? downloaded = null;   // set once the file is on disk and verified

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = _settings.ToElementTheme(),
            Title = SL("update.title"),
            Content = host,
            PrimaryButtonText = check.Asset is null ? "" : SL("update.install"),
            SecondaryButtonText = SL("update.later"),
            CloseButtonText = SL("update.skip"),
            DefaultButton = ContentDialogButton.Primary,
        };

        // No installer attached to the release (a release can carry anything):
        // offer the page instead of a button that could not work.
        if (check.Asset is null)
        {
            dialog.PrimaryButtonText = SL("update.openPage");
            dialog.PrimaryButtonClick += (_, _) => OpenUrl(check.PageUrl);
        }
        else
        {
            var cancellation = new CancellationTokenSource();
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                var deferral = args.GetDeferral();
                args.Cancel = true;   // the dialog stays up and becomes the progress view
                try
                {
                    sender.PrimaryButtonText = "";
                    sender.SecondaryButtonText = "";
                    sender.CloseButtonText = SL("update.cancel");
                    progressText.Text = SL("update.downloading")
                        .Replace("{size}", UpdateService.FormatSize(check.Asset.Size));
                    host.Content = progressPanel;

                    var progress = new Progress<double>(fraction =>
                    {
                        progressBar.Value = fraction;
                        progressText.Text = SL("update.downloading")
                            .Replace("{size}", UpdateService.FormatSize(check.Asset.Size))
                            + $"  {fraction * 100:0} %";
                    });

                    downloaded = await UpdateService.DownloadAsync(check.Asset, progress, cancellation.Token);
                    // Handing over happens once this dialog is gone: closing the
                    // documents may need dialogs of its own, and WinUI allows only
                    // one at a time.
                    sender.Hide();
                }
                catch (OperationCanceledException) { sender.Hide(); }
                catch (Exception ex)
                {
                    progressBar.Visibility = Visibility.Collapsed;
                    progressText.Text = SL("update.failed.desc").Replace("{error}", ex.Message);
                    sender.CloseButtonText = SL("update.close");
                    sender.PrimaryButtonText = SL("update.openPage");
                    sender.IsPrimaryButtonEnabled = true;
                }
                finally { deferral.Complete(); }
            };
            dialog.Closing += (_, _) => cancellation.Cancel();
        }

        var result = await ShowDialogAsync(dialog);

        if (downloaded is not null) { await HandOverToInstallerAsync(downloaded); return; }

        // The close button is "ignore this release" only while it still says so:
        // once the download starts it becomes Cancel/Close, and ignoring would be
        // the wrong reading of the same click.
        if (result == ContentDialogResult.None
            && string.Equals(dialog.CloseButtonText, SL("update.skip"), StringComparison.Ordinal))
        {
            _settings.SkippedUpdate = UpdateKey(check);
            _settings.Save();
        }
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
    /// the marks off so the text reads as text.
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
