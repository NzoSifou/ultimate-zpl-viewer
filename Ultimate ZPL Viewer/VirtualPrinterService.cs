using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Ultimate_ZPL_Viewer;

// Manages the "Ultimate ZPL Viewer" virtual printer.
//
// The printer uses the in-box "Generic / Text Only" driver bound to a Local Port
// that is a file path. When something prints to it, the spooler (SYSTEM) writes
// the raw job bytes to that file; the app watches the folder, reads the job,
// decides whether it is ZPL, and loads it (or reports an unsupported format).
//
// Installation needs admin rights and is done once by an elevated PowerShell
// script (single UAC prompt). The script also registers a scheduled task that
// relaunches the app (via its custom URI protocol) when a job prints while the
// app is closed.
public static class VirtualPrinterService
{
    public const string PrinterName = "Ultimate ZPL Viewer";
    public const string Protocol    = "ultimate-zpl-viewer";

    // The spooler runs as SYSTEM, so the spool lives under ProgramData (writable
    // by SYSTEM, readable by everyone).
    public static string SpoolFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UltimateZplViewer");

    public static string SpoolFile => Path.Combine(SpoolFolder, "spool.prn");
    public static string InstallLog => Path.Combine(SpoolFolder, "install.log");

    private const string TaskName = "UltimateZplViewer_PrintCapture";

    // Argument that puts a fresh (elevated) instance of this exe into "run the
    // install/uninstall script" mode — see Program.Main.
    public const string ElevatedInstallArg = "--run-elevated-script";

    // Runs the given PowerShell script HIDDEN in the already-elevated helper
    // instance (no UAC, no window). Returns the script's exit code.
    public static int RunElevatedScript(string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath)) return 2;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,   // already elevated → no prompt
                CreateNoWindow  = true,    // truly no console window
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode ?? 1;
        }
        catch { return 1; }
    }

    private enum ElevateOutcome { Ran, Cancelled, Unsupported }

    // Elevates a NEW instance of THIS exe to run the script (UAC names
    // "Ultimate ZPL Viewer", PowerShell stays hidden). Falls back to elevating
    // PowerShell directly if the packaged exe cannot be self-elevated (e.g. by policy).
    private static ElevateOutcome SelfElevate(string scriptPath)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return ElevateOutcome.Unsupported;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = $"{ElevatedInstallArg} \"{scriptPath}\"",
                UseShellExecute = true,
                Verb            = "runas",
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();
            return ElevateOutcome.Ran;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevateOutcome.Cancelled; // ERROR_CANCELLED (UAC refused)
        }
        catch
        {
            return ElevateOutcome.Unsupported; // couldn't launch the packaged exe elevated
        }
    }

    private static ElevateOutcome ElevatePowershell(string scriptPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb            = "runas",
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();
            return ElevateOutcome.Ran;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevateOutcome.Cancelled;
        }
        catch
        {
            return ElevateOutcome.Unsupported;
        }
    }

    // Runs the elevated script, preferring self-elevation (nice UAC + no window)
    // and falling back to a PowerShell UAC only when the app couldn't self-elevate
    // or its helper never ran (so the install is never left impossible).
    // Returns "cancelled" if UAC was refused, null otherwise (caller verifies state).
    private static string? ElevateAndRun(string scriptPath)
    {
        DateTime before = LogTimeUtc();
        var r = SelfElevate(scriptPath);
        if (r == ElevateOutcome.Cancelled) return "cancelled";
        // Fall back only if the packaged exe couldn't be elevated, or it elevated
        // but its helper never touched the log (crashed before running the script).
        if (r == ElevateOutcome.Unsupported || LogTimeUtc() <= before)
        {
            var f = ElevatePowershell(scriptPath);
            if (f == ElevateOutcome.Cancelled) return "cancelled";
        }
        return null;
    }

    private static DateTime LogTimeUtc()
    {
        try { return File.Exists(InstallLog) ? File.GetLastWriteTimeUtc(InstallLog) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    // True when the printer already exists (checked without elevation via the
    // registry key the app already reads for the printer list).
    public static bool IsInstalled()
    {
        try
        {
            using var printers = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Print\Printers");
            return printers?.GetSubKeyNames()
                .Any(n => string.Equals(n, PrinterName, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    // Installs the printer if missing. Shows one UAC prompt. The result carries a
    // diagnostic message read back from the script's log on failure.
    public static InstallResult EnsureInstalled()
    {
        if (IsInstalled()) return new InstallResult(true, null);

        string scriptPath;
        try
        {
            scriptPath = WriteInstallScript();
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"Impossible d'écrire le script d'installation : {ex.Message}");
        }

        var err = ElevateAndRun(scriptPath);
        if (err == "cancelled") return new InstallResult(false, "Installation annulée (élévation refusée).");

        if (IsInstalled()) return new InstallResult(true, null);

        // Failed: surface the script log (or a generic message).
        string? log = null;
        try { if (File.Exists(InstallLog)) log = File.ReadAllText(InstallLog).Trim(); } catch { }
        return new InstallResult(false, string.IsNullOrWhiteSpace(log)
            ? "L'installation ne s'est pas terminée. Réessayez, ou vérifiez que l'installation d'imprimantes n'est pas bloquée par une stratégie."
            : log);
    }

    // Reinstalls in a SINGLE elevated pass (one UAC prompt): the script removes the
    // existing printer/port/task, then recreates everything. Used by the "Reinstall"
    // button so it no longer prompts for admin twice.
    public static InstallResult Reinstall()
    {
        string scriptPath;
        try { scriptPath = WriteReinstallScript(); }
        catch (Exception ex)
        {
            return new InstallResult(false, $"Impossible d'écrire le script de réinstallation : {ex.Message}");
        }

        var err = ElevateAndRun(scriptPath);
        if (err == "cancelled") return new InstallResult(false, "Réinstallation annulée (élévation refusée).");

        if (IsInstalled()) return new InstallResult(true, null);

        string? log = null;
        try { if (File.Exists(InstallLog)) log = File.ReadAllText(InstallLog).Trim(); } catch { }
        return new InstallResult(false, string.IsNullOrWhiteSpace(log)
            ? "La réinstallation ne s'est pas terminée. Réessayez, ou vérifiez que l'installation d'imprimantes n'est pas bloquée par une stratégie."
            : log);
    }

    // Removes the printer, port, scheduled task and spool folder (elevated).
    public static InstallResult Uninstall()
    {
        if (!IsInstalled()) return new InstallResult(true, null);

        string scriptPath;
        try { scriptPath = WriteUninstallScript(); }
        catch (Exception ex) { return new InstallResult(false, ex.Message); }

        var err = ElevateAndRun(scriptPath);
        if (err == "cancelled") return new InstallResult(false, "Désinstallation annulée (élévation refusée).");

        return IsInstalled()
            ? new InstallResult(false, "La désinstallation n'a pas abouti.")
            : new InstallResult(true, null);
    }

    // Reads a captured job and clears the spool file. Returns null when there is
    // nothing pending (or it could not be read yet).
    public static CapturedJob? TryReadPending()
    {
        try
        {
            if (!File.Exists(SpoolFile)) return null;

            byte[] bytes;
            // The spooler may still hold the file for a moment after writing.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    bytes = File.ReadAllBytes(SpoolFile);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    System.Threading.Thread.Sleep(50);
                }
            }

            if (bytes.Length == 0) return null;

            try { File.Delete(SpoolFile); } catch { /* best effort */ }

            var text = DecodeText(bytes);
            return new CapturedJob(IsZpl(text), text);
        }
        catch
        {
            return null;
        }
    }

    // Decodes the raw bytes to text, dropping the trailing form-feed / nulls that
    // the Generic/Text driver may append.
    private static string DecodeText(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Trim('\0', '\f', '\r', '\n', ' ', '\t');
    }

    // A job is treated as ZPL when it contains a ^XA start command or begins with
    // a ZPL command prefix. Binary formats (PDF "%PDF", PostScript "%!", PCL,
    // ZIP/Office "PK"…) fail this test → reported as unsupported.
    public static bool IsZpl(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.TrimStart();

        // Reject well-known non-ZPL signatures up front.
        string[] foreignSignatures = { "%PDF", "%!", "PK", "%", "{\\rtf", "<?xml", "<html" };
        if (foreignSignatures.Any(sig => trimmed.StartsWith(sig, StringComparison.OrdinalIgnoreCase)))
            return false;

        return trimmed.Contains("^XA", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("^", StringComparison.Ordinal)
            || trimmed.StartsWith("~", StringComparison.Ordinal);
    }

    // Writes the elevated install script and returns its path. It MUST go to a
    // real (non-virtualized) location: under MSIX, %LOCALAPPDATA% is redirected to
    // the package's LocalCache, so a path written from inside the container points
    // elsewhere for the elevated process (which runs outside it) — the script file
    // would appear missing and the install would fail silently. The package's
    // LocalState folder has the same real path inside and outside the container.
    private static string WriteInstallScript()
    {
        string dir;
        try { dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ultimate ZPL Viewer");
            Directory.CreateDirectory(dir);
        }
        var scriptPath = Path.Combine(dir, "install-printer.ps1");

        var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        var script = BuildInstallScript(userSid);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        return scriptPath;
    }

    // Writes the elevated REINSTALL script (removal + install in one file, one UAC).
    private static string WriteReinstallScript()
    {
        string dir;
        try { dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ultimate ZPL Viewer");
            Directory.CreateDirectory(dir);
        }
        var scriptPath = Path.Combine(dir, "reinstall-printer.ps1");

        var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        var script = BuildReinstallScript(userSid);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        return scriptPath;
    }

    // Writes the elevated uninstall script (same real-path rules as the install
    // one). It touches the install log first so the elevation "did it run?"
    // heuristic works for uninstall too.
    private static string WriteUninstallScript()
    {
        string dir;
        try { dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ultimate ZPL Viewer");
            Directory.CreateDirectory(dir);
        }
        var scriptPath = Path.Combine(dir, "uninstall-printer.ps1");

        var script = $$"""
$spoolDir  = '{{SpoolFolder.Replace("'", "''")}}'
$logFile   = '{{InstallLog.Replace("'", "''")}}'
if (-not (Test-Path $spoolDir)) { New-Item -ItemType Directory -Path $spoolDir -Force | Out-Null }
Set-Content -Path $logFile -Value ("[{0}] Debut desinstallation" -f (Get-Date -Format o))
Remove-Printer -Name '{{PrinterName}}' -EA SilentlyContinue
Remove-PrinterPort -Name '{{SpoolFile.Replace("'", "''")}}' -EA SilentlyContinue
Unregister-ScheduledTask -TaskName '{{TaskName}}' -Confirm:$false -EA SilentlyContinue
# Delete the captured spool file but keep the folder + log (the elevation
# heuristic checks the log timestamp right after this runs).
Remove-Item '{{SpoolFile.Replace("'", "''")}}' -Force -EA SilentlyContinue
exit 0
""";
        File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
        return scriptPath;
    }

    private static string BuildInstallScript(string userSid) =>
        ScriptHeader(userSid, "installation") + InstallBody();

    // Removal + install in ONE script, so "Reinstall" prompts for admin only once.
    private static string BuildReinstallScript(string userSid) =>
        ScriptHeader(userSid, "reinstallation") + RemovalBlock() + InstallBody();

    // Variable declarations + spool folder + log start + Log function.
    private static string ScriptHeader(string userSid, string action)
    {
        return $$"""
$printerName = '{{PrinterName}}'
$spoolDir    = '{{SpoolFolder.Replace("'", "''")}}'
$spoolFile   = '{{SpoolFile.Replace("'", "''")}}'
$logFile     = '{{InstallLog.Replace("'", "''")}}'
$taskName    = '{{TaskName}}'
$exePath     = '{{(Environment.ProcessPath ?? "").Replace("'", "''")}}'
$userSid     = '{{userSid}}'

# Spool folder first (needed for the log), then log every step so a hidden,
# elevated failure can still be diagnosed from the app.
if (-not (Test-Path $spoolDir)) { New-Item -ItemType Directory -Path $spoolDir -Force | Out-Null }
Set-Content -Path $logFile -Value ("[{0}] Debut {{action}}" -f (Get-Date -Format o))
function Log($m) { try { Add-Content -Path $logFile -Value ("[{0}] {1}" -f (Get-Date -Format o), $m) } catch {} }

""";
    }

    // Tears down any existing printer/port/task before the install steps re-add them.
    private static string RemovalBlock()
    {
        return """
Remove-Printer -Name $printerName -EA SilentlyContinue
Remove-PrinterPort -Name $spoolFile -EA SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -EA SilentlyContinue
Log 'Ancienne installation supprimee'

""";
    }

    private static string InstallBody()
    {
        // Note: the scheduled task is event-triggered on PrintService event 307
        // (document printed) filtered by our printer name, and launches the app's
        // exe directly with --print-capture so it opens even when closed (the app
        // is unpackaged, so there is no MSIX URI protocol). The app-side capture
        // logic verifies there is real pending content, so a mis-filtered event
        // never pops the window.
        return """
try {
    # In-box Generic / Text Only driver.
    if (-not (Get-PrinterDriver -Name 'Generic / Text Only' -ErrorAction SilentlyContinue)) {
        Add-PrinterDriver -Name 'Generic / Text Only' -ErrorAction Stop
        Log 'Pilote Generic/Text ajoute'
    } else { Log 'Pilote deja present' }

    # Local port = the spool file path.
    if (-not (Get-PrinterPort -Name $spoolFile -ErrorAction SilentlyContinue)) {
        Add-PrinterPort -Name $spoolFile -ErrorAction Stop
        Log 'Port ajoute'
    } else { Log 'Port deja present' }

    # The printer itself.
    if (-not (Get-Printer -Name $printerName -ErrorAction SilentlyContinue)) {
        Add-Printer -Name $printerName -DriverName 'Generic / Text Only' -PortName $spoolFile -ErrorAction Stop
        Log 'Imprimante ajoutee'
    } else { Log 'Imprimante deja presente' }

    # Enable the PrintService operational log (source of the task trigger).
    try { wevtutil sl Microsoft-Windows-PrintService/Operational /e:true; Log 'Journal PrintService active' } catch { Log 'Journal PrintService: ignore' }

    # Scheduled task: on 'document printed' for our printer, launch the app's exe
    # directly (with --print-capture) so it opens even when closed.
    $subscription = @"
<QueryList>
  <Query Id="0" Path="Microsoft-Windows-PrintService/Operational">
    <Select Path="Microsoft-Windows-PrintService/Operational">*[System[(EventID=307)]] and *[UserData/DocumentPrinted[Param4='$printerName']]</Select>
  </Query>
</QueryList>
"@
    $action = New-ScheduledTaskAction -Execute $exePath -Argument '--print-capture'
    $stt = New-CimInstance -CimClass (Get-CimClass -Namespace root/Microsoft/Windows/TaskScheduler -ClassName MSFT_TaskEventTrigger) -ClientOnly
    $stt.Enabled = $true
    $stt.Subscription = $subscription
    $principal = New-ScheduledTaskPrincipal -UserId $userSid -LogonType Interactive -RunLevel Limited
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $stt -Principal $principal -Settings $settings -Force | Out-Null
    Log 'Tache planifiee enregistree'

    Log 'SUCCES'
    exit 0
}
catch {
    Log ('ERREUR: ' + $_.Exception.Message)
    exit 1
}
""";
    }
}

public sealed record CapturedJob(bool IsZpl, string Content);
public sealed record InstallResult(bool Ok, string? Error);
