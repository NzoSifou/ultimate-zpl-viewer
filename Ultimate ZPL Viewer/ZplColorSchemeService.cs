using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ultimate_ZPL_Viewer;

public sealed class ZplColorSchemeConfig
{
    [JsonPropertyName("commandColor")]
    public string CommandColor { get; set; } = "#FF4444";

    [JsonPropertyName("parameterColor")]
    public string ParameterColor { get; set; } = "#4488FF";

    [JsonPropertyName("textColor")]
    public string TextColor { get; set; } = "#FFFFFF";

    [JsonPropertyName("textParameters")]
    public List<string> TextParameters { get; set; } = ["data", "comment"];

    [JsonPropertyName("commands")]
    public List<ZplCommandDef> Commands { get; set; } = [];
}

public sealed class ZplCommandDef
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("alternativeCommand")]
    public string? AlternativeCommand { get; set; }

    [JsonPropertyName("shortDescription")]
    public string? ShortDescription { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public List<ZplParamDef>? Parameters { get; set; }
}

public sealed class ZplParamDef
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "string"; // "string" | "number"

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    public bool IsNumber => string.Equals(Type, "number", StringComparison.OrdinalIgnoreCase);
}

public static class ZplColorSchemeService
{
    private static ZplColorSchemeConfig? _config;

    public static ZplColorSchemeConfig Config => _config ??= Load();

    public static string UserConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ultimate ZPL Viewer",
        "zpl_color_scheme.json");

    public static void EnsureUserConfig()
    {
        if (File.Exists(UserConfigPath)) return;

        var dir = Path.GetDirectoryName(UserConfigPath)!;
        Directory.CreateDirectory(dir);

        string packagePath;
        try   { packagePath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path; }
        catch { packagePath = AppContext.BaseDirectory; }

        var candidates = new[]
        {
            Path.Combine(packagePath,                            "Assets", "zpl_color_scheme_default.json"),
            Path.Combine(AppContext.BaseDirectory,               "Assets", "zpl_color_scheme_default.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory,  "Assets", "zpl_color_scheme_default.json"),
        };

        foreach (var src in candidates)
        {
            if (!File.Exists(src)) continue;
            File.Copy(src, UserConfigPath);
            break;
        }
    }

    // Validates the user JSON against the bundled schema. Returns null when it
    // conforms (or when the schema asset can't be found — validation is skipped
    // rather than blocking the app), otherwise a French, multi-line description
    // of the problems. Ensures the file exists first (copies the default).
    public static string? ValidateUserConfig()
    {
        EnsureUserConfig();

        string userJson;
        try { userJson = File.ReadAllText(UserConfigPath); }
        catch (Exception ex) { return $"Impossible de lire le fichier : {ex.Message}"; }

        var schemaText = ReadSchema();
        if (schemaText is null) return null; // schema asset missing → don't block

        JsonDocument instance;
        try
        {
            instance = JsonDocument.Parse(userJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return $"Le fichier n'est pas un JSON valide : {ex.Message}";
        }

        using (instance)
        {
            JsonDocument schemaDoc;
            try { schemaDoc = JsonDocument.Parse(schemaText); }
            catch { return null; } // malformed bundled schema → skip

            using (schemaDoc)
            {
                var errors = JsonSchemaValidator.Validate(schemaDoc.RootElement, instance.RootElement);
                if (errors.Count == 0) return null;

                const int max = 15;
                var lines = new List<string>();
                for (int i = 0; i < errors.Count && i < max; i++) lines.Add("• " + errors[i]);
                var msg = string.Join("\n", lines);
                if (errors.Count > max) msg += $"\n… et {errors.Count - max} autre(s) erreur(s).";
                return msg;
            }
        }
    }

    // Reads the JSON schema shipped in Assets. Tolerant of the two names the file
    // has had (with a dot vs an underscore) and of packaged / unpackaged layouts.
    private static string? ReadSchema()
    {
        string packagePath;
        try   { packagePath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path; }
        catch { packagePath = AppContext.BaseDirectory; }

        string[] bases = { packagePath, AppContext.BaseDirectory, AppDomain.CurrentDomain.BaseDirectory };
        string[] names = { "zpl_color_scheme.schema.json", "zpl_color_scheme_schema.json" };

        foreach (var b in bases)
            foreach (var n in names)
            {
                var path = Path.Combine(b, "Assets", n);
                if (File.Exists(path))
                {
                    try { return File.ReadAllText(path); } catch { }
                }
            }
        return null;
    }

    public static Windows.UI.Color ParseHexColor(string hex, Windows.UI.Color fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 3)
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length == 8) // #AARRGGBB
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(hex[0..2], 16),
                    Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16),
                    Convert.ToByte(hex[6..8], 16));
            if (hex.Length != 6) return fallback;
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }
        catch { return fallback; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static ZplColorSchemeConfig Load()
    {
        EnsureUserConfig();
        try
        {
            var json = File.ReadAllText(UserConfigPath);
            return JsonSerializer.Deserialize<ZplColorSchemeConfig>(json, JsonOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    // Updates the three highlighting colors in memory and invalidates the
    // highlighter (cheap — safe to call live while dragging a color picker).
    public static void SetColors(string commandHex, string parameterHex, string textHex)
    {
        var cfg = Config;
        cfg.CommandColor   = commandHex;
        cfg.ParameterColor = parameterHex;
        cfg.TextColor      = textHex;
        ZplHighlighter.Invalidate();
    }

    // Writes the current config to the user JSON (call debounced — the file is large).
    // Nulls are omitted so optional fields (alternativeCommand, parameters) stay
    // absent rather than being written as null, which the schema forbids.
    private static readonly JsonSerializerOptions PersistOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void PersistColors()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, PersistOptions);
            File.WriteAllText(UserConfigPath, json);
        }
        catch { /* keep the in-memory change even if the file write fails */ }
    }
}

// Builds ZPL decoration messages for the Monaco Editor bridge.
// All data structures are built ONCE per app session from the loaded color scheme config.
public static class ZplHighlighter
{
    private static Regex? _commandRegex;
    private static Dictionary<string, ZplCommandDef>? _lookup;
    private static HashSet<string>? _textParams;
    private static Windows.UI.Color _commandColor;
    private static Windows.UI.Color _parameterColor;
    private static Windows.UI.Color _textColor;

    // Forces a rebuild on the next use (after the color scheme changed).
    public static void Invalidate() => _commandRegex = null;

    // Current highlighting colors (built from the config).
    public static Windows.UI.Color CommandColor   { get { EnsureBuilt(); return _commandColor; } }
    public static Windows.UI.Color ParameterColor { get { EnsureBuilt(); return _parameterColor; } }
    public static Windows.UI.Color TextColor      { get { EnsureBuilt(); return _textColor; } }

    // Exposed for the static analyzer: known commands (including alternative forms).
    internal static Dictionary<string, ZplCommandDef> Lookup
    {
        get { EnsureBuilt(); return _lookup!; }
    }

    // Exposed for the static analyzer: parameter names that hold free text (data, comment).
    internal static HashSet<string> TextParams
    {
        get { EnsureBuilt(); return _textParams!; }
    }

    private static void EnsureBuilt()
    {
        if (_commandRegex is not null) return;

        var cfg = ZplColorSchemeService.Config;

        _textParams     = new HashSet<string>(cfg.TextParameters, StringComparer.OrdinalIgnoreCase);
        _commandColor   = ZplColorSchemeService.ParseHexColor(cfg.CommandColor,   Colors.Tomato);
        _parameterColor = ZplColorSchemeService.ParseHexColor(cfg.ParameterColor, Colors.CornflowerBlue);
        _textColor      = ZplColorSchemeService.ParseHexColor(cfg.TextColor,      Colors.White);

        // Index both the primary command and its '~' alternative form, so ~CC
        // resolves to the same definition as ^CC.
        _lookup = new Dictionary<string, ZplCommandDef>(StringComparer.Ordinal);
        foreach (var c in cfg.Commands)
        {
            _lookup.TryAdd(c.Command, c);
            if (!string.IsNullOrEmpty(c.AlternativeCommand))
                _lookup.TryAdd(c.AlternativeCommand, c);
        }

        if (_lookup.Count == 0)
        {
            _commandRegex = new Regex(@"(?!)", RegexOptions.Compiled);
            return;
        }

        // Sort longest first so ^A@ is matched before ^A, ^BC before ^B, etc.
        var alternation = string.Join("|", _lookup.Keys
            .Select(Regex.Escape)
            .OrderByDescending(s => s.Length));

        _commandRegex = new Regex($@"({alternation})([^\^~\r\n]*)", RegexOptions.Compiled);
    }

    // Fills a per-character color array without touching the document.
    private static Windows.UI.Color[] BuildColorMap(string text)
    {
        var colors = new Windows.UI.Color[text.Length];
        Array.Fill(colors, _textColor);

        foreach (Match m in _commandRegex!.Matches(text))
        {
            var cmdStr    = m.Groups[1].Value;
            var argsStr   = m.Groups[2].Value;
            int cmdStart  = m.Index;
            int argsStart = cmdStart + m.Groups[1].Length;

            // Command keyword → commandColor
            for (int i = cmdStart; i < argsStart && i < colors.Length; i++)
                colors[i] = _commandColor;

            if (argsStr.Length == 0) continue;

            _lookup!.TryGetValue(cmdStr, out var def);
            var parameters = def?.Parameters ?? [];

            int paramIndex = 0;
            int segStart   = argsStart;

            for (int i = 0; i <= argsStr.Length; i++)
            {
                bool boundary = i == argsStr.Length || argsStr[i] == ',';
                if (!boundary) continue;

                int segEnd    = argsStart + i;
                var paramName = paramIndex < parameters.Count ? parameters[paramIndex].Name : null;
                bool isText   = paramName is null || _textParams!.Contains(paramName);
                var color     = isText ? _textColor : _parameterColor;

                for (int j = segStart; j < segEnd && j < colors.Length; j++)
                    colors[j] = color;

                // Comma takes the color of the segment it closes
                if (i < argsStr.Length && argsStart + i < colors.Length)
                    colors[argsStart + i] = color;

                segStart = argsStart + i + 1;
                paramIndex++;
            }
        }

        return colors;
    }

    // Returns the JSON message that sets the three CSS color classes in Monaco.
    // Colors too close to the editor background are adapted so they stay readable
    // (the default scheme uses white text, invisible on the light theme).
    public static string GetColorsJson(bool darkTheme)
    {
        EnsureBuilt();
        var cmd   = AdaptToTheme(_commandColor,   darkTheme);
        var param = AdaptToTheme(_parameterColor, darkTheme);
        var text  = AdaptToTheme(_textColor,      darkTheme);
        return $"{{\"type\":\"setColors\",\"cmd\":\"{ColorToHex(cmd)}\",\"param\":\"{ColorToHex(param)}\",\"text\":\"{ColorToHex(text)}\"}}";
    }

    private static Windows.UI.Color AdaptToTheme(Windows.UI.Color c, bool darkTheme)
    {
        var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        if (!darkTheme && luminance > 0.75)
            return Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A); // near-white → dark text
        if (darkTheme && luminance < 0.25)
            return Windows.UI.Color.FromArgb(255, 0xF0, 0xF0, 0xF0); // near-black → light text
        return c;
    }

    // Returns the JSON message that applies run-length-encoded decoration ranges in Monaco.
    public static string GetDecorationsJson(string text)
    {
        EnsureBuilt();
        if (string.IsNullOrEmpty(text))
            return "{\"type\":\"applyDecorations\",\"runs\":[]}";

        var colors = BuildColorMap(text);
        var sb = new StringBuilder("{\"type\":\"applyDecorations\",\"runs\":[");
        bool first = true;
        int runStart = 0;
        var runColor = colors[0];
        for (int i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && colors[i] == runColor) continue;
            var cssClass = runColor == _commandColor   ? "zpl-cmd"
                         : runColor == _parameterColor ? "zpl-param"
                         : "zpl-text";
            if (!first) sb.Append(',');
            sb.Append($"{{\"s\":{runStart},\"e\":{i},\"c\":\"{cssClass}\"}}");
            first = false;
            runStart = i;
            if (i < text.Length) runColor = colors[i];
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string ColorToHex(Windows.UI.Color c)
        => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
