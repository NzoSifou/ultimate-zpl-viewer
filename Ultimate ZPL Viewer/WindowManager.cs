using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// Keeps track of every open document window. They all live in ONE process — that
/// is what lets a tab be dragged from one window into another, and what lets a
/// second launch hand its file over to the window already on screen instead of
/// starting a whole new instance.
/// </summary>
internal static class WindowManager
{
    private static readonly List<MainWindow> _windows = new();

    /// <summary>Open windows, oldest first.</summary>
    public static IReadOnlyList<MainWindow> Windows => _windows;

    /// <summary>The window that was activated last — where a new tab goes.</summary>
    public static MainWindow? Active { get; private set; }

    public static void Register(MainWindow window)
    {
        if (_windows.Contains(window)) return;
        _windows.Add(window);
        Active ??= window;
        window.Activated += (s, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && s is MainWindow w)
                Active = w;
        };
        window.Closed += (s, _) =>
        {
            if (s is not MainWindow w) return;
            _windows.Remove(w);
            if (ReferenceEquals(Active, w)) Active = _windows.LastOrDefault();
        };
    }

    /// <summary>Opens a window, optionally carrying a document straight into it.</summary>
    public static MainWindow Open(LaunchOptions options, DocTab? adopt = null)
    {
        var window = new MainWindow(options, adopt);
        window.Activate();
        return window;
    }

    /// <summary>
    /// Records which documents sit in which window, so the next launch comes back to
    /// the same arrangement. Called while the app is LIVE (a tab or a window
    /// appearing or disappearing) and never during shutdown: closing the windows one
    /// by one would otherwise whittle the layout down to whichever went last.
    /// </summary>
    public static void SaveSessionLayout()
    {
        if (_windows.Count == 0) return;
        try
        {
            var layout = _windows
                .Select(w => w.Page?.OpenFilePaths() ?? new List<string>())
                .Where(files => files.Count > 0)
                .ToList();
            var flat = layout.SelectMany(f => f).Distinct().ToList();

            var settings = AppSettings.Load();
            settings.WindowSessions = layout;
            settings.OpenFiles = flat;   // kept in step for the flat legacy list
            settings.Save();

            // Every page holds its OWN settings instance: without this, the stale copy
            // one of them saves on the way out would put the old arrangement back.
            foreach (var w in _windows) w.Page?.SyncSessionLayout(layout, flat);
        }
        catch { /* a session snapshot must never break the UI */ }
    }

    /// <summary>The window hosting a given XAML tree (dialogs need its handle).</summary>
    public static Window? ForXamlRoot(XamlRoot? root)
    {
        if (root is null) return Active;
        return _windows.FirstOrDefault(w => ReferenceEquals(w.Content?.XamlRoot, root)) ?? Active;
    }

    /// <summary>
    /// The window under a screen point, topmost first — used when a tab is dropped
    /// to decide whether it lands in another window or becomes one of its own.
    /// The point is in physical pixels, like the cursor position.
    /// </summary>
    public static MainWindow? AtScreenPoint(int x, int y, MainWindow? ignore = null)
    {
        MainWindow? best = null;
        int bestZ = int.MaxValue;
        foreach (var w in _windows)
        {
            if (ReferenceEquals(w, ignore)) continue;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            if (!GetWindowRect(hwnd, out var r)) continue;
            if (x < r.Left || x > r.Right || y < r.Top || y > r.Bottom) continue;
            int z = ZOrder(hwnd);
            if (z < bestZ) { bestZ = z; best = w; }
        }
        return best;
    }

    // Distance from the top of the z-order: lower means more in front.
    private static int ZOrder(IntPtr hwnd)
    {
        int z = 0;
        for (var h = hwnd; h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV)) z++;
        return z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
}
