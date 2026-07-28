using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ultimate_ZPL_Viewer;

// Registers Ultimate ZPL Viewer (unpackaged) as a handler for .zpl files, all in
// HKCU (no admin needed):
//   - EnsureRegistered(): a ProgID + the "Applications\<exe>" entry so the app
//     shows up in the Explorer "Open with" list. Runs at every startup, idempotent.
//   - SetAsDefault():     points .zpl at our ProgID (best-effort; a pre-existing
//     Windows "UserChoice" would still win — .zpl usually has none).
//   - IsDefault():        whether .zpl currently opens with us.
public static class FileAssociationService
{
    private const string Ext    = ".zpl";
    private const string ProgId = "UltimateZplViewer.zpl";

    private static string ExePath => Environment.ProcessPath ?? "";
    private static string ExeName => Path.GetFileName(ExePath);
    private static string Command => $"\"{ExePath}\" \"%1\"";

    // SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, ...) — tells the shell the
    // associations changed so Explorer/Open-with refresh without a reboot.
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    // Idempotent registration so the app appears under "Open with" and can be set
    // as default. Re-runs at each startup to keep the exe path current.
    public static void EnsureRegistered()
    {
        if (string.IsNullOrEmpty(ExePath)) return;
        try
        {
            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progId.SetValue("", "Fichier ZPL");
                progId.SetValue("FriendlyTypeName", "Fichier ZPL");
                using (var icon = progId.CreateSubKey("DefaultIcon"))
                    icon.SetValue("", $"\"{ExePath}\",0");
                using (var cmd = progId.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", Command);
            }

            // Make the extension offer our ProgID in the Open-with list.
            using (var owp = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Ext}\OpenWithProgids"))
                owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

            // Applications\<exe> entry — the classic "Open with" application record.
            using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
            {
                app.SetValue("FriendlyAppName", "Ultimate ZPL Viewer");
                using (var cmd = app.CreateSubKey(@"shell\open\command"))
                    cmd.SetValue("", Command);
                using (var types = app.CreateSubKey("SupportedTypes"))
                    types.SetValue(Ext, "");
            }
        }
        catch { /* registration is best-effort; never block startup */ }
    }

    // True when opening a .zpl file currently launches this app.
    public static bool IsDefault()
    {
        try
        {
            // A user-set default (Win10/11 UserChoice) takes precedence over the
            // classic association.
            using (var uc = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{Ext}\UserChoice"))
            {
                if (uc?.GetValue("ProgId") is string chosen)
                    return string.Equals(chosen, ProgId, StringComparison.OrdinalIgnoreCase);
            }
            using var ext = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Ext}");
            return (ext?.GetValue("") as string) == ProgId;
        }
        catch { return false; }
    }

    // Best-effort: set .zpl to open with us. Works when no UserChoice exists (the
    // usual case for .zpl). Returns whether the app is default afterwards.
    public static bool SetAsDefault()
    {
        try
        {
            EnsureRegistered();
            using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Ext}"))
                ext.SetValue("", ProgId);
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* ignore */ }
        return IsDefault();
    }
}
