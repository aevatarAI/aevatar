using System.Text.Json.Serialization;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Projection.ReadModels;

[JsonConverter(typeof(ScriptReadModelDocumentJsonConverter))]
public sealed partial class ScriptReadModelDocument
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptReadModelDocument>
{
    [JsonIgnore]
    public string ActorId => Id;

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public ScriptReadModelDocument DeepClone() => Clone();
}

[JsonConverter(typeof(ScriptDefinitionSnapshotDocumentJsonConverter))]
public sealed partial class ScriptDefinitionSnapshotDocument
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptDefinitionSnapshotDocument>
{
    [JsonIgnore]
    public string ActorId => DefinitionActorId;

    [JsonIgnore]
    public DateTimeOffset CreatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public ScriptDefinitionSnapshotDocument DeepClone() => Clone();
}

[JsonConverter(typeof(ScriptCatalogEntryDocumentJsonConverter))]
public sealed partial class ScriptCatalogEntryDocument
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptCatalogEntryDocument>
{
    [JsonIgnore]
    public string ActorId => CatalogActorId;

    [JsonIgnore]
    public DateTimeOffset CreatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(CreatedAtUtcValue);
        set => CreatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    [JsonIgnore]
    public IList<string> RevisionHistory
    {
        get => RevisionHistoryEntries;
        set => ScriptProjectionReadModelSupport.ReplaceCollection(RevisionHistoryEntries, value);
    }

    public ScriptCatalogEntryDocument DeepClone() => Clone();
}

[JsonConverter(typeof(ScriptEvolutionReadModelJsonConverter))]
public sealed partial class ScriptEvolutionReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptEvolutionReadModel>
{
    [JsonIgnore]
    public IList<string> Diagnostics
    {
        get => DiagnosticsEntries;
        set => ScriptProjectionReadModelSupport.ReplaceCollection(DiagnosticsEntries, value);
    }

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public ScriptEvolutionReadModel DeepClone() => Clone();
}

[JsonConverter(typeof(ScriptNativeDocumentReadModelJsonConverter))]
public sealed partial class ScriptNativeDocumentReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptNativeDocumentReadModel>
{
    private IDictionary<string, object?>? _fieldsCache;

    [JsonIgnore]
    public string ActorId => Id;

    [JsonIgnore]
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

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public ScriptNativeDocumentReadModel DeepClone() => Clone();
}

public sealed partial class ScriptNativeGraphReadModel
    : IProjectionReadModel,
      IProjectionReadModelCloneable<ScriptNativeGraphReadModel>
{
    [JsonIgnore]
    public string ActorId => Id;

    [JsonIgnore]
    public DateTimeOffset UpdatedAt
    {
        get => ScriptProjectionReadModelSupport.ToDateTimeOffset(UpdatedAtUtcValue);
        set => UpdatedAtUtcValue = ScriptProjectionReadModelSupport.ToTimestamp(value);
    }

    public ScriptNativeGraphReadModel DeepClone() => Clone();
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

    public static object? CloneObjectGraph(object? value)
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

    public static object? ReadJsonValue(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    static property => ReadJsonValue(property.Value),
                    StringComparer.Ordinal),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray()
                .Select(ReadJsonValue)
                .ToList(),
            System.Text.Json.JsonValueKind.String => element.TryGetDateTimeOffset(out var dateTimeOffset)
                ? dateTimeOffset
                : element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => null,
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
