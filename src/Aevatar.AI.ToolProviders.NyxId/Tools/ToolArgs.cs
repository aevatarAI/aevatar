using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

/// <summary>
/// Case-insensitive argument parser for NyxID tools.
/// Normalizes all property names to lowercase for reliable lookup regardless of
/// how the LLM serializes argument keys (camelCase, PascalCase, snake_case).
/// </summary>
internal sealed class ToolArgs
{
    private readonly Dictionary<string, JsonElement> _props;
    private readonly string _raw;

    private ToolArgs(Dictionary<string, JsonElement> props, string raw)
    {
        _props = props;
        _raw = raw;
    }

    /// <summary>Parse JSON arguments from the LLM. Never throws.</summary>
    public static ToolArgs Parse(string? json)
    {
        var raw = json ?? "{}";
        try
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new ToolArgs([], raw);

            using var doc = JsonDocument.Parse(raw);
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return new ToolArgs(dict, raw);
        }
        catch
        {
            return new ToolArgs([], raw);
        }
    }

    /// <summary>Get a string property. Returns null if missing or not a string.</summary>
    public string? Str(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText().Trim('"');
    }

    /// <summary>Get a string property with a default value.</summary>
    public string Str(string name, string defaultValue) => Str(name) ?? defaultValue;

    /// <summary>Get a boolean property. Returns null if missing.</summary>
    public bool? Bool(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    /// <summary>Get a raw JSON element (for objects/arrays). Returns null if missing.</summary>
    public JsonElement? Element(string name) =>
        _props.TryGetValue(name, out var el) ? el : null;

    /// <summary>Get the raw JSON string for body passthrough. Handles string or object values.</summary>
    public string? RawOrStr(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }

    /// <summary>Get headers dictionary from an object property.</summary>
    public Dictionary<string, string>? Headers(string name = "headers")
    {
        if (!_props.TryGetValue(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;
        var dict = new Dictionary<string, string>();
        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? "";
        return dict;
    }

    /// <summary>The original raw JSON string, useful for error messages.</summary>
    public string Raw => _raw;

    /// <summary>Whether a property exists.</summary>
    public bool Has(string name) => _props.ContainsKey(name);
}
