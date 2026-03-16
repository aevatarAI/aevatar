using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;

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
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);
        ArgumentNullException.ThrowIfNull(plan);

        var graphScope = BuildGraphScope(plan.SchemaId);
        var ownerNodeId = BuildOwnerNodeId(plan.SchemaId, actorId);
        var nodes = new Dictionary<string, ProjectionGraphNode>(StringComparer.Ordinal)
        {
            [ownerNodeId] = new()
            {
                Scope = graphScope,
                NodeId = ownerNodeId,
                NodeType = NormalizeNodeType(plan.SchemaId, fallback: "script-read-model"),
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["schema_id"] = plan.SchemaId,
                    ["schema_version"] = plan.SchemaVersion,
                    ["schema_hash"] = plan.SchemaHash,
                    ["script_id"] = scriptId ?? string.Empty,
                    ["definition_actor_id"] = definitionActorId ?? string.Empty,
                    ["revision"] = revision ?? string.Empty,
                    ["actor_id"] = actorId,
                },
                UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs),
            },
        };
        var edges = new Dictionary<string, ProjectionGraphEdge>(StringComparer.Ordinal);

        foreach (var relation in plan.GraphRelations)
        {
            var rawValue = relation.SourceAccessor.ExtractValue(semanticReadModel);
            foreach (var relationValue in NormalizeRelationValues(rawValue))
            {
                var targetNodeId = BuildTargetNodeId(relation.TargetSchemaId ?? string.Empty, relationValue);
                if (!nodes.ContainsKey(targetNodeId))
                {
                    nodes[targetNodeId] = new ProjectionGraphNode
                    {
                        Scope = graphScope,
                        NodeId = targetNodeId,
                        NodeType = NormalizeNodeType(relation.TargetSchemaId, fallback: "external-reference"),
                        Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["schema_id"] = relation.TargetSchemaId ?? string.Empty,
                            ["target_path"] = relation.TargetPath ?? string.Empty,
                            ["target_key"] = relationValue,
                        },
                        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs),
                    };
                }

                var edgeId = BuildEdgeId(ownerNodeId, relation.Name ?? string.Empty, targetNodeId);
                edges[edgeId] = new ProjectionGraphEdge
                {
                    Scope = graphScope,
                    EdgeId = edgeId,
                    FromNodeId = ownerNodeId,
                    ToNodeId = targetNodeId,
                    EdgeType = NormalizeNodeType(relation.Name, fallback: "related_to"),
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["relation_name"] = relation.Name ?? string.Empty,
                        ["source_path"] = relation.SourcePath ?? string.Empty,
                        ["target_path"] = relation.TargetPath ?? string.Empty,
                        ["target_schema_id"] = relation.TargetSchemaId ?? string.Empty,
                        ["cardinality"] = relation.Cardinality ?? string.Empty,
                        ["target_key"] = relationValue,
                    },
                    UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs),
                };
            }
        }

        var readModel = new ScriptNativeGraphReadModel
        {
            Id = actorId,
            ScriptId = scriptId ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            Revision = revision ?? string.Empty,
            SchemaId = plan.SchemaId,
            SchemaVersion = plan.SchemaVersion,
            SchemaHash = plan.SchemaHash,
            GraphScope = graphScope,
            StateVersion = fact.StateVersion,
            LastEventId = sourceEventId,
            UpdatedAt = updatedAt,
        };
        readModel.GraphNodeEntries.Add(nodes.Values.Select(ScriptProjectionReadModelSupport.ToGraphNodeRecord));
        readModel.GraphEdgeEntries.Add(edges.Values.Select(ScriptProjectionReadModelSupport.ToGraphEdgeRecord));
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

    private static IEnumerable<string> NormalizeRelationValues(object? value)
    {
        if (value == null)
            yield break;

        if (value is string scalar)
        {
            var normalized = scalar.Trim();
            if (normalized.Length > 0)
                yield return normalized;
            yield break;
        }

        if (value is IEnumerable<object?> many)
        {
            foreach (var entry in many)
            {
                if (entry == null)
                    continue;

                var normalized = Convert.ToString(entry)?.Trim() ?? string.Empty;
                if (normalized.Length > 0)
                    yield return normalized;
            }

            yield break;
        }

        var single = Convert.ToString(value)?.Trim() ?? string.Empty;
        if (single.Length > 0)
            yield return single;
    }

    private static string BuildGraphScope(string schemaId)
    {
        var normalizedSchemaId = NormalizeNodeType(schemaId, fallback: "script");
        return $"script-native-{normalizedSchemaId}";
    }

    private static string BuildOwnerNodeId(string schemaId, string actorId) =>
        $"script:{NormalizeNodeType(schemaId, "script")}:{actorId}";

    private static string BuildTargetNodeId(string targetSchemaId, string targetKey) =>
        $"ref:{NormalizeNodeType(targetSchemaId, "external")}:{targetKey}";

    private static string BuildEdgeId(string ownerNodeId, string relationName, string targetNodeId) =>
        $"{ownerNodeId}:{NormalizeNodeType(relationName, "related_to")}:{targetNodeId}";

    private static string NormalizeNodeType(string? token, string fallback)
    {
        if (string.IsNullOrWhiteSpace(token))
            return fallback;

        var chars = token
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return normalized.Length == 0 ? fallback : normalized;
    }
}
