using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sisyphus.Application.Models.Graph;

public sealed class GraphSnapshot
{
    [JsonPropertyName("nodes")]
    public List<GraphNode> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<GraphEdge> Edges { get; set; } = [];
}

public sealed class GraphNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement> Properties { get; set; } = [];
}

public sealed class GraphEdge
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement> Properties { get; set; } = [];
}
