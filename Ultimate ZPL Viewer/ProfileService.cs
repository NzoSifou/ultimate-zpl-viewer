using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ultimate_ZPL_Viewer;

/// <summary>A settings profile as the list shows it.</summary>
internal sealed record ProfileInfo(string Id, IReadOnlyDictionary<string, string> Names, string? FallbackLanguage)
{
    /// <summary>
    /// The name to show in a given language: that language, then its base language
    /// ("fr" for "fr-CA"), then — for the profile that ships with the application —
    /// what the language file calls it, then the profile's own fallback language,
    /// then whichever name it has. Never blank.
    /// </summary>
    public string DisplayName(string language)
    {
        if (Lookup(language) is { } exact) return exact;
        var dash = language.IndexOf('-');
        if (dash > 0 && Lookup(language[..dash]) is { } basic) return basic;
        // The shipped profile names itself in every language that has a word for
        // it: a translator adding a language file names it there, without having
        // to find and edit the profile.
        if (Id == ProfileService.DefaultId
            && LocalizationService.TryGetOwn("settings.profiles.defaultName", out var fromLanguage))
            return fromLanguage;
        if (FallbackLanguage is { } fb && Lookup(fb) is { } fallback) return fallback;
        return Names.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? Id;
    }

    private string? Lookup(string language)
        => Names.FirstOrDefault(n => string.Equals(n.Key, language, StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace(n.Value)).Value;
}

/// <summary>
/// Settings profiles: named sets of every preference, one JSON file each under
/// %LOCALAPPDATA%\Ultimate ZPL Viewer\profiles. The active one is the live copy of
/// the settings — every change lands in it as it is made, and switching loads
/// another in its place.
///
/// What a profile does NOT hold is what belongs to the machine or to the session
/// rather than to a way of working: the recent files, the windows to reopen, the
/// update check, the type chosen for each printer, the size of the screens… (the
/// properties marked [MachineSetting]). Importing a colleague's profile never
/// brings those along.
///
/// <code>
/// {
///   "format": "ultimate-zpl-viewer-profile",
///   "version": 1,
///   "id": "default",
///   "names": { "fr": "Profil par défaut", "en": "Default profile" },
///   "fallbackLanguage": "en",
///   "settings": { "theme": "system", "language": "fr", … }
/// }
/// </code>
/// </summary>
internal static class ProfileService
{
    public const string DefaultId = "default";
    public const string Format = "ultimate-zpl-viewer-profile";
    public const int Version = 1;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultimate ZPL Viewer", "profiles");

    // Readable by a person as much as by the application: indented, camelCase,
    // enumerations by name ("dark", not 2).
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // The names of the profile that ships with the application. Any other
    // language finds its word in its own language file (ProfileInfo.DisplayName).
    private static readonly Dictionary<string, string> DefaultNames = new()
    {
        ["fr"] = "Profil par défaut",
        ["en"] = "Default profile",
    };

    /// <summary>The settings a profile carries: everything not tied to the machine.</summary>
    public static IReadOnlyList<PropertyInfo> ProfileProperties { get; } = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite
                    && p.GetCustomAttribute<MachineSettingAttribute>() is null
                    && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .ToList();

    // ── Start-up ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes sure there is a profile to be in, and puts the active one's values
    /// into <paramref name="settings"/>. The first time, the settings as they stand
    /// become the default profile, so nobody loses what they had set up.
    /// </summary>
    public static AppSettings Initialize(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var profiles = List();
            if (profiles.Count == 0)
            {
                Write(DefaultId, DefaultNames, "en", SettingsOf(settings));
                settings.ActiveProfile = DefaultId;
            }
            else if (profiles.All(p => p.Id != settings.ActiveProfile))
            {
                settings.ActiveProfile = profiles.Any(p => p.Id == DefaultId) ? DefaultId : profiles[0].Id;
            }

            // The profile file is what the profile IS: a value edited in it while
            // the application was closed is what comes up.
            if (ReadSettings(settings.ActiveProfile) is { } stored) Apply(stored, settings);
            settings.SaveSettingsFile();
        }
        catch { /* no profiles folder: the settings file alone still works */ }
        return settings;
    }

    // ── The list ─────────────────────────────────────────────────────────────

    /// <summary>Every profile on disk, the shipped one first, then by name.</summary>
    public static List<ProfileInfo> List()
    {
        var result = new List<ProfileInfo>();
        if (!Directory.Exists(Dir)) return result;
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (root is null) continue;
                var id = Path.GetFileNameWithoutExtension(file);
                result.Add(new ProfileInfo(id, NamesOf(root), root["fallbackLanguage"]?.GetValue<string>()));
            }
            catch { /* a file that is not a profile is not listed */ }
        }
        var lang = LocalizationService.CurrentCode;
        return result
            .OrderBy(p => p.Id == DefaultId ? 0 : 1)
            .ThenBy(p => p.DisplayName(lang), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static ProfileInfo? Find(string id) => List().FirstOrDefault(p => p.Id == id);

    // ── What the application does with them ──────────────────────────────────

    /// <summary>Writes the live settings into the active profile. Called by every save.</summary>
    public static void SaveActive(AppSettings settings)
    {
        var id = settings.ActiveProfile;
        if (string.IsNullOrEmpty(id)) return;
        var current = Read(id);
        Write(id, current is null ? DefaultNames : NamesOf(current),
              current?["fallbackLanguage"]?.GetValue<string>() ?? "en", SettingsOf(settings));
    }

    /// <summary>Loads another profile into the live settings.</summary>
    public static void Switch(AppSettings settings, string id)
    {
        var stored = ReadSettings(id) ?? throw new InvalidOperationException(id);
        // The profile being left keeps what it had until this very moment.
        SaveActive(settings);
        settings.ActiveProfile = id;
        Apply(stored, settings);
        settings.Save();
    }

    /// <summary>A new profile with every setting at its default. Returns its id.</summary>
    public static string Create(string name)
        => Add(name, SettingsOf(new AppSettings()));

    /// <summary>A copy of a profile, settings and all, under a new name.</summary>
    public static string Duplicate(string sourceId, string name)
        => Add(name, ReadSettings(sourceId) ?? new JsonObject());

    /// <summary>
    /// Renames a profile in the language the application is shown in. Its names in
    /// other languages stay as they were: a French rename is a French name.
    /// </summary>
    public static void Rename(string id, string name)
    {
        var root = Read(id) ?? throw new FileNotFoundException(id);
        var names = NamesOf(root);
        names[LocalizationService.CurrentCode] = name.Trim();
        Write(id, names, root["fallbackLanguage"]?.GetValue<string>() ?? LocalizationService.CurrentCode,
              root["settings"] as JsonObject ?? new JsonObject());
    }

    public static void Delete(string id)
    {
        if (List().Count <= 1) throw new InvalidOperationException("last profile");
        File.Delete(PathOf(id));
    }

    /// <summary>The profile as a file to give to someone.</summary>
    public static string Export(string id)
        => (Read(id) ?? throw new FileNotFoundException(id)).ToJsonString(Json);

    /// <summary>
    /// Adds a profile from an exported file. It always comes in as a NEW profile —
    /// importing never overwrites one already there. Only the settings a profile
    /// carries are read; anything else in the file is left aside, and a setting the
    /// file does not mention takes its default.
    /// </summary>
    public static string Import(string json, string fileName)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
              { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
              ?? throw new FormatException(); }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { throw new FormatException("json"); }

        if (root["format"] is JsonValue f && f.TryGetValue<string>(out var format) && format != Format)
            throw new FormatException("format");
        if (root["settings"] is not JsonObject settings)
            throw new FormatException("settings");

        var names = NamesOf(root);
        if (names.Count == 0) names[LocalizationService.CurrentCode] = fileName;
        var fallback = root["fallbackLanguage"]?.GetValue<string>() ?? names.Keys.First();

        // Normalised through the settings themselves: what is written back is what
        // the application understood, with the defaults filled in.
        var parsed = new AppSettings();
        Apply(settings, parsed);
        var id = NewId();
        Write(id, names, fallback, SettingsOf(parsed));
        return id;
    }

    // ── Files ────────────────────────────────────────────────────────────────

    private static string PathOf(string id) => Path.Combine(Dir, id + ".json");

    private static string NewId()
    {
        string id;
        do id = "profile-" + Guid.NewGuid().ToString("N")[..8];
        while (File.Exists(PathOf(id)));
        return id;
    }

    private static string Add(string name, JsonObject settings)
    {
        var id = NewId();
        var lang = LocalizationService.CurrentCode;
        Write(id, new Dictionary<string, string> { [lang] = name.Trim() }, lang, settings);
        return id;
    }

    private static JsonObject? Read(string id)
    {
        try { return JsonNode.Parse(File.ReadAllText(PathOf(id))) as JsonObject; }
        catch { return null; }
    }

    private static JsonObject? ReadSettings(string id) => Read(id)?["settings"] as JsonObject;

    private static Dictionary<string, string> NamesOf(JsonObject root)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root["names"] is JsonObject obj)
            foreach (var (lang, value) in obj)
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    names[lang] = s;
        return names;
    }

    private static void Write(string id, IReadOnlyDictionary<string, string> names, string fallback, JsonObject settings)
    {
        var namesObj = new JsonObject();
        foreach (var (lang, name) in names) namesObj[lang] = name;
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = Version,
            ["id"] = id,
            ["names"] = namesObj,
            ["fallbackLanguage"] = fallback,
            ["settings"] = settings.DeepClone(),
        };
        Directory.CreateDirectory(Dir);
        var path = PathOf(id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Json));
        File.Move(tmp, path, overwrite: true);
    }

    // ── Settings ↔ JSON ──────────────────────────────────────────────────────

    private static JsonObject SettingsOf(AppSettings settings)
    {
        var obj = new JsonObject();
        foreach (var p in ProfileProperties)
            obj[Key(p)] = JsonSerializer.SerializeToNode(p.GetValue(settings), p.PropertyType, Json);
        return obj;
    }

    // Every profile setting takes the file's value, or its default when the file
    // does not have it (or has it in a shape that does not read) — a profile
    // written by an older version comes up with the newer settings at their
    // defaults rather than at whatever the previous profile had.
    private static void Apply(JsonObject stored, AppSettings settings)
    {
        var defaults = new AppSettings();
        foreach (var p in ProfileProperties)
        {
            object? value = p.GetValue(defaults);
            JsonNode? node = stored[Key(p)] ?? stored[p.Name];
            if (node is not null)
            {
                try { value = node.Deserialize(p.PropertyType, Json); }
                catch { /* unreadable: the default stands */ }
            }
            if (value is null && p.PropertyType.IsValueType) continue;
            p.SetValue(settings, value);
        }
    }

    private static string Key(PropertyInfo p) => JsonNamingPolicy.CamelCase.ConvertName(p.Name);
}
