using System.Text.Json.Nodes;

namespace Aevatar.AI.ToolProviders.NyxId.ConnectedServices;

/// <summary>An OpenAPI <c>path</c>/<c>query</c>/<c>header</c> parameter for a connected-service tool.</summary>
public sealed record ConnectedServiceToolParameter(
    string Name,
    ParameterLocation In,
    bool Required,
    JsonNode? Schema,
    string? Description);

/// <summary>Where a normalized MCP operation parameter is carried.</summary>
public enum ParameterLocation
{
    Path,
    Query,
    Header,
}
