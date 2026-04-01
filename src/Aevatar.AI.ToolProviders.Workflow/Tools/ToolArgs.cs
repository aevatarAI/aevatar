using System.Text.Json;

namespace Aevatar.AI.ToolProviders.Workflow.Tools;

/// <summary>Case-insensitive argument parser for Workflow tools.</summary>
internal sealed class ToolArgs
{
    private readonly Dictionary<string, JsonElement> _props;

    private ToolArgs(Dictionary<string, JsonElement> props) => _props = props;

    public static ToolArgs Parse(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ToolArgs([]);

            using var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();
            return new ToolArgs(dict);
        }
        catch
        {
            return new ToolArgs([]);
        }
    }

    public string? Str(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText().Trim('"');
    }

    public string Str(string name, string defaultValue) => Str(name) ?? defaultValue;

    public int? Int(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;
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

    public bool Has(string name) => _props.ContainsKey(name);
}
