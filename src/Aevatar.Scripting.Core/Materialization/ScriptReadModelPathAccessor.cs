using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using System.Collections;

namespace Aevatar.Scripting.Core.Materialization;

public sealed class ScriptReadModelPathAccessor
{
    private readonly IReadOnlyList<CompiledPathSegment> _segments;
    private readonly bool _returnsCollection;

    internal ScriptReadModelPathAccessor(
        string path,
        IReadOnlyList<CompiledPathSegment> segments)
    {
        Path = path ?? string.Empty;
        _segments = segments ?? throw new ArgumentNullException(nameof(segments));
        _returnsCollection = _segments.Any(static x => x.IsRepeated);
    }

    public string Path { get; }

    public object? ExtractValue(IMessage? root)
    {
        if (root == null)
            return null;

        IReadOnlyList<object?> current = [root];
        foreach (var segment in _segments)
        {
            if (current.Count == 0)
                return null;

            var next = new List<object?>();
            foreach (var item in current)
            {
                if (item is not IMessage message)
                    continue;

                var value = segment.Field.Accessor.GetValue(message);
                if (segment.IsRepeated)
                {
                    if (value is IEnumerable enumerable)
                    {
                        foreach (var entry in enumerable)
                        {
                            if (entry != null)
                                next.Add(entry);
                        }
                    }

                    continue;
                }

                if (value != null)
                    next.Add(value);
            }

            current = next;
        }

        if (current.Count == 0)
            return null;

        var converted = current
            .Select(ConvertLeafValue)
            .Where(static x => x != null)
            .ToList();
        if (converted.Count == 0)
            return null;

        return _returnsCollection ? converted : converted[0];
    }

    internal static ScriptReadModelPathAccessor Compile(
        System.Type readModelClrType,
        string path)
    {
        ArgumentNullException.ThrowIfNull(readModelClrType);
        if (Activator.CreateInstance(readModelClrType) is not IMessage message)
        {
            throw new InvalidOperationException(
                $"Read model CLR type `{readModelClrType.FullName}` is not a protobuf message.");
        }

        return Compile(message.Descriptor, path);
    }

    internal static ScriptReadModelPathAccessor Compile(
        MessageDescriptor rootDescriptor,
        string path)
    {
        ArgumentNullException.ThrowIfNull(rootDescriptor);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Read model path cannot be empty.");

        var tokens = path
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            throw new InvalidOperationException($"Read model path `{path}` is invalid.");

        var compiled = new List<CompiledPathSegment>(tokens.Length);
        var currentDescriptor = rootDescriptor;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var isRepeated = token.EndsWith("[]", StringComparison.Ordinal);
            var fieldName = isRepeated
                ? token[..^2]
                : token;
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new InvalidOperationException($"Read model path `{path}` contains an empty segment.");

            var field = currentDescriptor.FindFieldByName(fieldName);
            if (field == null)
            {
                throw new InvalidOperationException(
                    $"Read model path `{path}` references unknown field `{fieldName}` on `{currentDescriptor.FullName}`.");
            }

            if (field.IsRepeated && !isRepeated)
            {
                throw new InvalidOperationException(
                    $"Read model path `{path}` must use `[]` for repeated field `{fieldName}`.");
            }

            if (!field.IsRepeated && isRepeated)
            {
                throw new InvalidOperationException(
                    $"Read model path `{path}` cannot use `[]` on non-repeated field `{fieldName}`.");
            }

            var isLast = i == tokens.Length - 1;
            if (isRepeated && !isLast)
            {
                throw new InvalidOperationException(
                    $"Read model path `{path}` cannot traverse through repeated field `{fieldName}`. Repeated segments are only supported at the leaf.");
            }

            if (!isLast)
            {
                if (field.FieldType != FieldType.Message || field.MessageType == null)
                {
                    throw new InvalidOperationException(
                        $"Read model path `{path}` traverses non-message field `{fieldName}`.");
                }

                currentDescriptor = field.MessageType;
            }
            else if (field.FieldType == FieldType.Message &&
                     field.MessageType != null &&
                     !IsSupportedLeafMessage(field.MessageType))
            {
                throw new InvalidOperationException(
                    $"Read model path `{path}` ends on unsupported message field `{fieldName}` ({field.MessageType.FullName}).");
            }

            compiled.Add(new CompiledPathSegment(field, isRepeated));
        }

        return new ScriptReadModelPathAccessor(path, compiled);
    }

    private static bool IsSupportedLeafMessage(MessageDescriptor descriptor)
    {
        return descriptor.FullName switch
        {
            "google.protobuf.Timestamp" => true,
            "google.protobuf.StringValue" => true,
            "google.protobuf.BoolValue" => true,
            "google.protobuf.Int32Value" => true,
            "google.protobuf.Int64Value" => true,
            "google.protobuf.UInt32Value" => true,
            "google.protobuf.UInt64Value" => true,
            "google.protobuf.DoubleValue" => true,
            "google.protobuf.FloatValue" => true,
            "google.protobuf.BytesValue" => true,
            _ => false,
        };
    }

    private static object? ConvertLeafValue(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            bool boolean => boolean,
            byte byteValue => byteValue,
            sbyte signedByteValue => signedByteValue,
            short shortValue => shortValue,
            ushort unsignedShortValue => unsignedShortValue,
            int intValue => intValue,
            uint unsignedIntValue => unsignedIntValue,
            long longValue => longValue,
            ulong unsignedLongValue => unsignedLongValue,
            float floatValue => floatValue,
            double doubleValue => doubleValue,
            decimal decimalValue => decimalValue,
            System.Enum enumValue => enumValue.ToString(),
            Timestamp timestamp => timestamp.ToDateTimeOffset(),
            StringValue wrapper => wrapper.Value,
            BoolValue wrapper => wrapper.Value,
            Int32Value wrapper => wrapper.Value,
            Int64Value wrapper => wrapper.Value,
            UInt32Value wrapper => wrapper.Value,
            UInt64Value wrapper => (long)wrapper.Value,
            DoubleValue wrapper => wrapper.Value,
            FloatValue wrapper => wrapper.Value,
            BytesValue wrapper => Convert.ToBase64String(wrapper.Value.ToByteArray()),
            ByteString byteString => Convert.ToBase64String(byteString.ToByteArray()),
            _ => throw new InvalidOperationException(
                $"Read model path `{nameof(ConvertLeafValue)}` encountered unsupported value type `{value.GetType().FullName}`."),
        };
    }

    internal sealed record CompiledPathSegment(FieldDescriptor Field, bool IsRepeated);
}
