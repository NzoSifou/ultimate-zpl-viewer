using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ultimate_ZPL_Viewer;

// ── Status strip ────────────────────────────────────────────────────────────
// The one line at the bottom of the window. It exists only while something is
// worth reporting: no task, no strip, and no room taken.
//
// Several things can be under way at once — a file opening while an update
// downloads — so jobs are kept in a list and the strip shows the most recent.
// The others are not lost: whichever outlives the rest takes the line back.
public sealed partial class PreviewPage
{
    internal sealed class StatusJob
    {
        public string Text = "";
        public double? Progress;      // null → indeterminate
        public Action? Cancel;        // null → no cancel button
    }

    private readonly List<StatusJob> _statusJobs = new();

    private StatusJob BeginStatus(string text, Action? cancel = null)
    {
        var job = new StatusJob { Text = text, Cancel = cancel };
        _statusJobs.Add(job);
        RenderStatus();
        return job;
    }

    private void UpdateStatus(StatusJob job, double? progress = null, string? text = null)
    {
        if (!_statusJobs.Contains(job)) return;
        if (progress is not null) job.Progress = Math.Clamp(progress.Value, 0, 1);
        if (text is not null) job.Text = text;
        RenderStatus();
    }

    private void EndStatus(StatusJob job)
    {
        _statusJobs.Remove(job);
        RenderStatus();
    }

    private void RenderStatus()
    {
        var job = _statusJobs.LastOrDefault();
        if (job is null)
        {
            StatusStrip.Visibility = Visibility.Collapsed;
            _statusCancel = null;
            return;
        }

        StatusText.Text = job.Text;
        if (job.Progress is { } fraction)
        {
            StatusProgress.IsIndeterminate = false;
            StatusProgress.Value = fraction;
            StatusPercent.Text = $"{fraction * 100:0} %";
        }
        else
        {
            StatusProgress.IsIndeterminate = true;
            StatusPercent.Text = "";
        }
        _statusCancel = job.Cancel;
        StatusCancel.Visibility = job.Cancel is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(StatusCancel, LocalizationService.Get("status.cancel"));
        StatusStrip.Visibility = Visibility.Visible;
    }

    private Action? _statusCancel;

    private void StatusCancel_Click(object sender, RoutedEventArgs e) => _statusCancel?.Invoke();

    /// <summary>
    /// Hands the frame back so what was just put on the strip is actually painted.
    /// Work that runs on the UI thread — parsing, rendering, static analysis — never
    /// gives the strip a chance to appear on its own: it has to be posted behind the
    /// next frame, which is what the Low priority buys.
    /// </summary>
    private Task YieldToUiAsync()
    {
        var done = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => done.SetResult());
        return done.Task;
    }
}
