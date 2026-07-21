using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>The outcome of parsing one proxy-aware OpenAPI document for tool admission.</summary>
public sealed record ConnectedServiceSpecParseResult(
    AevatarToolMarker? ServiceMarker,
    IReadOnlyList<ConnectedServiceToolOperation> Operations)
{
    public static ConnectedServiceSpecParseResult Empty { get; } = new(null, []);

    /// <summary>
    /// Applies the explicit allow-list. An operation is eligible only when it carries an
    /// enabled operation-level marker, or inherits an enabled service-level marker without
    /// an operation-level opt-out. Absence of any marker means "not eligible".
    /// </summary>
    public IEnumerable<ConnectedServiceToolOperation> AdmittedOperations()
    {
        var serviceEnabled = ServiceMarker is { Enabled: true };
        foreach (var operation in Operations)
        {
            var admitted = operation.Marker is { } marker ? marker.Enabled : serviceEnabled;
            if (admitted)
                yield return operation;
        }
    }
}

/// <summary>
/// Parses a NyxID proxy-aware OpenAPI document into the operations Aevatar may register as
/// LLM tools. The parser only extracts structure (markers, parameters, request-body schema);
/// admission is decided by <see cref="ConnectedServiceSpecParseResult.AdmittedOperations"/>.
/// </summary>
public static class OpenApiToolSpecParser
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "put", "post", "delete", "patch", "head", "options",
    };
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Content-Type", "If-Match", "If-None-Match",
    };

    public static ConnectedServiceSpecParseResult Parse(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
            return ConnectedServiceSpecParseResult.Empty;

        try
        {
            using var doc = JsonDocument.Parse(specJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ConnectedServiceSpecParseResult.Empty;

            var serviceMarker = AevatarToolMarker.FromOwner(root);
            if (serviceMarker is null &&
                root.TryGetProperty("info", out var info) &&
                info.ValueKind == JsonValueKind.Object)
            {
                serviceMarker = AevatarToolMarker.FromOwner(info);
            }

            if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                return new ConnectedServiceSpecParseResult(serviceMarker, []);

            var inliner = OpenApiSchemaInliner.FromDocument(root);
            var components = root.TryGetProperty("components", out var c) && c.ValueKind == JsonValueKind.Object
                ? c
                : default;

            var operations = new List<ConnectedServiceToolOperation>();
            foreach (var pathEntry in paths.EnumerateObject())
            {
                if (pathEntry.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var pathItem = pathEntry.Value;
                var sharedParameters = ParseParameters(pathItem, inliner, components);

                foreach (var methodEntry in pathItem.EnumerateObject())
                {
                    if (!HttpMethods.Contains(methodEntry.Name) ||
                        methodEntry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var parsed = BuildOperation(
                        pathEntry.Name,
                        methodEntry.Name.ToUpperInvariant(),
                        methodEntry.Value,
                        sharedParameters,
                        inliner,
                        components);
                    if (parsed is not null)
                        operations.Add(parsed);
                }
            }

            return new ConnectedServiceSpecParseResult(serviceMarker, operations);
        }
        catch (JsonException)
        {
            return ConnectedServiceSpecParseResult.Empty;
        }
    }

    private static ConnectedServiceToolOperation? BuildOperation(
        string path,
        string method,
        JsonElement operation,
        IReadOnlyList<ConnectedServiceToolParameter> sharedParameters,
        OpenApiSchemaInliner inliner,
        JsonElement components)
    {
        var operationId = operation.TryGetProperty("operationId", out var oid) &&
                          oid.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrWhiteSpace(oid.GetString())
            ? oid.GetString()!.Trim()
            : $"{method}_{path}";

        var summary = operation.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : operation.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()?.Split('\n', 2)[0]
                : null;

        var parameters = MergeParameters(sharedParameters, ParseParameters(operation, inliner, components));
        if (parameters.Any(parameter =>
                parameter.In == ParameterLocation.Header &&
                parameter.Required &&
                !AllowedHeaders.Contains(parameter.Name)))
        {
            return null;
        }
        parameters = parameters
            .Where(parameter => parameter.In != ParameterLocation.Header || AllowedHeaders.Contains(parameter.Name))
            .ToArray();
        var (bodySchema, bodyRequired, bodyMediaType, unsupportedRequiredBody) =
            ParseRequestBody(operation, inliner, components);
        if (unsupportedRequiredBody)
            return null;

        return new ConnectedServiceToolOperation(
            operationId,
            method,
            path,
            string.IsNullOrWhiteSpace(summary) ? null : summary!.Trim(),
            AevatarToolMarker.FromOwner(operation),
            parameters,
            bodySchema,
            bodyRequired,
            bodyMediaType);
    }

    private static IReadOnlyList<ConnectedServiceToolParameter> MergeParameters(
        IReadOnlyList<ConnectedServiceToolParameter> shared,
        IReadOnlyList<ConnectedServiceToolParameter> operationLevel)
    {
        if (operationLevel.Count == 0)
            return shared;
        if (shared.Count == 0)
            return operationLevel;

        var merged = new Dictionary<(string, ParameterLocation), ConnectedServiceToolParameter>();
        foreach (var parameter in shared)
            merged[(parameter.Name, parameter.In)] = parameter;
        foreach (var parameter in operationLevel)
            merged[(parameter.Name, parameter.In)] = parameter;
        return merged.Values.ToArray();
    }

    private static IReadOnlyList<ConnectedServiceToolParameter> ParseParameters(
        JsonElement owner,
        OpenApiSchemaInliner inliner,
        JsonElement components)
    {
        if (!owner.TryGetProperty("parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ConnectedServiceToolParameter>();
        foreach (var parameterElement in parameters.EnumerateArray())
        {
            var resolved = ResolveComponentRef(parameterElement, components, "parameters");
            if (resolved.ValueKind != JsonValueKind.Object)
                continue;

            if (!resolved.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!resolved.TryGetProperty("in", out var inEl) || inEl.ValueKind != JsonValueKind.String)
                continue;
            if (!TryMapLocation(inEl.GetString(), out var location))
                continue;

            var required = location == ParameterLocation.Path ||
                           (resolved.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True);

            var schema = resolved.TryGetProperty("schema", out var schemaEl)
                ? inliner.Inline(schemaEl)
                : null;

            var description = resolved.TryGetProperty("description", out var descEl) &&
                              descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString()
                : null;

            results.Add(new ConnectedServiceToolParameter(name!.Trim(), location, required, schema, description));
        }

        return results;
    }

    private static (System.Text.Json.Nodes.JsonNode? Schema, bool Required, string? MediaType, bool UnsupportedRequired)
        ParseRequestBody(
        JsonElement operation,
        OpenApiSchemaInliner inliner,
        JsonElement components)
    {
        if (!operation.TryGetProperty("requestBody", out var requestBody))
            return (null, false, null, false);

        var resolved = ResolveComponentRef(requestBody, components, "requestBodies");
        if (resolved.ValueKind != JsonValueKind.Object)
            return (null, false, null, false);

        var required = resolved.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.True;

        if (!resolved.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
            return (null, false, null, required);

        if (!TryGetJsonContentSchema(content, out var schemaEl))
            return (null, required, null, required);

        return (inliner.Inline(schemaEl), required, "application/json", false);
    }

    private static bool TryGetJsonContentSchema(JsonElement content, out JsonElement schema)
    {
        schema = default;
        foreach (var media in content.EnumerateObject())
        {
            if (string.Equals(media.Name, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                if (media.Value.ValueKind != JsonValueKind.Object ||
                    !media.Value.TryGetProperty("schema", out var schemaEl))
                {
                    return false;
                }

                schema = schemaEl;
                return true;
            }
        }
        return false;
    }

    private static JsonElement ResolveComponentRef(JsonElement node, JsonElement components, string section)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("$ref", out var refEl) ||
            refEl.ValueKind != JsonValueKind.String)
        {
            return node;
        }

        var prefix = $"#/components/{section}/";
        var refStr = refEl.GetString();
        if (string.IsNullOrEmpty(refStr) || !refStr.StartsWith(prefix, StringComparison.Ordinal))
            return node;

        if (components.ValueKind != JsonValueKind.Object ||
            !components.TryGetProperty(section, out var sectionMap) ||
            sectionMap.ValueKind != JsonValueKind.Object)
        {
            return node;
        }

        var name = Uri.UnescapeDataString(refStr[prefix.Length..]);
        return sectionMap.TryGetProperty(name, out var target) ? target : node;
    }

    private static bool TryMapLocation(string? value, out ParameterLocation location)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "path":
                location = ParameterLocation.Path;
                return true;
            case "query":
                location = ParameterLocation.Query;
                return true;
            case "header":
                location = ParameterLocation.Header;
                return true;
            default:
                location = ParameterLocation.Query;
                return false;
        }
    }
}
