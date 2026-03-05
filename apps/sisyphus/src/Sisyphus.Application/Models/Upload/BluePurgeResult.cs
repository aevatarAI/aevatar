using System.Text.Json.Serialization;

namespace Sisyphus.Application.Models.Upload;

public sealed class NodePurgeResult
{
    [JsonPropertyName("blue_nodes")]
    public List<BlueNodeOutput> BlueNodes { get; set; } = [];

    [JsonPropertyName("blue_edges")]
    public List<BlueEdgeOutput> BlueEdges { get; set; } = [];
}

public sealed class BlueNodeOutput
{
    [JsonPropertyName("temp_id")]
    public string TempId { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("abstract")]
    public string Abstract { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    /// <summary>Assigned by chrono-graph after write.</summary>
    [JsonIgnore]
    public string? GraphUuid { get; set; }
}

public sealed class BlueEdgeOutput
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("source_id")]
    public string? SourceId { get; set; }

    [JsonPropertyName("target_id")]
    public string? TargetId { get; set; }

    [JsonPropertyName("edge_type")]
    public string EdgeType { get; set; } = "";
}

public sealed class EdgePurgeResult
{
    [JsonPropertyName("blue_edges")]
    public List<BlueEdgeOutput> BlueEdges { get; set; } = [];
}

// Batch result wrappers
public sealed class BatchNodePurgeResult
{
    [JsonPropertyName("results")]
    public List<BatchNodePurgeEntry> Results { get; set; } = [];
}

public sealed class BatchNodePurgeEntry
{
    [JsonPropertyName("kg_id")]
    public string KgId { get; set; } = "";

    [JsonPropertyName("blue_nodes")]
    public List<BlueNodeOutput> BlueNodes { get; set; } = [];

    [JsonPropertyName("blue_edges")]
    public List<BlueEdgeOutput> BlueEdges { get; set; } = [];
}

public sealed class BatchEdgePurgeResult
{
    [JsonPropertyName("results")]
    public List<BatchEdgePurgeEntry> Results { get; set; } = [];
}

public sealed class BatchEdgePurgeEntry
{
    [JsonPropertyName("source_kg_id")]
    public string SourceKgId { get; set; } = "";

    [JsonPropertyName("target_kg_id")]
    public string TargetKgId { get; set; } = "";

    [JsonPropertyName("blue_edges")]
    public List<BlueEdgeOutput> BlueEdges { get; set; } = [];
}
