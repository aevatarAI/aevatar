using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>An OpenAPI <c>path</c>/<c>query</c>/<c>header</c> parameter for a connected-service tool.</summary>
public sealed record ConnectedServiceToolParameter(
    string Name,
    ParameterLocation In,
    bool Required,
    JsonNode? Schema,
    string? Description);

/// <summary>Where an OpenAPI parameter is carried.</summary>
public enum ParameterLocation
{
    Path,
    Query,
    Header,
}

/// <summary>
/// A single admitted OpenAPI operation, carrying everything needed to (a) emit a stable
/// tool parameter JSON Schema and (b) rebuild the downstream NyxID proxy request at
/// execution time. The request body slot is named <c>body</c> unless a path/query/header
/// parameter already claims that name, in which case it falls back to <c>request_body</c>.
/// </summary>
public sealed record ConnectedServiceToolOperation(
    string OperationId,
    string Method,
    string PathTemplate,
    string? Summary,
    AevatarToolMarker? Marker,
    IReadOnlyList<ConnectedServiceToolParameter> Parameters,
    JsonNode? RequestBodySchema,
    bool RequestBodyRequired)
{
    public bool HasRequestBody => RequestBodySchema is not null;

    /// <summary>The tool-argument property under which the JSON request body is supplied.</summary>
    public string? BodyPropertyName
    {
        get
        {
            if (!HasRequestBody)
                return null;
            var taken = Parameters.Any(p => string.Equals(p.Name, "body", StringComparison.Ordinal));
            return taken ? "request_body" : "body";
        }
    }

    /// <summary>Builds the <see cref="Aevatar.AI.Abstractions.ToolProviders.IAgentTool.ParametersSchema"/> JSON.</summary>
    public string BuildParametersSchema()
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in Parameters)
        {
            var schema = parameter.Schema?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "string" };
            if (!string.IsNullOrWhiteSpace(parameter.Description) && schema["description"] is null)
                schema["description"] = parameter.Description;

            properties[parameter.Name] = schema;
            if (parameter.Required || parameter.In == ParameterLocation.Path)
                required.Add(parameter.Name);
        }

        if (BodyPropertyName is { } bodyName)
        {
            properties[bodyName] = RequestBodySchema!.DeepClone();
            if (RequestBodyRequired)
                required.Add(bodyName);
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            root["required"] = required;

        return root.ToJsonString();
    }
}
