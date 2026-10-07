using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// While a tab is dragged, a window that cannot be seen covers the screens
/// everywhere but over this app's own windows, and takes the drag. Without it the
/// pointer showed the no-entry sign as soon as it left the window (the desktop and
/// other apps refuse a tab) although letting go there does something: the tab
/// becomes a window of its own.
/// <para>
/// The window holds a XAML surface rather than a plain OLE drop target: the drag
/// image is drawn by XAML, and it only shows a caption a XAML target gives it -
/// over a plain target it wrote "Move", which is not what happens.
/// </para>
/// </summary>
internal static class TabDropCatcher
{
    private static IntPtr _hwnd;
    private static Microsoft.UI.Xaml.Hosting.DesktopWindowXamlSource? _island;
    private static bool _failed;
    private static string _caption = "";
    private static Action<int, int>? _onDrop;
    private static System.Threading.Timer? _watch;

    /// <param name="caption">What the drag image says outside the windows.</param>
    /// <param name="onDrop">Opens the new window, at the screen point given.</param>
    public static void Show(string caption, Action<int, int> onDrop)
    {
        if (_failed) return;
        try
        {
            if (_hwnd == IntPtr.Zero && !Create()) { _failed = true; return; }
            _caption = caption;
            _onDrop = onDrop;
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            // Holes over this app's windows: their own drop zones answer there.
            IntPtr region = CreateRectRgn(0, 0, w, h);
            foreach (var r in OwnWindows())
            {
                IntPtr hole = CreateRectRgn(r.Left - x, r.Top - y, r.Right - x, r.Bottom - y);
                CombineRgn(region, region, hole, RGN_DIFF);
                DeleteObject(hole);
            }
            SetWindowRgn(_hwnd, region, false);   // the window owns the region now

            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _island!.SiteBridge.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, w, h));
            WatchButton();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TabDropCatcher: {ex.Message}");
            _failed = true;
        }
    }

    public static void Hide()
    {
        _watch?.Dispose();
        _watch = null;
        _onDrop = null;
        if (_hwnd != IntPtr.Zero) ShowWindowAsync(_hwnd, SW_HIDE);
    }

    // The window must never outlive the drag: the button up for a moment means the
    // drag is over, whichever way it ended, and an invisible window left behind
    // would sit over the screens taking every click.
    private static void WatchButton()
    {
        _watch?.Dispose();
        int upTicks = 0;
        _watch = new System.Threading.Timer(_ =>
        {
            bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            upTicks = down ? 0 : upTicks + 1;
            if (upTicks >= 3 && _hwnd != IntPtr.Zero) ShowWindowAsync(_hwnd, SW_HIDE);
        }, null, 250, 250);
    }

    private static bool Create()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "", WS_POPUP, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return false;
        // Alpha 1, not 0: a fully transparent window is skipped by hit testing,
        // which is how the drop target under the pointer is found.
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);

        // A transparent background still counts for hit testing in XAML.
        var surface = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), AllowDrop = true };
        surface.DragOver += (_, e) =>
        {
            if (!e.DataView.Properties.ContainsKey(TabDragState.Key)) return;
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = _caption;
            e.Handled = true;
        };
        surface.Drop += (_, e) =>
        {
            if (!e.DataView.Properties.ContainsKey(TabDragState.Key)) return;
            e.Handled = true;
            var onDrop = _onDrop;
            GetCursorPos(out var p);
            Hide();
            onDrop?.Invoke(p.X, p.Y);
        };
        _island = new Microsoft.UI.Xaml.Hosting.DesktopWindowXamlSource();
        _island.Initialize(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));
        _island.Content = surface;
        return true;
    }

    private static List<RECT> OwnWindows()
    {
        var list = new List<RECT>();
        uint pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != _hwnd && IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner == pid && GetWindowRect(hwnd, out var r)) list.Add(r);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private const string ClassName = "UltimateZplViewer.TabDropCatcher";
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int RGN_DIFF = 4, SW_HIDE = 0, VK_LBUTTON = 1;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const uint LWA_ALPHA = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
