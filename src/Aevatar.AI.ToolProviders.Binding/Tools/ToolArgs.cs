using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

internal sealed class ToolArgs
{
    private readonly Dictionary<string, JsonElement> _props;
    private readonly string? _parseError;

    private ToolArgs(Dictionary<string, JsonElement> props, string? parseError = null)
    {
        _props = props;
        _parseError = parseError;
    }

    /// <summary>Parse error message, if the input JSON was malformed.</summary>
    public string? ParseError => _parseError;

    public static ToolArgs Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ToolArgs([]);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return new ToolArgs(dict);
        }
        catch (JsonException ex)
        {
            return new ToolArgs([], $"Invalid arguments JSON: {ex.Message}");
        }
    }

    public string? Str(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    public int Int(string name, int defaultValue)
    {
        if (!_props.TryGetValue(name, out var el)) return defaultValue;
        return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : defaultValue;
    }

    public bool Bool(string name, bool defaultValue)
    {
        if (!_props.TryGetValue(name, out var el)) return defaultValue;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : defaultValue,
            _ => defaultValue,
        };
    }

    public string? RawOrStr(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }

    public string[] StrArray(string name)
    {
        if (!_props.TryGetValue(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }
}
