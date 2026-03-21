using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.ReadModels;

public sealed partial class ScriptReadModelDocument : IProjectionReadModel<ScriptReadModelDocument>
{
    public string ActorId => Id;

    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ScriptDefinitionSnapshotDocument : IProjectionReadModel<ScriptDefinitionSnapshotDocument>
{
    public string ActorId => DefinitionActorId;

    public DateTimeOffset CreatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ScriptCatalogEntryDocument : IProjectionReadModel<ScriptCatalogEntryDocument>
{
    public string ActorId => CatalogActorId;

    public DateTimeOffset CreatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public IList<string> RevisionHistory
    {
        get => RevisionHistoryEntries;
        set => ScriptProjectionReadModelSupport.ReplaceCollection(RevisionHistoryEntries, value);
    }
}

public sealed partial class ScriptEvolutionReadModel : IProjectionReadModel<ScriptEvolutionReadModel>
{
    public IList<string> Diagnostics
    {
        get => DiagnosticsEntries;
        set => ScriptProjectionReadModelSupport.ReplaceCollection(DiagnosticsEntries, value);
    }

    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ScriptNativeDocumentReadModel : IProjectionReadModel<ScriptNativeDocumentReadModel>
{
    private IDictionary<string, object?>? _fieldsCache;

    public string ActorId => Id;

    public IDictionary<string, object?> Fields
    {
        get => _fieldsCache ??= ScriptProjectionReadModelSupport.ToDictionary(FieldsValue);
        set
        {
            _fieldsCache = value == null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(value, StringComparer.Ordinal);
            FieldsValue = ScriptProjectionReadModelSupport.ToStruct(_fieldsCache);
        }
    }

    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }
}

public sealed partial class ScriptNativeGraphReadModel : IProjectionReadModel<ScriptNativeGraphReadModel>
{
    public string ActorId => Id;

    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }
}

internal static class ScriptProjectionReadModelSupport
{
    public static Timestamp ToTimestamp(DateTimeOffset value) =>
        Timestamp.FromDateTimeOffset(value.ToUniversalTime());

    public static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value == null ? default : value.ToDateTimeOffset();

    public static void ReplaceCollection<T>(RepeatedField<T> target, IEnumerable<T>? source)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();
        if (source != null)
            target.Add(source);
    }

    public static Struct ToStruct(IDictionary<string, object?>? source)
    {
        var result = new Struct();
        if (source == null)
            return result;

        foreach (var (key, rawValue) in source)
            result.Fields[key] = ToValue(rawValue);

        return result;
    }

    public static Dictionary<string, object?> ToDictionary(Struct? source)
    {
        if (source == null)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        return source.Fields.ToDictionary(
            static pair => pair.Key,
            static pair => ToObject(pair.Value),
            StringComparer.Ordinal);
    }

    public static ScriptNativeGraphNodeRecord ToGraphNodeRecord(ProjectionGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new ScriptNativeGraphNodeRecord
        {
            Scope = node.Scope,
            NodeId = node.NodeId,
            NodeType = node.NodeType,
            Properties =
            {
                node.Properties,
            },
            UpdatedAtUtcValue = ToTimestamp(node.UpdatedAt),
        };
    }

    public static ProjectionGraphNode ToProjectionGraphNode(ScriptNativeGraphNodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return new ProjectionGraphNode
        {
            Scope = node.Scope,
            NodeId = node.NodeId,
            NodeType = node.NodeType,
            Properties = new Dictionary<string, string>(node.Properties, StringComparer.Ordinal),
            UpdatedAt = ToDateTimeOffset(node.UpdatedAtUtcValue),
        };
    }

    public static ScriptNativeGraphEdgeRecord ToGraphEdgeRecord(ProjectionGraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        return new ScriptNativeGraphEdgeRecord
        {
            Scope = edge.Scope,
            EdgeId = edge.EdgeId,
            FromNodeId = edge.FromNodeId,
            ToNodeId = edge.ToNodeId,
            EdgeType = edge.EdgeType,
            Properties =
            {
                edge.Properties,
            },
            UpdatedAtUtcValue = ToTimestamp(edge.UpdatedAt),
        };
    }

    public static ProjectionGraphEdge ToProjectionGraphEdge(ScriptNativeGraphEdgeRecord edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        return new ProjectionGraphEdge
        {
            Scope = edge.Scope,
            EdgeId = edge.EdgeId,
            FromNodeId = edge.FromNodeId,
            ToNodeId = edge.ToNodeId,
            EdgeType = edge.EdgeType,
            Properties = new Dictionary<string, string>(edge.Properties, StringComparer.Ordinal),
            UpdatedAt = ToDateTimeOffset(edge.UpdatedAtUtcValue),
        };
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

    private static object? ToObject(Value? value)
    {
        return value?.KindCase switch
        {
            Value.KindOneofCase.StructValue => ToDictionary(value.StructValue),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(ToObject).ToList(),
            Value.KindOneofCase.NumberValue => ToNumber(value.NumberValue),
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.NullValue => null,
            _ => null,
        };
    }

    private static object ToNumber(double value)
    {
        var truncated = Math.Truncate(value);
        if (Math.Abs(value - truncated) < 0.0000001d &&
            truncated <= long.MaxValue &&
            truncated >= long.MinValue)
        {
            return (long)truncated;
        }

        return value;
    }
}
