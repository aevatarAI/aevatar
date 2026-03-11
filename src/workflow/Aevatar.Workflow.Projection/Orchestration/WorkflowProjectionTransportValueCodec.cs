using System.Collections;
using System.Globalization;
using System.Reflection;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Workflow.Projection.Transport;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowProjectionTransportValueCodec
{
    public static WorkflowProjectionValue Serialize(object? value) =>
        value == null
            ? CreateNullValue()
            : SerializeCore(value);

    public static Value SerializeLegacy(object? value) =>
        value == null
            ? new Value { NullValue = NullValue.NullValue }
            : SerializeLegacyCore(value);

    public static object? Deserialize(WorkflowProjectionValue? value)
    {
        if (value == null)
            return null;

        return value.KindCase switch
        {
            WorkflowProjectionValue.KindOneofCase.NullValue => null,
            WorkflowProjectionValue.KindOneofCase.StringValue => value.StringValue,
            WorkflowProjectionValue.KindOneofCase.BoolValue => value.BoolValue,
            WorkflowProjectionValue.KindOneofCase.Int64Value => NormalizeInt64(value.Int64Value),
            WorkflowProjectionValue.KindOneofCase.Uint64Value => NormalizeUInt64(value.Uint64Value),
            WorkflowProjectionValue.KindOneofCase.DoubleValue => value.DoubleValue,
            WorkflowProjectionValue.KindOneofCase.DecimalValue => decimal.Parse(value.DecimalValue, CultureInfo.InvariantCulture),
            WorkflowProjectionValue.KindOneofCase.ObjectValue => value.ObjectValue.Fields.ToDictionary(
                x => x.Key,
                x => Deserialize(x.Value),
                StringComparer.Ordinal),
            WorkflowProjectionValue.KindOneofCase.ListValue => value.ListValue.Values.Select(Deserialize).ToList(),
            _ => null,
        };
    }

    public static object? DeserializeLegacy(Value? value)
    {
        if (value == null)
            return null;

        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.NumberValue => NormalizeLegacyNumber(value.NumberValue),
            Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(
                x => x.Key,
                x => DeserializeLegacy(x.Value),
                StringComparer.Ordinal),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(DeserializeLegacy).ToList(),
            _ => null,
        };
    }

    private static WorkflowProjectionValue SerializeCore(object value)
    {
        return value switch
        {
            WorkflowProjectionValue projectionValue => projectionValue.Clone(),
            WorkflowProjectionObject projectionObject => new WorkflowProjectionValue { ObjectValue = projectionObject.Clone() },
            WorkflowProjectionList projectionList => new WorkflowProjectionValue { ListValue = projectionList.Clone() },
            Value protobufValue => Serialize(DeserializeLegacy(protobufValue)),
            Struct protobufStruct => Serialize(DeserializeLegacy(new Value { StructValue = protobufStruct.Clone() })),
            ListValue protobufList => Serialize(DeserializeLegacy(new Value { ListValue = protobufList.Clone() })),
            string text => new WorkflowProjectionValue { StringValue = text },
            bool boolean => new WorkflowProjectionValue { BoolValue = boolean },
            byte number => new WorkflowProjectionValue { Int64Value = number },
            sbyte number => new WorkflowProjectionValue { Int64Value = number },
            short number => new WorkflowProjectionValue { Int64Value = number },
            ushort number => new WorkflowProjectionValue { Uint64Value = number },
            int number => new WorkflowProjectionValue { Int64Value = number },
            uint number => new WorkflowProjectionValue { Uint64Value = number },
            long number => new WorkflowProjectionValue { Int64Value = number },
            ulong number => new WorkflowProjectionValue { Uint64Value = number },
            float number => new WorkflowProjectionValue { DoubleValue = number },
            double number => new WorkflowProjectionValue { DoubleValue = number },
            decimal number => new WorkflowProjectionValue
            {
                DecimalValue = number.ToString("G29", CultureInfo.InvariantCulture),
            },
            System.Enum enumValue => new WorkflowProjectionValue { StringValue = enumValue.ToString() },
            Guid guid => new WorkflowProjectionValue { StringValue = guid.ToString("D") },
            DateTimeOffset dateTimeOffset => new WorkflowProjectionValue
            {
                StringValue = dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            },
            DateTime dateTime => new WorkflowProjectionValue
            {
                StringValue = ToUtc(dateTime).ToString("O", CultureInfo.InvariantCulture),
            },
            Uri uri => new WorkflowProjectionValue { StringValue = uri.ToString() },
            IDictionary dictionary => new WorkflowProjectionValue { ObjectValue = SerializeDictionary(dictionary) },
            IEnumerable enumerable when value is not string => new WorkflowProjectionValue { ListValue = SerializeList(enumerable) },
            _ => new WorkflowProjectionValue { ObjectValue = SerializeObject(value) },
        };
    }

    private static Value SerializeLegacyCore(object value)
    {
        return value switch
        {
            Value protobufValue => protobufValue.Clone(),
            Struct protobufStruct => new Value { StructValue = protobufStruct.Clone() },
            ListValue protobufList => new Value { ListValue = protobufList.Clone() },
            string text => new Value { StringValue = text },
            bool boolean => new Value { BoolValue = boolean },
            byte number => new Value { NumberValue = number },
            sbyte number => new Value { NumberValue = number },
            short number => new Value { NumberValue = number },
            ushort number => new Value { NumberValue = number },
            int number => new Value { NumberValue = number },
            uint number => new Value { NumberValue = number },
            long number => new Value { NumberValue = number },
            ulong number => new Value { NumberValue = number },
            float number => new Value { NumberValue = number },
            double number => new Value { NumberValue = number },
            decimal number => new Value { NumberValue = Convert.ToDouble(number, CultureInfo.InvariantCulture) },
            System.Enum enumValue => new Value { StringValue = enumValue.ToString() },
            Guid guid => new Value { StringValue = guid.ToString("D") },
            DateTimeOffset dateTimeOffset => new Value
            {
                StringValue = dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            },
            DateTime dateTime => new Value
            {
                StringValue = ToUtc(dateTime).ToString("O", CultureInfo.InvariantCulture),
            },
            Uri uri => new Value { StringValue = uri.ToString() },
            IDictionary dictionary => new Value { StructValue = SerializeLegacyDictionary(dictionary) },
            IEnumerable enumerable when value is not string => new Value { ListValue = SerializeLegacyList(enumerable) },
            _ => new Value { StructValue = SerializeLegacyObject(value) },
        };
    }

    private static WorkflowProjectionObject SerializeDictionary(IDictionary dictionary)
    {
        var result = new WorkflowProjectionObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key == null)
                continue;

            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            result.Fields[key] = Serialize(entry.Value);
        }

        return result;
    }

    private static WorkflowProjectionList SerializeList(IEnumerable enumerable)
    {
        var result = new WorkflowProjectionList();
        foreach (var item in enumerable)
            result.Values.Add(Serialize(item));

        return result;
    }

    private static WorkflowProjectionObject SerializeObject(object value)
    {
        var result = new WorkflowProjectionObject();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;

            result.Fields[property.Name] = Serialize(property.GetValue(value));
        }

        return result;
    }

    private static Struct SerializeLegacyDictionary(IDictionary dictionary)
    {
        var result = new Struct();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key == null)
                continue;

            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            result.Fields[key] = SerializeLegacy(entry.Value);
        }

        return result;
    }

    private static ListValue SerializeLegacyList(IEnumerable enumerable)
    {
        var result = new ListValue();
        foreach (var item in enumerable)
            result.Values.Add(SerializeLegacy(item));

        return result;
    }

    private static Struct SerializeLegacyObject(object value)
    {
        var result = new Struct();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;

            result.Fields[property.Name] = SerializeLegacy(property.GetValue(value));
        }

        return result;
    }

    private static object NormalizeLegacyNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Truncate(value) != value)
            return value;

        if (value >= int.MinValue && value <= int.MaxValue)
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);

        if (value >= long.MinValue && value <= long.MaxValue)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);

        return value;
    }

    private static object NormalizeInt64(long value) =>
        value >= int.MinValue && value <= int.MaxValue
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : value;

    private static object NormalizeUInt64(ulong value) =>
        value <= uint.MaxValue
            ? Convert.ToUInt32(value, CultureInfo.InvariantCulture)
            : value;

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

    private static WorkflowProjectionValue CreateNullValue() =>
        new()
        {
            NullValue = NullValue.NullValue,
        };
}
