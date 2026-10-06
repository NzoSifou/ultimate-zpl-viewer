using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// Timing log for chasing lags, off unless the ZV_PERF environment variable names
/// a file. Two things go in it: the scopes the code wraps (parse, draw, split…),
/// and every stall of the UI thread — a watchdog thread pings the dispatcher and
/// writes down how late the ping was answered, which catches layout and other
/// work no scope covers.
/// </summary>
internal static class PerfLog
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("ZV_PERF");
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static bool Enabled => LogPath is not null;

    public static Scope Time(string name) => new(Enabled ? name : null);

    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        public Scope(string? name) { _name = name; _start = name is null ? 0 : Clock.ElapsedTicks; }
        public void Dispose()
        {
            if (_name is null) return;
            double ms = (Clock.ElapsedTicks - _start) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 1) Write($"{ms,8:0.0} ms  {_name}");
        }
    }

    public static void Write(string line)
    {
        if (LogPath is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}\n", Encoding.UTF8); }
            catch { }
        }
    }

    private static DateTime? _processStart;

    /// <summary>A milestone, written down as the time since the process started.</summary>
    public static void Mark(string name)
    {
        if (!Enabled) return;
        _processStart ??= Process.GetCurrentProcess().StartTime;
        Write($"{(DateTime.Now - _processStart.Value).TotalMilliseconds,8:0} ms  @ {name}");
    }

    private static bool _watching;

    /// <summary>Starts the stall watchdog on the given UI thread's queue.</summary>
    public static void Watch(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        if (!Enabled || _watching) return;
        _watching = true;
        var thread = new Thread(() =>
        {
            var answered = new ManualResetEventSlim();
            while (true)
            {
                answered.Reset();
                long sent = Clock.ElapsedTicks;
                if (!queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, answered.Set)) return;
                answered.Wait();
                double late = (Clock.ElapsedTicks - sent) * 1000.0 / Stopwatch.Frequency;
                if (late >= 50) Write($"{late,8:0.0} ms  ** UI STALL **");
                Thread.Sleep(15);
            }
        }) { IsBackground = true, Name = "PerfLog watchdog" };
        thread.Start();
    }
}
