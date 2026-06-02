using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>
/// Resolves local <c>#/components/schemas/{name}</c> <c>$ref</c> pointers in an OpenAPI
/// schema into a self-contained JSON Schema, so a generated tool's parameter schema needs
/// no external resolution by the LLM provider. Cycles are broken with an open
/// <c>{"type":"object"}</c> placeholder and resolution is depth-bounded as a backstop.
/// </summary>
internal sealed class OpenApiSchemaInliner
{
    private const string RefPrefix = "#/components/schemas/";
    private const int MaxDepth = 64;

    private readonly IReadOnlyDictionary<string, JsonElement> _schemas;

    private OpenApiSchemaInliner(IReadOnlyDictionary<string, JsonElement> schemas)
    {
        _schemas = schemas;
    }

    public static OpenApiSchemaInliner FromDocument(JsonElement root)
    {
        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("components", out var components) &&
            components.ValueKind == JsonValueKind.Object &&
            components.TryGetProperty("schemas", out var schemaMap) &&
            schemaMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in schemaMap.EnumerateObject())
                schemas[prop.Name] = prop.Value.Clone();
        }

        return new OpenApiSchemaInliner(schemas);
    }

    /// <summary>Returns a self-contained <see cref="JsonNode"/> clone of <paramref name="schema"/>.</summary>
    public JsonNode? Inline(JsonElement schema) =>
        Resolve(schema, new HashSet<string>(StringComparer.Ordinal), 0);

    private JsonNode? Resolve(JsonElement node, HashSet<string> visiting, int depth)
    {
        if (depth > MaxDepth)
            return new JsonObject { ["type"] = "object" };

        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetRefName(node, out var refName))
                {
                    // OpenAPI 3.0 ignores keywords that sit beside a $ref, so the ref target
                    // replaces the whole node. An unresolvable or recursive ref collapses to an
                    // open object rather than recursing forever.
                    if (!visiting.Add(refName) || !_schemas.TryGetValue(refName, out var target))
                        return new JsonObject { ["type"] = "object" };

                    var resolved = Resolve(target, visiting, depth + 1);
                    visiting.Remove(refName);
                    return resolved;
                }

                var obj = new JsonObject();
                foreach (var prop in node.EnumerateObject())
                    obj[prop.Name] = Resolve(prop.Value, visiting, depth + 1);
                return obj;

            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in node.EnumerateArray())
                    arr.Add(Resolve(item, visiting, depth + 1));
                return arr;

            default:
                return JsonNode.Parse(node.GetRawText());
        }
    }

    private static bool TryGetRefName(JsonElement obj, out string name)
    {
        name = string.Empty;
        if (!obj.TryGetProperty("$ref", out var refValue) || refValue.ValueKind != JsonValueKind.String)
            return false;

        var refStr = refValue.GetString();
        if (string.IsNullOrEmpty(refStr) || !refStr.StartsWith(RefPrefix, StringComparison.Ordinal))
            return false;

        name = Uri.UnescapeDataString(refStr[RefPrefix.Length..]);
        return !string.IsNullOrEmpty(name);
    }
}
