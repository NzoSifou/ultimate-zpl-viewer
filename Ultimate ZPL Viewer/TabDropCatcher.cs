using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ultimate_ZPL_Viewer;

/// <summary>
/// While a tab is dragged, a window that cannot be seen covers the screens
/// everywhere but over this app's own windows, and accepts the drag as a move.
/// Without it the pointer showed the no-entry sign as soon as it left the window
/// (the desktop and other apps refuse a tab) although letting go there does
/// something: the tab becomes a window of its own. On the drop it answers
/// "nothing done", so TabView still raises TabDroppedOutside and the tab moves
/// out exactly as before.
/// </summary>
internal static class TabDropCatcher
{
    private static IntPtr _hwnd;
    private static DropTarget? _target;   // kept alive: OLE holds it as a raw COM pointer
    private static bool _failed;

    public static void Show()
    {
        if (_failed) return;
        try
        {
            if (_hwnd == IntPtr.Zero && !Create()) { _failed = true; return; }
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TabDropCatcher: {ex.Message}");
            _failed = true;
        }
    }

    public static void Hide()
    {
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE);
    }

    private static bool Create()
    {
        OleInitialize(IntPtr.Zero);   // already done on the UI thread: S_FALSE
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
        // which is how OLE finds the drop target under the pointer.
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);
        _target = new DropTarget();
        if (RegisterDragDrop(_hwnd, _target) < 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            return false;
        }
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

    [ComVisible(true)]
    private sealed class DropTarget : IDropTarget
    {
        public int DragEnter(IntPtr dataObject, uint keyState, POINTL pt, ref uint effect)
        { effect = DROPEFFECT_MOVE; return 0; }

        public int DragOver(uint keyState, POINTL pt, ref uint effect)
        { effect = DROPEFFECT_MOVE; return 0; }

        public int DragLeave() => 0;

        // Nothing done here: TabView then raises TabDroppedOutside, which opens
        // the tab in a window of its own where it was let go.
        public int Drop(IntPtr dataObject, uint keyState, POINTL pt, ref uint effect)
        { effect = DROPEFFECT_NONE; Hide(); return 0; }
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(IntPtr dataObject, uint keyState, POINTL pt, ref uint effect);
        [PreserveSig] int DragOver(uint keyState, POINTL pt, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IntPtr dataObject, uint keyState, POINTL pt, ref uint effect);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINTL { public int X, Y; }
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
    private const uint DROPEFFECT_NONE = 0, DROPEFFECT_MOVE = 2;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const int RGN_DIFF = 4, SW_HIDE = 0;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const uint LWA_ALPHA = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
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
