using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ultimate_ZPL_Viewer;

public sealed record FontInfo(
    string DisplayName,
    string[] RegistryPrefixes,
    string[] FileNames,
    string? ArchiveUrl,
    bool CanAutoInstall);

public sealed record FontInstallResult(string DisplayName, bool Success, string? Error = null);

public static class FontService
{
    private static readonly HttpClient Http = new();

    // Swiss 721 Condensed Bold is a commercial Monotype/Bitstream font — no free download.
    // Bitstream Vera Sans Mono was donated to the community by Bitstream and is freely available.
    public static readonly IReadOnlyList<FontInfo> RequiredFonts =
    [
        new FontInfo(
            DisplayName: "Swiss 721 Condensed Bold",
            RegistryPrefixes: ["Swiss 721", "Swis721 BdCnBT", "Swiss721 BdCnBT", "Swiss 721 BdCn BT", "Swiss721BT-BoldCondensed", "Swiss 721 Bold Condensed"],
            FileNames: ["swis721b.ttf", "Swiss721BdCnBT.ttf", "Swis721BdCnBT.ttf", "swis721bd.ttf"],
            ArchiveUrl: null,
            CanAutoInstall: false),

        new FontInfo(
            DisplayName: "Bitstream Vera Sans Mono",
            RegistryPrefixes: ["Bitstream Vera Sans Mono"],
            FileNames: ["VeraMono.ttf", "VeraMoBd.ttf"],
            ArchiveUrl: "https://download.gnome.org/sources/ttf-bitstream-vera/1.10/ttf-bitstream-vera-1.10.tar.gz",
            CanAutoInstall: true),
    ];

    public static List<FontInfo> GetMissingFonts() =>
        RequiredFonts.Where(f => !IsFontInstalled(f)).ToList();

    private static bool IsFontInstalled(FontInfo font)
    {
        var fontDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts"),
        };

        foreach (var dir in fontDirs.Where(Directory.Exists))
        {
            if (font.FileNames.Any(name => File.Exists(Path.Combine(dir, name))))
                return true;
        }

        var registryPaths = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
            (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"),
        };

        foreach (var (hive, subKey) in registryPaths)
        {
            using var key = hive.OpenSubKey(subKey);
            if (key is null) continue;

            foreach (var valueName in key.GetValueNames())
            {
                if (font.RegistryPrefixes.Any(prefix =>
                        valueName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Downloads and installs all auto-installable fonts.
    /// Returns one result per font with success/failure status.
    /// Fonts that cannot be auto-installed are skipped silently.
    /// </summary>
    public static async Task<IReadOnlyList<FontInstallResult>> InstallAsync(
        IEnumerable<FontInfo> fonts,
        IProgress<(double Value, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var installable = fonts.Where(f => f.CanAutoInstall && f.ArchiveUrl is not null).ToList();
        var results = new List<FontInstallResult>();
        if (installable.Count == 0) return results;

        var userFontsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");
        Directory.CreateDirectory(userFontsDir);

        for (var i = 0; i < installable.Count; i++)
        {
            var font = installable[i];
            var sliceStart = (double)i / installable.Count;
            var sliceEnd   = (double)(i + 1) / installable.Count;

            progress?.Report((sliceStart, $"Téléchargement : {font.DisplayName}…"));

            var tempFile = Path.Combine(Path.GetTempPath(), $"ultimatezplviewer_{Guid.NewGuid():N}.tar.gz");
            try
            {
                ct.ThrowIfCancellationRequested();

                await DownloadAsync(
                    font.ArchiveUrl!,
                    tempFile,
                    p => progress?.Report((sliceStart + p * (sliceEnd - sliceStart) * 0.75,
                                          $"Téléchargement : {font.DisplayName}…")),
                    ct);

                progress?.Report((sliceStart + (sliceEnd - sliceStart) * 0.75,
                                  $"Installation : {font.DisplayName}…"));

                var installed = await ExtractAndInstallAsync(tempFile, font.FileNames, userFontsDir, ct);
                RegisterFonts(installed);

                results.Add(new FontInstallResult(font.DisplayName, Success: true));
                progress?.Report((sliceEnd, $"{font.DisplayName} installée."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new FontInstallResult(font.DisplayName, Success: false, Error: ex.Message));
                progress?.Report((sliceEnd, $"Échec : {font.DisplayName}."));
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best-effort */ }
            }
        }

        // Broadcast WM_FONTCHANGE so all running apps (Word, etc.) refresh their font lists.
        // SendMessageTimeout avoids a hang if any app is unresponsive.
        NativeMethods.SendMessageTimeout(
            NativeMethods.HWND_BROADCAST,
            NativeMethods.WM_FONTCHANGE,
            0, 0,
            NativeMethods.SMTO_ABORTIFHUNG,
            1000,
            out _);

        return results;
    }

    private static async Task DownloadAsync(
        string url, string destFile,
        Action<double>? onProgress,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var downloaded  = 0L;

        await using var src  = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destFile);

        var buf = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (totalBytes > 0) onProgress?.Invoke((double)downloaded / totalBytes);
        }
    }

    private static async Task<List<(string FileName, string FullPath)>> ExtractAndInstallAsync(
        string archivePath, string[] wantedFiles, string destDir, CancellationToken ct)
    {
        var installed = new List<(string, string)>();

        await using var fs  = File.OpenRead(archivePath);
        await using var gz  = new GZipStream(fs, CompressionMode.Decompress);
        using  var tar = new TarReader(gz, leaveOpen: false);

        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken: ct)) is not null)
        {
            var name = Path.GetFileName(entry.Name);
            if (!wantedFiles.Any(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (entry.DataStream is null)
                continue;

            var destPath = Path.Combine(destDir, name);
            await using (var dest = File.Create(destPath))
                await entry.DataStream.CopyToAsync(dest, ct);

            // Register with GDI immediately so the font is available in the current Windows
            // session without requiring a logoff/reboot. Without this call, apps like Word
            // only see the font after the next Windows session starts (registry alone is not
            // enough for already-running processes).
            NativeMethods.AddFontResource(destPath);

            installed.Add((name, destPath));
        }

        return installed;
    }

    private static void RegisterFonts(IEnumerable<(string FileName, string FullPath)> fonts)
    {
        // Per-user font registration (no admin rights required, Windows 10 1809+).
        // Value must be the full path for HKCU; Windows resolves it at next login too.
        using var key = Registry.CurrentUser.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", writable: true);

        foreach (var (fileName, fullPath) in fonts)
            key.SetValue(FriendlyName(fileName), fullPath);
    }

    private static string FriendlyName(string fileName) => fileName.ToLowerInvariant() switch
    {
        "veramono.ttf"  => "Bitstream Vera Sans Mono (TrueType)",
        "veramobd.ttf"  => "Bitstream Vera Sans Mono Bold (TrueType)",
        "veramobi.ttf"  => "Bitstream Vera Sans Mono Bold Italic (TrueType)",
        _               => Path.GetFileNameWithoutExtension(fileName) + " (TrueType)",
    };
}

internal static partial class NativeMethods
{
    public const nint HWND_BROADCAST   = 0xffff;
    public const uint WM_FONTCHANGE    = 0x001D;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // Loads a font file into GDI for the current Windows session.
    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int AddFontResource(string lpFileName);

    // Broadcasts a message to all top-level windows with a timeout per window.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, nint lParam,
        uint flags, uint timeout, out nint pdwResult);
}
