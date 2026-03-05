namespace Sisyphus.Application.Models.Graph;

/// <summary>
/// A filtered view of <see cref="GraphSnapshot"/> containing only nodes and edges
/// with <c>sisyphus_status == "purified"</c>.
/// </summary>
public sealed class BlueGraphSnapshot
{
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
}
