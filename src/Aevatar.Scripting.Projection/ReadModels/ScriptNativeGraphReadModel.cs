using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Scripting.Projection.ReadModels;

public sealed class ScriptNativeGraphReadModel : IGraphReadModel
{
    public string Id { get; set; } = string.Empty;

    public string ScriptId { get; set; } = string.Empty;

    public string DefinitionActorId { get; set; } = string.Empty;

    public string Revision { get; set; } = string.Empty;

    public string SchemaId { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string SchemaHash { get; set; } = string.Empty;

    public string GraphScope { get; set; } = string.Empty;

    public IReadOnlyList<ProjectionGraphNode> GraphNodes { get; set; } = [];

    public IReadOnlyList<ProjectionGraphEdge> GraphEdges { get; set; } = [];

    public long StateVersion { get; set; }

    public string LastEventId { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
