using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>
/// Expands document-local JSON pointers at the OpenAPI boundary. No remote resolution,
/// default insertion or removal of validation constraints is permitted.
/// One instance bounds the expansion work for one operation.
/// </summary>
internal sealed class NyxIdOpenApiReferenceResolver(JsonElement document)
{
    private const int MaxDepth = 48;
    private const int MaxNodes = 4096;
    private const int MaxCharacters = 1024 * 1024;
    private int _remainingNodes = MaxNodes;
    private int _remainingCharacters = MaxCharacters;

    public JsonElement ResolveObject(JsonElement value) =>
        JsonSerializer.SerializeToElement(ResolveReference(value, [], 0));

    public JsonNode ResolveSchema(JsonElement value) => ExpandSchema(value, [], 0);

    private JsonObject ExpandSchema(JsonElement value, HashSet<string> activeReferences, int depth)
    {
        var json = value.GetRawText();
        CheckBudget(depth, json.Length);
        if (value.ValueKind != JsonValueKind.Object)
            throw Unsupported();

        if (value.TryGetProperty("$ref", out var reference))
        {
            var pointer = ReadPointer(reference);
            if (!activeReferences.Add(pointer))
                throw Unsupported();
            try
            {
                var target = ExpandSchema(FindTarget(pointer), activeReferences, depth + 1);
                return MergeSiblings(target, ExpandSchema(WithoutReference(value), activeReferences, depth + 1));
            }
            finally
            {
                activeReferences.Remove(pointer);
            }
        }

        var result = JsonNode.Parse(json)!.AsObject();
        foreach (var property in value.EnumerateObject())
        {
            // Only schema positions are traversed. A literal "$ref" in an example
            // or default is data, and must survive unchanged.
            if (property.Name is "properties" or "patternProperties" or "$defs" or "definitions")
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw Unsupported();
                var properties = new JsonObject();
                foreach (var child in property.Value.EnumerateObject())
                    properties[child.Name] = ExpandSchema(child.Value, activeReferences, depth + 1);
                result[property.Name] = properties;
            }
            else if (property.Name is "items" or "additionalProperties" or "not" or "contains" &&
                     property.Value.ValueKind == JsonValueKind.Object)
            {
                result[property.Name] = ExpandSchema(property.Value, activeReferences, depth + 1);
            }
            else if (property.Name is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw Unsupported();
                var schemas = new JsonArray();
                foreach (var child in property.Value.EnumerateArray())
                    schemas.Add(ExpandSchema(child, activeReferences, depth + 1));
                result[property.Name] = schemas;
            }
        }
        return result;
    }

    private JsonObject ResolveReference(JsonElement value, HashSet<string> activeReferences, int depth)
    {
        var json = value.GetRawText();
        CheckBudget(depth, json.Length);
        if (value.ValueKind != JsonValueKind.Object)
            throw Unsupported();
        if (!value.TryGetProperty("$ref", out var reference))
            return JsonNode.Parse(json)!.AsObject();
        var pointer = ReadPointer(reference);
        if (!activeReferences.Add(pointer))
            throw Unsupported();
        try
        {
            return MergeSiblings(ResolveReference(FindTarget(pointer), activeReferences, depth + 1),
                JsonNode.Parse(WithoutReference(value).GetRawText())!.AsObject());
        }
        finally
        {
            activeReferences.Remove(pointer);
        }
    }

    private static JsonObject MergeSiblings(JsonObject target, JsonObject siblings)
    {
        foreach (var (name, value) in siblings)
        {
            // Validation siblings are conjunctive, not overrides. Reject ambiguous
            // intersections instead of silently weakening the referenced contract.
            if (target.TryGetPropertyValue(name, out var original) &&
                !JsonNode.DeepEquals(original, value) && name is not ("description" or "title" or "summary"))
                throw Unsupported();
            target[name] = value?.DeepClone();
        }
        return target;
    }

    private JsonElement FindTarget(string pointer)
    {
        var target = document;
        foreach (var encoded in pointer[2..].Split('/'))
        {
            for (var i = 0; i < encoded.Length; i++)
                if (encoded[i] == '~' && (++i >= encoded.Length || encoded[i] is not ('0' or '1')))
                    throw Unsupported();
            var name = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(name, out target))
                throw Unsupported();
        }
        return target;
    }

    private static string ReadPointer(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } pointer &&
        pointer.StartsWith("#/", StringComparison.Ordinal) && !pointer.Contains('%')
            ? pointer
            : throw Unsupported();

    private static JsonElement WithoutReference(JsonElement value)
    {
        var result = JsonNode.Parse(value.GetRawText())!.AsObject();
        result.Remove("$ref");
        return JsonSerializer.SerializeToElement(result);
    }

    private void CheckBudget(int depth, int characters)
    {
        _remainingCharacters -= characters;
        if (depth >= MaxDepth || --_remainingNodes < 0 || _remainingCharacters < 0)
            throw Unsupported();
    }

    private static NyxIdOperationSchemaUnsupportedException Unsupported() =>
        new("The OpenAPI local reference is missing, cyclic, external, conflicting, or exceeds the expansion limit.");
}
