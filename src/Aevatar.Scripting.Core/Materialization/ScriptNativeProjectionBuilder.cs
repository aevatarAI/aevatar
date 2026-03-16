using Aevatar.Scripting.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Materialization;

public sealed class ScriptNativeProjectionBuilder : IScriptNativeProjectionBuilder
{
    public ScriptNativeDocumentProjection? BuildDocument(
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SupportsDocument || semanticReadModel == null)
            return null;

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in plan.DocumentFields)
        {
            var value = field.Accessor.ExtractValue(semanticReadModel);
            if (value == null)
                continue;

            AssignFieldValue(fields, field.Path, CloneObjectGraph(value));
        }

        return new ScriptNativeDocumentProjection
        {
            SchemaId = plan.SchemaId,
            SchemaVersion = plan.SchemaVersion,
            SchemaHash = plan.SchemaHash,
            DocumentIndexScope = plan.DocumentIndexScope,
            FieldsValue = ToStruct(fields),
        };
    }

    public ScriptNativeGraphProjection? BuildGraph(
        string actorId,
        string scriptId,
        string definitionActorId,
        string revision,
        IMessage? semanticReadModel,
        ScriptReadModelMaterializationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.SupportsGraph || semanticReadModel == null)
            return null;

        var graphScope = BuildGraphScope(plan.SchemaId);
        var ownerNodeId = BuildOwnerNodeId(plan.SchemaId, actorId);
        var nodes = new Dictionary<string, ScriptNativeGraphNodeProjection>(StringComparer.Ordinal)
        {
            [ownerNodeId] = new()
            {
                NodeId = ownerNodeId,
                NodeType = NormalizeToken(plan.SchemaId, fallback: "script-read-model"),
                Properties =
                {
                    ["schema_id"] = plan.SchemaId,
                    ["schema_version"] = plan.SchemaVersion,
                    ["schema_hash"] = plan.SchemaHash,
                    ["script_id"] = scriptId ?? string.Empty,
                    ["definition_actor_id"] = definitionActorId ?? string.Empty,
                    ["revision"] = revision ?? string.Empty,
                    ["actor_id"] = actorId,
                },
            },
        };
        var edges = new Dictionary<string, ScriptNativeGraphEdgeProjection>(StringComparer.Ordinal);

        foreach (var relation in plan.GraphRelations)
        {
            var rawValue = relation.SourceAccessor.ExtractValue(semanticReadModel);
            foreach (var relationValue in NormalizeRelationValues(rawValue))
            {
                var targetNodeId = BuildTargetNodeId(relation.TargetSchemaId ?? string.Empty, relationValue);
                if (!nodes.ContainsKey(targetNodeId))
                {
                    nodes[targetNodeId] = new ScriptNativeGraphNodeProjection
                    {
                        NodeId = targetNodeId,
                        NodeType = NormalizeToken(relation.TargetSchemaId, fallback: "external-reference"),
                        Properties =
                        {
                            ["schema_id"] = relation.TargetSchemaId ?? string.Empty,
                            ["target_path"] = relation.TargetPath ?? string.Empty,
                            ["target_key"] = relationValue,
                        },
                    };
                }

                var edgeId = BuildEdgeId(ownerNodeId, relation.Name ?? string.Empty, targetNodeId);
                edges[edgeId] = new ScriptNativeGraphEdgeProjection
                {
                    EdgeId = edgeId,
                    FromNodeId = ownerNodeId,
                    ToNodeId = targetNodeId,
                    EdgeType = NormalizeToken(relation.Name, fallback: "related_to"),
                    Properties =
                    {
                        ["relation_name"] = relation.Name ?? string.Empty,
                        ["source_path"] = relation.SourcePath ?? string.Empty,
                        ["target_path"] = relation.TargetPath ?? string.Empty,
                        ["target_schema_id"] = relation.TargetSchemaId ?? string.Empty,
                        ["cardinality"] = relation.Cardinality ?? string.Empty,
                        ["target_key"] = relationValue,
                    },
                };
            }
        }

        var projection = new ScriptNativeGraphProjection
        {
            SchemaId = plan.SchemaId,
            SchemaVersion = plan.SchemaVersion,
            SchemaHash = plan.SchemaHash,
            GraphScope = graphScope,
        };
        projection.NodeEntries.Add(nodes.Values);
        projection.EdgeEntries.Add(edges.Values);
        return projection;
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

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var entry in enumerable.Cast<object?>())
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

    private static string BuildGraphScope(string schemaId) =>
        $"script-native-{NormalizeToken(schemaId, "script")}";

    private static string BuildOwnerNodeId(string schemaId, string actorId) =>
        $"script:{NormalizeToken(schemaId, "script")}:{actorId}";

    private static string BuildTargetNodeId(string targetSchemaId, string targetKey) =>
        $"ref:{NormalizeToken(targetSchemaId, "external")}:{targetKey}";

    private static string BuildEdgeId(string ownerNodeId, string relationName, string targetNodeId) =>
        $"{ownerNodeId}:{NormalizeToken(relationName, "related_to")}:{targetNodeId}";

    private static string NormalizeToken(string? token, string fallback)
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

    private static object? CloneObjectGraph(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            bool boolean => boolean,
            byte byteValue => byteValue,
            sbyte signedByte => signedByte,
            short shortValue => shortValue,
            ushort ushortValue => ushortValue,
            int intValue => intValue,
            uint uintValue => uintValue,
            long longValue => longValue,
            ulong ulongValue => ulongValue,
            float floatValue => floatValue,
            double doubleValue => doubleValue,
            decimal decimalValue => decimalValue,
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            IReadOnlyDictionary<string, object?> readonlyDictionary => readonlyDictionary.ToDictionary(
                static pair => pair.Key,
                static pair => CloneObjectGraph(pair.Value),
                StringComparer.Ordinal),
            IDictionary<string, object?> dictionary => dictionary.ToDictionary(
                static pair => pair.Key,
                static pair => CloneObjectGraph(pair.Value),
                StringComparer.Ordinal),
            IEnumerable<object?> sequence => sequence.Select(CloneObjectGraph).ToList(),
            System.Collections.IEnumerable sequence when value is not string => sequence
                .Cast<object?>()
                .Select(CloneObjectGraph)
                .ToList(),
            _ => value,
        };
    }

    private static Struct ToStruct(IDictionary<string, object?>? source)
    {
        var result = new Struct();
        if (source == null)
            return result;

        foreach (var (key, rawValue) in source)
            result.Fields[key] = ToValue(rawValue);

        return result;
    }

    private static Value ToValue(object? value)
    {
        var result = new Value();
        switch (value)
        {
            case null:
                result.NullValue = NullValue.NullValue;
                return result;
            case string text:
                result.StringValue = text;
                return result;
            case bool boolean:
                result.BoolValue = boolean;
                return result;
            case byte byteValue:
                result.NumberValue = byteValue;
                return result;
            case sbyte signedByte:
                result.NumberValue = signedByte;
                return result;
            case short shortValue:
                result.NumberValue = shortValue;
                return result;
            case ushort ushortValue:
                result.NumberValue = ushortValue;
                return result;
            case int intValue:
                result.NumberValue = intValue;
                return result;
            case uint unsignedIntValue:
                result.NumberValue = unsignedIntValue;
                return result;
            case long longValue:
                result.NumberValue = longValue;
                return result;
            case ulong unsignedLongValue:
                result.NumberValue = unsignedLongValue;
                return result;
            case float floatValue:
                result.NumberValue = floatValue;
                return result;
            case double doubleValue:
                result.NumberValue = doubleValue;
                return result;
            case decimal decimalValue:
                result.NumberValue = (double)decimalValue;
                return result;
            case DateTimeOffset dateTimeOffset:
                result.StringValue = dateTimeOffset.ToString("O");
                return result;
            case IReadOnlyDictionary<string, object?> readonlyDictionary:
                result.StructValue = ToStruct(readonlyDictionary.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal));
                return result;
            case IDictionary<string, object?> dictionary:
                result.StructValue = ToStruct(dictionary);
                return result;
            case IEnumerable<object?> sequence:
                result.ListValue = new ListValue();
                result.ListValue.Values.Add(sequence.Select(ToValue));
                return result;
            case System.Collections.IEnumerable sequence:
                result.ListValue = new ListValue();
                foreach (var entry in sequence.Cast<object?>())
                    result.ListValue.Values.Add(ToValue(entry));
                return result;
            default:
                result.StringValue = Convert.ToString(value) ?? string.Empty;
                return result;
        }
    }
}
