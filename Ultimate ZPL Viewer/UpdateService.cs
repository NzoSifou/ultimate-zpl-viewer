using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Ultimate_ZPL_Viewer;

/// <summary>One downloadable file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>
/// What a check found. <see cref="Available"/> false means "nothing to do" —
/// either we are current, or the answer could not be established (no network,
/// GitHub down, rate-limited), which is reported in <see cref="Error"/>.
/// </summary>
public sealed record UpdateCheck(
    bool Available, string Label, string Notes, string PageUrl,
    ReleaseAsset? Asset, string? Error, DateTimeOffset? PublishedAt = null);

/// <summary>
/// Looks for a newer build on the project's GitHub releases, and fetches it.
///
/// HOW "NEWER" IS DECIDED. Not by comparing version strings: a release can be
/// renamed, re-tagged, or re-published with a corrected binary under the same
/// name, and a string comparison misses every one of those. The installer records
/// the SHA-256 of the setup it was launched from (see install.json, written by the
/// Inno Setup script), and GitHub publishes the SHA-256 of each release asset. Two
/// different hashes mean two different builds — whatever they are called. That is
/// also why a renamed tag scheme cannot break this: nothing here reads the version
/// except to SHOW it.
///
/// Version numbers are the fallback, for an installation that predates install.json
/// or an asset GitHub reports no digest for.
/// </summary>
public static class UpdateService
{
    public const string Repo = "NzoSifou/ultimate-zpl-viewer";
    private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";

    // ─────────────────────────────────────────────────────────────────────────
    //  INTERRUPTEUR DE TEST — à laisser sur false dans une version publiée.
    //
    //  true  : la dernière release est toujours annoncée comme une mise à jour,
    //          même quand elle est déjà installée. Tout le reste est réel — le
    //          téléchargement, la vérification d'empreinte, l'installation et la
    //          relance — ce qui permet de dérouler le parcours complet sans avoir
    //          à publier quoi que ce soit.
    //  false : comportement normal, l'empreinte décide.
    //
    //  Un champ et non une constante : le compilateur ne replie donc rien, et la
    //  valeur reste modifiable à chaud depuis le débogueur.
    // ─────────────────────────────────────────────────────────────────────────
    public static bool ForceUpdatePrompt = false;

    // GitHub refuses requests without a User-Agent.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent", "UltimateZplViewer");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    // ── What we know about ourselves ─────────────────────────────────────────

    private static string InstallInfoPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultimate ZPL Viewer", "install.json");

    /// <summary>
    /// SHA-256 of the setup this installation came from, or null when unknown —
    /// installed before the updater existed, or copied by hand.
    /// </summary>
    public static string? InstalledSetupSha256()
    {
        try
        {
            if (!File.Exists(InstallInfoPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(InstallInfoPath));
            var hash = node?["setupSha256"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>The running executable's file version, e.g. "1.4.1.0".</summary>
    public static string CurrentVersion()
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo
                .GetVersionInfo(Environment.ProcessPath!).FileVersion;
            return string.IsNullOrWhiteSpace(v) ? "" : v!;
        }
        catch { return ""; }
    }

    // ── The check ────────────────────────────────────────────────────────────

    public static async Task<UpdateCheck> CheckAsync(CancellationToken token = default)
    {
        JsonNode? release;
        try
        {
            using var response = await Http.GetAsync(LatestApi, token);
            if (!response.IsSuccessStatusCode)
                return Failed($"HTTP {(int)response.StatusCode}");
            release = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        }
        catch (Exception ex) { return Failed(ex.Message); }

        if (release is null) return Failed("réponse illisible");

        var tag   = release["tag_name"]?.GetValue<string>() ?? "";
        var name  = release["name"]?.GetValue<string>();
        var notes = release["body"]?.GetValue<string>() ?? "";
        var page  = release["html_url"]?.GetValue<string>()
                    ?? $"https://github.com/{Repo}/releases/latest";
        var label = string.IsNullOrWhiteSpace(name) ? tag : name!;

        DateTimeOffset? published = null;
        if (DateTimeOffset.TryParse(release["published_at"]?.GetValue<string>(), out var when))
            published = when.ToLocalTime();

        var asset = PickInstaller(release["assets"] as JsonArray);
        bool newer = ForceUpdatePrompt || IsNewer(asset, tag, name);
        return new UpdateCheck(newer, label, notes.Trim(), page, asset, null, published);

        static UpdateCheck Failed(string why) => new(false, "", "", "", null, why);
    }

    /// <summary>
    /// The setup among the release's files. Preferred by name so a release can also
    /// carry a portable zip, a checksum file or anything else without confusing us.
    /// </summary>
    private static ReleaseAsset? PickInstaller(JsonArray? assets)
    {
        if (assets is null) return null;
        var candidates = new List<ReleaseAsset>();
        foreach (var node in assets)
        {
            var fileName = node?["name"]?.GetValue<string>();
            var url = node?["browser_download_url"]?.GetValue<string>();
            if (fileName is null || url is null) continue;
            if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            // GitHub reports "sha256:<hex>"; keep the hex alone.
            var digest = node?["digest"]?.GetValue<string>();
            if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                digest = digest["sha256:".Length..].Trim().ToLowerInvariant();
            else
                digest = null;

            long size = 0;
            try { size = node?["size"]?.GetValue<long>() ?? 0; } catch { }
            candidates.Add(new ReleaseAsset(fileName, url, size, digest));
        }
        return candidates.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Hash first, version second. Returns false whenever neither can answer: an
    /// updater that is unsure must stay quiet rather than nag about a release that
    /// may well be the one already installed.
    /// </summary>
    private static bool IsNewer(ReleaseAsset? asset, string tag, string? name)
    {
        var mine = InstalledSetupSha256();
        if (mine is not null && asset?.Sha256 is { } theirs)
            return !string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);

        var local = ParseVersion(CurrentVersion());
        var remote = ParseVersion(tag) ?? ParseVersion(name ?? "");
        return local is not null && remote is not null && remote > local;
    }

    /// <summary>First dotted number in a string: "v1.4.1", "Release 1.4.1 — …".</summary>
    private static Version? ParseVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var v) ? v : null;
    }

    // ── Fetching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the installer to a temporary file and checks its hash against the
    /// one GitHub published. A file that does not match is deleted, never run: it
    /// is the only thing standing between a truncated or tampered download and an
    /// executable started with the user's rights.
    /// </summary>
    public static async Task<string> DownloadAsync(
        ReleaseAsset asset, IProgress<double>? progress, CancellationToken token = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "UltimateZplViewerUpdate");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, asset.Name);

        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var destination = File.Create(target);

            var buffer = new byte[128 * 1024];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (asset.Sha256 is { } expected)
        {
            var actual = await Task.Run(() => Sha256OfFile(target), token);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(target); } catch { }
                throw new InvalidOperationException(
                    $"empreinte incorrecte (attendu {expected[..12]}…, obtenu {actual[..12]}…)");
            }
        }
        return target;
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Hands over to the downloaded installer and leaves. /SILENT keeps the wizard
    /// out of the way — the app already asked; /UPDATED tells the script to bring
    /// the application back up once the files are in place.
    ///
    /// /NORESTARTAPPLICATIONS matters: Setup would otherwise restart, on its own,
    /// whatever the Restart Manager closed, and the app would come back twice.
    /// Bringing it back is the script's job, once.
    /// </summary>
    public static void LaunchInstaller(string setupPath) =>
        Run(setupPath, "/SILENT /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /UPDATED");

    // ── Installing on the way out ────────────────────────────────────────────

    /// <summary>A verified installer waiting for the application to close.</summary>
    public static string? PendingInstaller { get; private set; }

    public static void ArmForExit(string setupPath) => PendingInstaller = setupPath;

    /// <summary>
    /// Starts the installer the user asked to run at closing time. No /UPDATED and
    /// no restart: they chose "on exit" to be left alone, and an application that
    /// springs back up after being closed is the opposite of that.
    /// Called once, from the last window going away.
    /// </summary>
    public static void RunPendingInstaller()
    {
        if (PendingInstaller is not { } path) return;
        PendingInstaller = null;
        try { Run(path, "/SILENT /NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS"); }
        catch { }
    }

    private static void Run(string setupPath, string arguments) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = arguments,
            UseShellExecute = true,
        });

    /// <summary>Human-readable size, for the "download 80 MB" line.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024
            ? (mb / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " Go"
            : mb.ToString("0.#", CultureInfo.CurrentCulture) + " Mo";
    }
}
