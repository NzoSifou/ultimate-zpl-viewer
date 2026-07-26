using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Display;

namespace Ultimate_ZPL_Viewer;

public sealed record MonitorInfo(
    string InterfaceId,       // stable per-connection id, used as the settings key
    string DeviceName,        // \\.\DISPLAYn
    string FriendlyName,      // human-readable
    int ResW, int ResH,       // current resolution in physical pixels
    int PosX, int PosY,       // top-left in virtual desktop (physical pixels)
    double? EdidDiagonalInches); // physical diagonal from EDID, null if unknown

// Physical screen metrics: monitor enumeration, real physical size (from EDID),
// and the pixel-density math used to render the preview at real-world size.
public static class DisplayMetrics
{
    // Standard monitor / laptop diagonals (inches). A manually entered size is
    // snapped to the nearest of these.
    public static readonly double[] StandardSizesInches =
    {
        10.1, 11.6, 12.3, 12.5, 13.3, 14, 15, 15.6, 16, 17, 17.3,
        18.5, 19, 19.5, 20, 21.5, 22, 23, 23.6, 23.8, 24, 25, 26, 27,
        28, 29, 30, 31.5, 32, 34, 35, 38, 40, 42, 43, 48, 49, 55, 65,
    };

    public static double SnapToStandard(double inches)
        => StandardSizesInches.OrderBy(s => Math.Abs(s - inches)).First();

    // Physical pixels per millimetre from a diagonal and the current resolution
    // (pixels are square, so horizontal density == diagonal density).
    public static double PxPerMmFromDiagonal(double diagInches, int resW, int resH)
    {
        if (diagInches <= 0 || resW <= 0 || resH <= 0) return 0;
        var diagPx = Math.Sqrt((double)resW * resW + (double)resH * resH);
        return diagPx / (diagInches * 25.4);
    }

    // The device interface path of the monitor a window sits on.
    public static string? GetMonitorInterfaceId(IntPtr hwnd)
    {
        var name = GetMonitorDeviceName(hwnd);
        return name is null ? null : GetInterfaceId(name);
    }

    private static string? GetMonitorDeviceName(IntPtr hwnd)
    {
        try
        {
            var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero) return null;
            var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            return GetMonitorInfo(hmon, ref mi) ? mi.szDevice : null;
        }
        catch { return null; }
    }

    private static string? GetInterfaceId(string deviceName)
    {
        var dd = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
        if (!EnumDisplayDevices(deviceName, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return null;
        return string.IsNullOrEmpty(dd.DeviceID) ? null : dd.DeviceID;
    }

    private static (int W, int H, int X, int Y) GetCurrentMode(string deviceName)
    {
        var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
        return EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm)
            ? ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, dm.dmPositionX, dm.dmPositionY)
            : (0, 0, 0, 0);
    }

    // Enumerates all active monitors with their resolution, name and EDID diagonal.
    public static async Task<List<MonitorInfo>> EnumerateMonitorsAsync()
    {
        var result = new List<MonitorInfo>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var (w, h, px, py) = GetCurrentMode(adapter.DeviceName);

            var mon = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            EnumDisplayDevices(adapter.DeviceName, 0, ref mon, 0);
            var iface = GetInterfaceId(adapter.DeviceName);
            if (iface is null) continue;

            string name = mon.DeviceString;
            double? edidDiag = null;
            try
            {
                var dm = await DisplayMonitor.FromInterfaceIdAsync(iface);
                if (dm is not null)
                {
                    if (!string.IsNullOrWhiteSpace(dm.DisplayName)) name = dm.DisplayName;
                    edidDiag = EdidDiagonalInches(dm);
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(name)) name = $"Écran {result.Count + 1}";
            result.Add(new MonitorInfo(iface, adapter.DeviceName, name, w, h, px, py, edidDiag));
        }
        return result;
    }

    // The MonitorInfo for the monitor a window is currently on.
    public static async Task<MonitorInfo?> GetCurrentMonitorAsync(IntPtr hwnd)
    {
        var id = GetMonitorInterfaceId(hwnd);
        if (id is null) return null;
        var list = await EnumerateMonitorsAsync();
        return list.FirstOrDefault(m => m.InterfaceId == id);
    }

    private static double? EdidDiagonalInches(DisplayMonitor dm)
    {
        var phys = dm.PhysicalSizeInInches;
        if (phys.HasValue && phys.Value.Width > 0 && phys.Value.Height > 0)
            return Math.Sqrt(phys.Value.Width * phys.Value.Width + phys.Value.Height * phys.Value.Height);

        var native = dm.NativeResolutionInRawPixels;
        if (dm.RawDpiX > 1 && dm.RawDpiY > 1 && native.Width > 0 && native.Height > 0)
        {
            var wIn = native.Width / dm.RawDpiX;
            var hIn = native.Height / dm.RawDpiY;
            return Math.Sqrt(wIn * wIn + hIn * hIn);
        }
        return null;
    }

    // Physical pixels/mm for the monitor a window is on, using EDID; null when
    // the physical size is unavailable. Uses the current resolution.
    public static async Task<double?> GetPhysicalPxPerMmAsync(IntPtr hwnd)
    {
        var deviceName = GetMonitorDeviceName(hwnd);
        if (deviceName is null) return null;
        var iface = GetInterfaceId(deviceName);
        if (iface is null) return null;
        try
        {
            var dm = await DisplayMonitor.FromInterfaceIdAsync(iface);
            var diag = dm is null ? null : EdidDiagonalInches(dm);
            if (diag is double d)
            {
                var (w, h, _, _) = GetCurrentMode(deviceName);
                var pxmm = PxPerMmFromDiagonal(d, w, h);
                if (pxmm > 0) return pxmm;
            }
        }
        catch { }
        return null;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);
}
