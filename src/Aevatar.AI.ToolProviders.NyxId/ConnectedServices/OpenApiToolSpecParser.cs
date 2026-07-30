using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>The outcome of parsing one proxy-aware OpenAPI document for tool admission.</summary>
public sealed record ConnectedServiceSpecParseResult(
    AevatarToolMarker? ServiceMarker,
    IReadOnlyList<ConnectedServiceToolOperation> Operations,
    IReadOnlyList<ConnectedServiceSpecIssue> Issues,
    IReadOnlyList<string> DeclaredOperationIds)
{
    public static ConnectedServiceSpecParseResult Empty { get; } = new(null, [], [], []);

    public bool HasFatalIdentityAmbiguity => Issues.Any(static issue =>
        issue.Kind == ConnectedServiceSpecIssueKind.DuplicateOperationId);

    public bool HasDeclaredOperation(string? operationId) =>
        !string.IsNullOrWhiteSpace(operationId) &&
        DeclaredOperationIds.Contains(operationId, StringComparer.Ordinal);

    public ConnectedServiceSpecIssue? FindIssue(
        string? operationId,
        params ConnectedServiceSpecIssueKind[] kinds)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return null;

        var acceptedKinds = kinds.ToHashSet();
        return Issues.FirstOrDefault(issue =>
            acceptedKinds.Contains(issue.Kind) &&
            string.Equals(issue.OperationId, operationId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Applies the explicit allow-list. An operation is eligible only when it carries an
    /// enabled operation-level marker, or inherits an enabled service-level marker without
    /// an operation-level opt-out. Absence of any marker means "not eligible".
    /// </summary>
    public IEnumerable<ConnectedServiceToolOperation> AdmittedOperations()
    {
        if (HasFatalIdentityAmbiguity)
            yield break;

        var serviceEnabled = ServiceMarker is { Enabled: true };
        foreach (var operation in Operations)
        {
            var admitted = operation.Marker is { } marker ? marker.Enabled : serviceEnabled;
            if (admitted && !operation.Parameters.Any(static parameter =>
                    parameter.In == ParameterLocation.Header &&
                    NyxIdProxyHeaderPolicy.IsSensitive(parameter.Name)))
            {
                yield return operation;
            }
        }
    }
}

public enum ConnectedServiceSpecIssueKind
{
    InvalidDocument = 0,
    MissingOperationId = 1,
    DuplicateOperationId = 2,
    RequiredHeaderNotAllowed = 3,
    RequiredRequestBodyUnsupported = 4,
}

public sealed record ConnectedServiceSpecIssue(
    ConnectedServiceSpecIssueKind Kind,
    string? OperationId,
    string Method,
    string PathTemplate);

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
                return new ConnectedServiceSpecParseResult(serviceMarker, [], [], []);

            var inliner = OpenApiSchemaInliner.FromDocument(root);
            var components = root.TryGetProperty("components", out var c) && c.ValueKind == JsonValueKind.Object
                ? c
                : default;

            var operations = new List<ConnectedServiceToolOperation>();
            var issues = new List<ConnectedServiceSpecIssue>();
            var declaredOperationIds = new List<string>();
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

                    var method = methodEntry.Name.ToUpperInvariant();
                    var operationId = ReadOperationId(methodEntry.Value);
                    if (operationId is null)
                    {
                        issues.Add(new ConnectedServiceSpecIssue(
                            ConnectedServiceSpecIssueKind.MissingOperationId,
                            null,
                            method,
                            pathEntry.Name));
                        continue;
                    }

                    declaredOperationIds.Add(operationId);
                    var parsed = BuildOperation(
                        pathEntry.Name,
                        method,
                        operationId,
                        methodEntry.Value,
                        sharedParameters,
                        inliner,
                        components,
                        issues);
                    if (parsed is not null)
                        operations.Add(parsed);
                }
            }

            foreach (var duplicateOperationId in declaredOperationIds
                         .GroupBy(static operationId => operationId, StringComparer.Ordinal)
                         .Where(static group => group.Count() > 1)
                         .Select(static group => group.Key)
                         .OrderBy(static operationId => operationId, StringComparer.Ordinal))
            {
                issues.Add(new ConnectedServiceSpecIssue(
                    ConnectedServiceSpecIssueKind.DuplicateOperationId,
                    duplicateOperationId,
                    string.Empty,
                    string.Empty));
            }

            return new ConnectedServiceSpecParseResult(
                serviceMarker,
                operations,
                issues,
                declaredOperationIds.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (JsonException)
        {
            return new ConnectedServiceSpecParseResult(
                null,
                [],
                [new ConnectedServiceSpecIssue(
                    ConnectedServiceSpecIssueKind.InvalidDocument,
                    null,
                    string.Empty,
                    string.Empty)],
                []);
        }
    }

    private static string? ReadOperationId(JsonElement operation)
    {
        if (!operation.TryGetProperty("operationId", out var operationId) ||
            operationId.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var normalized = operationId.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static ConnectedServiceToolOperation? BuildOperation(
        string path,
        string method,
        string operationId,
        JsonElement operation,
        IReadOnlyList<ConnectedServiceToolParameter> sharedParameters,
        OpenApiSchemaInliner inliner,
        JsonElement components,
        ICollection<ConnectedServiceSpecIssue> issues)
    {
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
            issues.Add(new ConnectedServiceSpecIssue(
                ConnectedServiceSpecIssueKind.RequiredHeaderNotAllowed,
                operationId,
                method,
                path));
            return null;
        }
        parameters = parameters
            .Where(parameter => parameter.In != ParameterLocation.Header || AllowedHeaders.Contains(parameter.Name))
            .ToArray();
        var (bodySchema, bodyRequired, bodyMediaType, unsupportedRequiredBody) =
            ParseRequestBody(operation, inliner, components);
        if (unsupportedRequiredBody)
        {
            issues.Add(new ConnectedServiceSpecIssue(
                ConnectedServiceSpecIssueKind.RequiredRequestBodyUnsupported,
                operationId,
                method,
                path));
            return null;
        }

        return new ConnectedServiceToolOperation(
            operationId,
            method,
            path,
            string.IsNullOrWhiteSpace(summary) ? null : summary!.Trim(),
            AevatarToolMarker.FromOwner(operation),
            parameters,
            bodySchema,
            bodyRequired,
            bodyMediaType,
            ParseResponseMediaTypes(operation));
    }

    private static IReadOnlyList<string> ParseResponseMediaTypes(JsonElement operation)
    {
        if (!operation.TryGetProperty("responses", out var responses) ||
            responses.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return responses.EnumerateObject()
            .Where(static response =>
                response.Name == "default" ||
                response.Name.Length == 3 && response.Name[0] == '2')
            .SelectMany(static response =>
                response.Value.ValueKind == JsonValueKind.Object &&
                response.Value.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Object
                    ? content.EnumerateObject().Select(static media => media.Name)
                    : [])
            .Where(static mediaType => !string.IsNullOrWhiteSpace(mediaType))
            .Select(static mediaType => mediaType.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static mediaType => mediaType, StringComparer.Ordinal)
            .ToArray();
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
            return (null, false, null, true);

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
