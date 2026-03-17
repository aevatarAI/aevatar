using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Materialization;

public sealed class ScriptNativeGraphMaterializer
    : IScriptNativeGraphMaterializer,
      IProjectionGraphMaterializer<ScriptNativeGraphReadModel>
{
    public ScriptNativeGraphReadModel Materialize(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        ScriptDomainFactCommitted fact,
        string sourceEventId,
        DateTimeOffset updatedAt,
        ScriptNativeGraphProjection nativeGraph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        ArgumentNullException.ThrowIfNull(nativeGraph);

        var readModel = new ScriptNativeGraphReadModel
        {
            Id = actorId,
            ScriptId = scriptId ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            Revision = revision ?? string.Empty,
            SchemaId = nativeGraph.SchemaId ?? string.Empty,
            SchemaVersion = nativeGraph.SchemaVersion ?? string.Empty,
            SchemaHash = nativeGraph.SchemaHash ?? string.Empty,
            GraphScope = nativeGraph.GraphScope ?? string.Empty,
            StateVersion = fact.StateVersion,
            LastEventId = sourceEventId,
            UpdatedAt = updatedAt,
        };
        readModel.GraphNodeEntries.Add(nativeGraph.NodeEntries.Select(node => new ScriptNativeGraphNodeRecord
        {
            Scope = nativeGraph.GraphScope ?? string.Empty,
            NodeId = node.NodeId ?? string.Empty,
            NodeType = node.NodeType ?? string.Empty,
            Properties = { node.Properties },
            UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(updatedAt),
        }));
        readModel.GraphEdgeEntries.Add(nativeGraph.EdgeEntries.Select(edge => new ScriptNativeGraphEdgeRecord
        {
            Scope = nativeGraph.GraphScope ?? string.Empty,
            EdgeId = edge.EdgeId ?? string.Empty,
            FromNodeId = edge.FromNodeId ?? string.Empty,
            ToNodeId = edge.ToNodeId ?? string.Empty,
            EdgeType = edge.EdgeType ?? string.Empty,
            Properties = { edge.Properties },
            UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(updatedAt),
        }));
        return readModel;
    }

    public ProjectionGraphMaterialization Materialize(ScriptNativeGraphReadModel readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);

        return new ProjectionGraphMaterialization
        {
            Scope = readModel.GraphScope ?? string.Empty,
            Nodes = readModel.GraphNodeEntries
                .Select(ScriptProjectionReadModelSupport.ToProjectionGraphNode)
                .ToArray(),
            Edges = readModel.GraphEdgeEntries
                .Select(ScriptProjectionReadModelSupport.ToProjectionGraphEdge)
                .ToArray(),
        };
    }

}
