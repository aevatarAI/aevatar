using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.GAgentService.Hosting.Serialization;

internal static class JsonToProto
{
    public static byte[] WriteMessage(MessageDescriptor descriptor, JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"payloadJson root must be a JSON object for '{descriptor.FullName}' but was '{json.ValueKind}'.");

        using var ms = new MemoryStream();
        var output = new CodedOutputStream(ms);
        WriteMessageFields(descriptor, json, output);
        output.Flush();
        return ms.ToArray();
    }

    private static void WriteMessageFields(MessageDescriptor descriptor, JsonElement json, CodedOutputStream output)
    {
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            if (!TryFindProperty(json, field, out var element))
                continue;
            if (element.ValueKind == JsonValueKind.Null)
                continue;

            if (field.IsRepeated)
            {
                WriteRepeated(field, element, output);
            }
            else
            {
                WriteSingle(field, element, output);
            }
        }
    }

    private static bool TryFindProperty(JsonElement obj, FieldDescriptor field, out JsonElement value)
    {
        if (obj.TryGetProperty(field.JsonName, out value))
            return true;
        if (!string.Equals(field.JsonName, field.Name, StringComparison.Ordinal) &&
            obj.TryGetProperty(field.Name, out value))
            return true;
        value = default;
        return false;
    }

    private static void WriteRepeated(FieldDescriptor field, JsonElement array, CodedOutputStream output)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"field '{field.FullName}' expects a JSON array but received '{array.ValueKind}'.");

        if (IsPackable(field.FieldType))
        {
            using var bufferStream = new MemoryStream();
            var bufferOutput = new CodedOutputStream(bufferStream);
            foreach (var item in array.EnumerateArray())
            {
                WritePrimitiveValue(field, item, bufferOutput);
            }

            bufferOutput.Flush();
            var packedBytes = bufferStream.ToArray();
            if (packedBytes.Length == 0)
                return;

            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(packedBytes));
        }
        else
        {
            foreach (var item in array.EnumerateArray())
            {
                WriteSingle(field, item, output);
            }
        }
    }

    private static void WriteSingle(FieldDescriptor field, JsonElement value, CodedOutputStream output)
    {
        if (field.FieldType == FieldType.Message)
        {
            if (field.MessageType == null)
                throw new InvalidOperationException(
                    $"field '{field.FullName}' has unresolved message type.");
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    $"field '{field.FullName}' expects a JSON object but received '{value.ValueKind}'.");

            var nestedBytes = WriteMessage(field.MessageType, value);
            output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(ByteString.CopyFrom(nestedBytes));
            return;
        }

        if (field.FieldType == FieldType.Group)
            throw new InvalidOperationException(
                $"field '{field.FullName}' is a proto2 group; not supported in JSON payloads.");

        var wireType = GetWireType(field.FieldType);
        output.WriteTag(field.FieldNumber, wireType);
        WritePrimitiveValue(field, value, output);
    }

    private static void WritePrimitiveValue(FieldDescriptor field, JsonElement value, CodedOutputStream output)
    {
        switch (field.FieldType)
        {
            case FieldType.Double:
                output.WriteDouble(ReadDouble(field, value));
                break;
            case FieldType.Float:
                output.WriteFloat((float)ReadDouble(field, value));
                break;
            case FieldType.Int64:
                output.WriteInt64(ReadInt64(field, value));
                break;
            case FieldType.UInt64:
                output.WriteUInt64(ReadUInt64(field, value));
                break;
            case FieldType.Int32:
                output.WriteInt32((int)ReadInt64(field, value));
                break;
            case FieldType.Fixed64:
                output.WriteFixed64(ReadUInt64(field, value));
                break;
            case FieldType.Fixed32:
                output.WriteFixed32((uint)ReadUInt64(field, value));
                break;
            case FieldType.Bool:
                output.WriteBool(ReadBool(field, value));
                break;
            case FieldType.String:
                output.WriteString(ReadString(field, value));
                break;
            case FieldType.Bytes:
                output.WriteBytes(ReadBytes(field, value));
                break;
            case FieldType.UInt32:
                output.WriteUInt32((uint)ReadUInt64(field, value));
                break;
            case FieldType.Enum:
                output.WriteInt32(ReadEnum(field, value));
                break;
            case FieldType.SFixed32:
                output.WriteSFixed32((int)ReadInt64(field, value));
                break;
            case FieldType.SFixed64:
                output.WriteSFixed64(ReadInt64(field, value));
                break;
            case FieldType.SInt32:
                output.WriteSInt32((int)ReadInt64(field, value));
                break;
            case FieldType.SInt64:
                output.WriteSInt64(ReadInt64(field, value));
                break;
            default:
                throw new InvalidOperationException(
                    $"field '{field.FullName}' has unsupported type '{field.FieldType}'.");
        }
    }

    private static WireFormat.WireType GetWireType(FieldType type) => type switch
    {
        FieldType.Double or FieldType.Fixed64 or FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        FieldType.Float or FieldType.Fixed32 or FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.String or FieldType.Bytes => WireFormat.WireType.LengthDelimited,
        _ => WireFormat.WireType.Varint,
    };

    private static bool IsPackable(FieldType type) => type switch
    {
        FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group => false,
        _ => true,
    };

    private static long ReadInt64(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        throw new InvalidOperationException(
            $"field '{field.FullName}' expects an integer but received '{value.ValueKind}'.");
    }

    private static ulong ReadUInt64(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        throw new InvalidOperationException(
            $"field '{field.FullName}' expects an unsigned integer but received '{value.ValueKind}'.");
    }

    private static double ReadDouble(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (string.Equals(raw, "NaN", StringComparison.Ordinal)) return double.NaN;
            if (string.Equals(raw, "Infinity", StringComparison.Ordinal)) return double.PositiveInfinity;
            if (string.Equals(raw, "-Infinity", StringComparison.Ordinal)) return double.NegativeInfinity;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return s;
        }
        throw new InvalidOperationException(
            $"field '{field.FullName}' expects a number but received '{value.ValueKind}'.");
    }

    private static bool ReadBool(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        throw new InvalidOperationException(
            $"field '{field.FullName}' expects a boolean but received '{value.ValueKind}'.");
    }

    private static string ReadString(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        throw new InvalidOperationException(
            $"field '{field.FullName}' expects a string but received '{value.ValueKind}'.");
    }

    private static ByteString ReadBytes(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                $"field '{field.FullName}' expects a base64-encoded string but received '{value.ValueKind}'.");

        var raw = value.GetString() ?? string.Empty;
        try
        {
            return ByteString.CopyFrom(Convert.FromBase64String(raw));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"field '{field.FullName}' is not valid base64.", ex);
        }
    }

    private static int ReadEnum(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString() ?? string.Empty;
            if (field.EnumType == null)
                throw new InvalidOperationException(
                    $"field '{field.FullName}' has unresolved enum type.");
            var match = field.EnumType.FindValueByName(raw);
            if (match != null) return match.Number;
            throw new InvalidOperationException(
                $"field '{field.FullName}' has no enum value named '{raw}'.");
        }
        throw new InvalidOperationException(
            $"field '{field.FullName}' expects an enum number or name but received '{value.ValueKind}'.");
    }
}
