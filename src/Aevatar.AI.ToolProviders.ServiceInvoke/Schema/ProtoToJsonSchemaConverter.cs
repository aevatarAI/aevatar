using System.Text.Json;
using Google.Protobuf.Reflection;

namespace Aevatar.AI.ToolProviders.ServiceInvoke.Schema;

/// <summary>
/// Converts a Protobuf <see cref="MessageDescriptor"/> to a JSON Schema string
/// suitable for LLM tool parameter descriptions.
/// </summary>
public static class ProtoToJsonSchemaConverter
{
    public static string Convert(MessageDescriptor descriptor)
    {
        var schema = BuildMessageSchema(descriptor, depth: 0);
        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = false });
    }

    private static Dictionary<string, object> BuildMessageSchema(MessageDescriptor descriptor, int depth)
    {
        if (depth > 8)
            return new Dictionary<string, object> { ["type"] = "object" };

        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            if (field.ContainingOneof != null && !field.ContainingOneof.IsSynthetic)
                continue;

            var jsonName = field.JsonName;
            properties[jsonName] = BuildFieldSchema(field, depth);
        }

        // Add oneof groups as optional properties
        foreach (var oneof in descriptor.Oneofs)
        {
            if (oneof.IsSynthetic) continue;
            foreach (var field in oneof.Fields)
            {
                var jsonName = field.JsonName;
                properties[jsonName] = BuildFieldSchema(field, depth);
            }
        }

        var result = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
            result["required"] = required;

        return result;
    }

    private static Dictionary<string, object> BuildFieldSchema(FieldDescriptor field, int depth)
    {
        if (field.IsMap)
        {
            var valueField = field.MessageType.FindFieldByName("value");
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = BuildSingleFieldTypeSchema(valueField, depth + 1),
            };
        }

        if (field.IsRepeated)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = BuildSingleFieldTypeSchema(field, depth + 1),
            };
        }

        return BuildSingleFieldTypeSchema(field, depth);
    }

    private static Dictionary<string, object> BuildSingleFieldTypeSchema(FieldDescriptor field, int depth)
    {
        return field.FieldType switch
        {
            FieldType.String => new Dictionary<string, object> { ["type"] = "string" },
            FieldType.Bool => new Dictionary<string, object> { ["type"] = "boolean" },
            FieldType.Int32 or FieldType.SInt32 or FieldType.UInt32 or FieldType.Fixed32 or FieldType.SFixed32 =>
                new Dictionary<string, object> { ["type"] = "integer" },
            FieldType.Int64 or FieldType.SInt64 or FieldType.UInt64 or FieldType.Fixed64 or FieldType.SFixed64 =>
                new Dictionary<string, object> { ["type"] = "integer" },
            FieldType.Float or FieldType.Double =>
                new Dictionary<string, object> { ["type"] = "number" },
            FieldType.Bytes =>
                new Dictionary<string, object> { ["type"] = "string", ["format"] = "byte" },
            FieldType.Enum => BuildEnumSchema(field.EnumType),
            FieldType.Message => BuildNestedMessageSchema(field.MessageType, depth),
            _ => new Dictionary<string, object> { ["type"] = "string" },
        };
    }

    private static Dictionary<string, object> BuildEnumSchema(EnumDescriptor enumType)
    {
        var values = enumType.Values.Select(v => (object)v.Name).ToList();
        return new Dictionary<string, object>
        {
            ["type"] = "string",
            ["enum"] = values,
        };
    }

    private static Dictionary<string, object> BuildNestedMessageSchema(MessageDescriptor descriptor, int depth)
    {
        // Well-known types
        var fullName = descriptor.FullName;
        if (fullName == "google.protobuf.Timestamp")
            return new Dictionary<string, object> { ["type"] = "string", ["format"] = "date-time" };
        if (fullName == "google.protobuf.Duration")
            return new Dictionary<string, object> { ["type"] = "string", ["format"] = "duration" };
        if (fullName == "google.protobuf.Struct")
            return new Dictionary<string, object> { ["type"] = "object" };
        if (fullName == "google.protobuf.Value")
            return new Dictionary<string, object> { };
        if (fullName == "google.protobuf.Any")
            return new Dictionary<string, object> { ["type"] = "object" };
        if (fullName.StartsWith("google.protobuf.") && fullName.EndsWith("Value"))
        {
            // Wrapper types (StringValue, Int32Value, etc.)
            return ResolveWrapperType(fullName);
        }

        return BuildMessageSchema(descriptor, depth + 1);
    }

    private static Dictionary<string, object> ResolveWrapperType(string fullName) => fullName switch
    {
        "google.protobuf.StringValue" => new Dictionary<string, object> { ["type"] = "string" },
        "google.protobuf.BoolValue" => new Dictionary<string, object> { ["type"] = "boolean" },
        "google.protobuf.Int32Value" or "google.protobuf.UInt32Value" =>
            new Dictionary<string, object> { ["type"] = "integer" },
        "google.protobuf.Int64Value" or "google.protobuf.UInt64Value" =>
            new Dictionary<string, object> { ["type"] = "integer" },
        "google.protobuf.FloatValue" or "google.protobuf.DoubleValue" =>
            new Dictionary<string, object> { ["type"] = "number" },
        "google.protobuf.BytesValue" =>
            new Dictionary<string, object> { ["type"] = "string", ["format"] = "byte" },
        _ => new Dictionary<string, object> { ["type"] = "string" },
    };
}
