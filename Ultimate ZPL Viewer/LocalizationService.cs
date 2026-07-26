using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ultimate_ZPL_Viewer;

// Loads the user-editable language files from
//   %LOCALAPPDATA%\Ultimate ZPL Viewer\languages\*.json
// (the bundled Assets\lang_{en,fr}.json are copied there on first run) and
// resolves dotted keys such as "toolbar.newFile" or "editor.shortDescription.XA".
//
// A language is identified by its "language" field (NOT its filename). Each file
// must carry both "language" (the code) and "displayName" (the shown name) to be
// offered in the UI. The optional "basedOn" field names another file's "language"
// to inherit from: that base's strings load first, then this file's override them.
//
// The colour-scheme JSON stores dynamic references like
//   "shortDescription": "lang.editor.shortDescription.XA"
// which Resolve() turns into the active language's string. Missing keys fall
// back to English, then to the raw key, so the UI never shows blanks.
public static class LocalizationService
{
    // A language file's header, extracted from its "language"/"displayName"/
    // "basedOn" fields. Valid=false means the JSON did not parse (the header was
    // recovered by a best-effort text scan) — such a language is still listed but
    // rejected on selection.
    private sealed record LangInfo(string Code, string? DisplayName, string? BasedOn, string Path, bool Valid);

    private const string Fallback = "en";
    private static readonly string[] Bundled = { "en", "fr" };

    public static string LanguagesDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultimate ZPL Viewer", "languages");

    private static string _current = Fallback;
    private static Dictionary<string, string>? _flat;      // active language, flattened
    private static Dictionary<string, string>? _flatFallback;
    private static Dictionary<string, string[]>? _arrays;  // active language, string arrays
    private static Dictionary<string, string[]>? _arraysFallback;

    // Raised (on a threadpool thread) whenever a file in the languages folder is
    // created, edited, renamed or deleted. Subscribers must marshal to the UI
    // thread and reload via SetLanguage / rebuild the language list themselves.
    public static event Action? LanguagesChanged;

    private static FileSystemWatcher? _watcher;
    private static System.Threading.Timer? _debounce;

    // Sets the active language (by its "language" code) and (re)loads its strings
    // (resolving any "basedOn" chain) plus the English fallback.
    public static void SetLanguage(string code)
    {
        EnsureFiles();
        _current = string.IsNullOrWhiteSpace(code) ? Fallback : code;
        var reg = ScanFiles();
        (_flat, _arrays) = LoadByCode(_current, reg, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        (_flatFallback, _arraysFallback) = _current.Equals(Fallback, StringComparison.OrdinalIgnoreCase)
            ? (_flat, _arrays)
            : LoadByCode(Fallback, reg, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    // Returns a localized string array (combo options). current → English → empty.
    public static string[] GetArray(string key)
    {
        if (_arrays is null) SetLanguage(_current);
        if (_arrays!.TryGetValue(key, out var v)) return v;
        if (_arraysFallback!.TryGetValue(key, out var f)) return f;
        return System.Array.Empty<string>();
    }

    // Resolves a dotted key ("toolbar.newFile"). A value beginning with "lang."
    // (as stored in the colour scheme) is resolved after stripping that prefix;
    // any other literal string is returned unchanged (so users can still hardcode).
    public static string Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        if (value.StartsWith("lang.", StringComparison.Ordinal))
            return Get(value.Substring("lang.".Length));
        return value;
    }

    // Looks up a dotted key directly. current → English fallback → the key itself.
    public static string Get(string key)
    {
        if (_flat is null) SetLanguage(_current);
        if (_flat!.TryGetValue(key, out var v)) return v;
        if (_flatFallback!.TryGetValue(key, out var f)) return f;
        return key;
    }

    // The active language code (its "language" field value).
    public static string CurrentCode => _current;

    // ── available languages / live watching ───────────────────────────────────

    // Every languages/*.json that declares BOTH "language" and "displayName",
    // as (code, displayName). The filename is irrelevant — the code is the
    // "language" field. English and French are listed first, the rest by name.
    // A file with invalid JSON is still listed if its two header fields could be
    // recovered textually (it is rejected on selection — see IsValidLanguageFile).
    public static List<(string Code, string DisplayName)> AvailableLanguages()
    {
        var list = ScanFiles()
            .Where(l => !string.IsNullOrWhiteSpace(l.DisplayName))
            .Select(l => (l.Code, DisplayName: l.DisplayName!))
            .ToList();

        int Rank(string c) => c.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0
                            : c.Equals("fr", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        list.Sort((a, b) =>
        {
            int r = Rank(a.Code).CompareTo(Rank(b.Code));
            return r != 0 ? r : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });
        return list;
    }

    // True if the file backing a language code is valid JSON (used to gate
    // selection: an invalid file is listed but can't be applied).
    public static bool IsValidLanguageFile(string code)
        => ScanFiles().FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Valid ?? false;

    // Raw text of the file backing a language code (to prefill JSONLint). null if none.
    public static string? GetLanguageRaw(string code)
    {
        var info = ScanFiles().FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (info is null) return null;
        try { return File.ReadAllText(info.Path); } catch { return null; }
    }

    // Scans every languages/*.json into a header registry. A file must carry a
    // "language" code to be kept; the first file for a given code wins. Files that
    // fail to parse still contribute a header if "language"/"displayName" can be
    // recovered by a text scan (Valid=false).
    private static List<LangInfo> ScanFiles()
    {
        EnsureFiles();
        var list = new List<LangInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(LanguagesDir, "*.json"))
            {
                var (code, disp, based, valid) = ReadHeader(path);
                if (string.IsNullOrWhiteSpace(code) || !seen.Add(code!)) continue;
                list.Add(new LangInfo(code!, disp, based, path, valid));
            }
        }
        catch { /* folder unreadable → whatever we gathered */ }
        return list;
    }

    // Extracts the header (language / displayName / basedOn) from a file. Parses as
    // JSON first; on failure, recovers the three fields by regex (Valid=false).
    private static (string? Code, string? Display, string? BasedOn, bool Valid) ReadHeader(string path)
    {
        string text;
        try { text = File.ReadAllText(path); } catch { return (null, null, null, false); }
        try
        {
            using var doc = JsonDocument.Parse(text, JsonOpts);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null, true);
            return (Str(doc.RootElement, "language"),
                    Str(doc.RootElement, "displayName"),
                    Str(doc.RootElement, "basedOn"),
                    true);
        }
        catch
        {
            return (RegexField(text, "language"), RegexField(text, "displayName"), RegexField(text, "basedOn"), false);
        }

        static string? Str(JsonElement o, string name)
            => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
    }

    private static string? RegexField(string text, string name)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value : null;
    }

    // Starts watching the languages folder. Create/edit/rename/delete all raise
    // LanguagesChanged (debounced 250 ms to coalesce editor save bursts). Safe to
    // call more than once.
    public static void StartWatching()
    {
        if (_watcher is not null) return;
        try { Directory.CreateDirectory(LanguagesDir); } catch { return; }
        try
        {
            _watcher = new FileSystemWatcher(LanguagesDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => Debounce();
            _watcher.Deleted += (_, _) => Debounce();
            _watcher.Changed += (_, _) => Debounce();
            _watcher.Renamed += (_, _) => Debounce();
        }
        catch { _watcher = null; }
    }

    private static void Debounce()
    {
        _debounce?.Dispose();
        _debounce = new System.Threading.Timer(_ => LanguagesChanged?.Invoke(),
            null, 250, System.Threading.Timeout.Infinite);
    }

    // ── file management ──────────────────────────────────────────────────────

    private static readonly JsonDocumentOptions JsonOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Loads a language by its "language" code, resolving "basedOn" first (base
    // strings, then this file overrides them). Cycles and missing/broken files
    // yield whatever could be gathered; callers still fall back to English.
    private static (Dictionary<string, string>, Dictionary<string, string[]>) LoadByCode(
        string code, List<LangInfo> reg, HashSet<string> visited)
    {
        var flat = new Dictionary<string, string>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(code) || !visited.Add(code)) return (flat, arrays);

        var info = reg.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (info is null) return (flat, arrays);

        if (!string.IsNullOrWhiteSpace(info.BasedOn))
        {
            var (bf, ba) = LoadByCode(info.BasedOn!, reg, visited);
            foreach (var kv in bf) flat[kv.Key] = kv.Value;
            foreach (var kv in ba) arrays[kv.Key] = kv.Value;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(info.Path), JsonOpts);
            Flatten(doc.RootElement, "", flat, arrays); // this file overrides the base
        }
        catch { /* invalid JSON → keep the base strings only */ }
        return (flat, arrays);
    }

    private static void Flatten(JsonElement el, string prefix,
        Dictionary<string, string> flat, Dictionary<string, string[]> arrays)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                    Flatten(prop.Value, prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name, flat, arrays);
                break;
            case JsonValueKind.String:
                flat[prefix] = el.GetString() ?? "";
                break;
            case JsonValueKind.Array:
                // A string array → combo options (non-string elements make it ignored).
                var items = new List<string>();
                bool allStrings = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) items.Add(item.GetString() ?? "");
                    else { allStrings = false; break; }
                }
                if (allStrings && items.Count > 0) arrays[prefix] = items.ToArray();
                break;
        }
    }

    // Copies any missing bundled language file into the user languages folder.
    public static void EnsureFiles()
    {
        try { Directory.CreateDirectory(LanguagesDir); } catch { return; }

        string packagePath;
        try   { packagePath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path; }
        catch { packagePath = AppContext.BaseDirectory; }
        string[] bases = { packagePath, AppContext.BaseDirectory, AppDomain.CurrentDomain.BaseDirectory };

        foreach (var code in Bundled)
        {
            var dest = Path.Combine(LanguagesDir, code + ".json");
            if (File.Exists(dest)) continue;
            foreach (var b in bases)
            {
                var src = Path.Combine(b, "Assets", "lang_" + code + ".json");
                if (File.Exists(src)) { try { File.Copy(src, dest); } catch { } break; }
            }
        }
    }
}
