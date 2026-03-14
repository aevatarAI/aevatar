using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Materialization;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;

namespace Aevatar.Scripting.Projection.Materialization;

public sealed class ScriptNativeDocumentMaterializer : IScriptNativeDocumentMaterializer
{
    public ScriptNativeDocumentReadModel Materialize(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        ScriptDomainFactCommitted fact,
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(plan);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in plan.DocumentFields)
        {
            var value = field.Accessor.ExtractValue(semanticReadModel);
            if (value == null)
                continue;

            AssignFieldValue(fields, field.Path, ScriptProjectionReadModelSupport.CloneObjectGraph(value));
        }

        return new ScriptNativeDocumentReadModel
        {
            Id = actorId,
            ScriptId = scriptId ?? string.Empty,
            DefinitionActorId = definitionActorId ?? string.Empty,
            Revision = revision ?? string.Empty,
            SchemaId = plan.SchemaId,
            SchemaVersion = plan.SchemaVersion,
            SchemaHash = plan.SchemaHash,
            DocumentIndexScope = plan.DocumentIndexScope,
            Fields = fields,
            StateVersion = fact.StateVersion,
            LastEventId = string.IsNullOrWhiteSpace(fact.EventType)
                ? fact.DomainEventPayload?.TypeUrl ?? string.Empty
                : fact.EventType,
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(fact.OccurredAtUnixTimeMs),
            DocumentMetadata = CloneMetadata(plan.DocumentMetadata),
        };
    }

    private static void AssignFieldValue(
        IDictionary<string, object?> current,
        string path,
        object? value)
    {
        var segments = path
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return;

        IDictionary<string, object?> cursor = current;
        for (var i = 0; i < segments.Length; i++)
        {
            var rawSegment = segments[i];
            var segment = rawSegment.EndsWith("[]", StringComparison.Ordinal)
                ? rawSegment[..^2]
                : rawSegment;
            if (segment.Length == 0)
                return;

            var isLeaf = i == segments.Length - 1;
            if (isLeaf)
            {
                cursor[segment] = value;
                return;
            }

            if (!cursor.TryGetValue(segment, out var existing) ||
                existing is not IDictionary<string, object?> existingMap)
            {
                existingMap = new Dictionary<string, object?>(StringComparer.Ordinal);
                cursor[segment] = existingMap;
            }

            cursor = existingMap;
        }
    }

    private static DocumentIndexMetadata CloneMetadata(ScriptMaterializedDocumentMetadata metadata)
    {
        return new DocumentIndexMetadata(
            metadata.IndexName,
            CloneDictionary(metadata.Mappings),
            CloneDictionary(metadata.Settings),
            CloneDictionary(metadata.Aliases));
    }

    private static IReadOnlyDictionary<string, object?> CloneDictionary(
        IReadOnlyDictionary<string, object?> source)
    {
        return source.ToDictionary(
            static pair => pair.Key,
            static pair => ScriptProjectionReadModelSupport.CloneObjectGraph(pair.Value),
            StringComparer.Ordinal);
    }
}
