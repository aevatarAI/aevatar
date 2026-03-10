using System.Collections;
using System.Globalization;
using System.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

internal static class WorkflowProjectionTransportValueCodec
{
    public static Value Serialize(object? value) =>
        value == null
            ? CreateNullValue()
            : SerializeCore(value);

    public static object? Deserialize(Value? value)
    {
        if (value == null)
            return null;

        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.NumberValue => NormalizeNumber(value.NumberValue),
            Value.KindOneofCase.StructValue => value.StructValue.Fields.ToDictionary(
                x => x.Key,
                x => Deserialize(x.Value),
                StringComparer.Ordinal),
            Value.KindOneofCase.ListValue => value.ListValue.Values.Select(Deserialize).ToList(),
            _ => null,
        };
    }

    private static Value SerializeCore(object value)
    {
        return value switch
        {
            Value protobufValue => protobufValue.Clone(),
            Struct protobufStruct => new Value { StructValue = protobufStruct.Clone() },
            ListValue protobufList => new Value { ListValue = protobufList.Clone() },
            string text => Value.ForString(text),
            bool boolean => Value.ForBool(boolean),
            byte number => Value.ForNumber(number),
            sbyte number => Value.ForNumber(number),
            short number => Value.ForNumber(number),
            ushort number => Value.ForNumber(number),
            int number => Value.ForNumber(number),
            uint number => Value.ForNumber(number),
            long number => Value.ForNumber(number),
            ulong number => Value.ForNumber(number),
            float number => Value.ForNumber(number),
            double number => Value.ForNumber(number),
            decimal number => Value.ForNumber(Convert.ToDouble(number, CultureInfo.InvariantCulture)),
            System.Enum enumValue => Value.ForString(enumValue.ToString()),
            Guid guid => Value.ForString(guid.ToString("D")),
            DateTimeOffset dateTimeOffset => Value.ForString(dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            DateTime dateTime => Value.ForString(ToUtc(dateTime).ToString("O", CultureInfo.InvariantCulture)),
            Uri uri => Value.ForString(uri.ToString()),
            IDictionary dictionary => new Value { StructValue = SerializeDictionary(dictionary) },
            IEnumerable enumerable when value is not string => new Value { ListValue = SerializeList(enumerable) },
            _ => new Value { StructValue = SerializeObject(value) },
        };
    }

    private static Struct SerializeDictionary(IDictionary dictionary)
    {
        var result = new Struct();
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

    private static ListValue SerializeList(IEnumerable enumerable)
    {
        var result = new ListValue();
        foreach (var item in enumerable)
            result.Values.Add(Serialize(item));

        return result;
    }

    private static Struct SerializeObject(object value)
    {
        var result = new Struct();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                continue;

            result.Fields[property.Name] = Serialize(property.GetValue(value));
        }

        return result;
    }

    private static object NormalizeNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Truncate(value) != value)
            return value;

        if (value >= int.MinValue && value <= int.MaxValue)
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);

        if (value >= long.MinValue && value <= long.MaxValue)
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);

        return value;
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();

    private static Value CreateNullValue() =>
        new()
        {
            NullValue = NullValue.NullValue,
        };
}
