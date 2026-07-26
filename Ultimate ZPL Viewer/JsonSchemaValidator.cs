using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ultimate_ZPL_Viewer;

// Minimal JSON Schema validator covering the subset of Draft 2020-12 used by
// the ZPL color-scheme schema: type, properties, required, pattern, enum, items.
// Dependency-free (no NuGet), reads the actual schema document so it stays in
// sync with Assets\zpl_color_scheme.schema.json. Returns human-readable,
// French, path-anchored error messages.
public static class JsonSchemaValidator
{
    public static List<string> Validate(JsonElement schema, JsonElement instance)
    {
        var errors = new List<string>();
        ValidateNode(schema, instance, "racine", errors);
        return errors;
    }

    private static void ValidateNode(JsonElement schema, JsonElement inst, string path, List<string> errors)
    {
        // type — a mismatch makes the remaining keywords meaningless, so stop here.
        if (schema.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
        {
            var expected = typeEl.GetString();
            if (!MatchesType(expected, inst))
            {
                errors.Add($"{path} : type attendu « {expected} », trouvé « {ActualType(inst)} ».");
                return;
            }
        }

        // enum — value must be one of the allowed constants.
        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            bool ok = false;
            foreach (var allowed in enumEl.EnumerateArray())
                if (JsonEquals(allowed, inst)) { ok = true; break; }
            if (!ok)
                errors.Add($"{path} : valeur « {Raw(inst)} » non autorisée.");
        }

        // pattern — regex constraint on strings.
        if (inst.ValueKind == JsonValueKind.String
            && schema.TryGetProperty("pattern", out var patEl)
            && patEl.ValueKind == JsonValueKind.String)
        {
            var pattern = patEl.GetString();
            if (!string.IsNullOrEmpty(pattern) && !SafeIsMatch(inst.GetString() ?? "", pattern))
                errors.Add($"{path} : « {inst.GetString()} » ne respecte pas le format requis ({pattern}).");
        }

        // object — required properties + recurse into declared properties.
        if (inst.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
                foreach (var r in reqEl.EnumerateArray())
                {
                    var name = r.GetString();
                    if (name is not null && !inst.TryGetProperty(name, out _))
                        errors.Add($"{path} : propriété obligatoire « {name} » manquante.");
                }

            if (schema.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object)
                foreach (var prop in propsEl.EnumerateObject())
                    if (inst.TryGetProperty(prop.Name, out var childInst))
                        ValidateNode(prop.Value, childInst, $"{path}.{prop.Name}", errors);
        }

        // array — validate each element against the items schema.
        if (inst.ValueKind == JsonValueKind.Array
            && schema.TryGetProperty("items", out var itemsEl)
            && itemsEl.ValueKind == JsonValueKind.Object)
        {
            int i = 0;
            foreach (var el in inst.EnumerateArray())
                ValidateNode(itemsEl, el, $"{path}[{i++}]", errors);
        }
    }

    private static bool MatchesType(string? expected, JsonElement inst) => expected switch
    {
        "object"  => inst.ValueKind == JsonValueKind.Object,
        "array"   => inst.ValueKind == JsonValueKind.Array,
        "string"  => inst.ValueKind == JsonValueKind.String,
        "boolean" => inst.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number"  => inst.ValueKind == JsonValueKind.Number,
        "integer" => inst.ValueKind == JsonValueKind.Number && inst.TryGetInt64(out _),
        "null"    => inst.ValueKind == JsonValueKind.Null,
        _         => true, // unknown/absent type keyword → no constraint
    };

    private static string ActualType(JsonElement inst) => inst.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array  => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null   => "null",
        _                    => "inconnu",
    };

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    private static string Raw(JsonElement inst)
        => inst.ValueKind == JsonValueKind.String ? inst.GetString() ?? "" : inst.GetRawText();

    private static bool SafeIsMatch(string value, string pattern)
    {
        try { return Regex.IsMatch(value, pattern); }
        catch (ArgumentException) { return true; } // bad pattern in schema → don't flag the data
        catch (RegexMatchTimeoutException) { return true; }
    }
}
