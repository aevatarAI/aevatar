using System.Text.Json;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Tools;

internal sealed class ToolArgs
{
    private readonly Dictionary<string, JsonElement> _props;
    private readonly string _raw;

    private ToolArgs(Dictionary<string, JsonElement> props, string raw)
    {
        _props = props;
        _raw = raw;
    }

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

    public string? Str(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText().Trim('"');
    }

    public string Str(string name, string defaultValue) => Str(name) ?? defaultValue;

    public JsonElement? Element(string name) =>
        _props.TryGetValue(name, out var el) ? el : null;

    public string? RawOrStr(string name)
    {
        if (!_props.TryGetValue(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }

    public string Raw => _raw;

    public bool Has(string name) => _props.ContainsKey(name);
}
