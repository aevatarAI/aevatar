namespace Aevatar.Scripting.Projection.ReadModels;

internal static class ScriptNativeReadModelCloneSupport
{
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
            _ => value,
        };
    }
}
