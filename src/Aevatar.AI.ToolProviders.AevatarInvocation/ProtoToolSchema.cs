using System.Text.Json;
using Google.Protobuf.Reflection;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

internal static class ProtoToolSchema
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public static string Build(
        MessageDescriptor descriptor,
        IReadOnlySet<string>? requiredFields = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? stringEnums = null,
        IReadOnlyList<IReadOnlyList<string>>? oneOfRequiredGroups = null,
        bool emitTopLevelOneOf = true,
        IReadOnlyDictionary<string, string>? fieldDescriptions = null)
    {
        var schema = BuildMessageSchema(descriptor, requiredFields, stringEnums, depth: 0);
        if (emitTopLevelOneOf && oneOfRequiredGroups is { Count: > 0 })
        {
            schema["oneOf"] = oneOfRequiredGroups
                .Select(group => new Dictionary<string, object>
                {
                    ["required"] = group,
                })
                .ToArray();
        }

        if (fieldDescriptions is { Count: > 0 })
        {
            foreach (var (path, description) in fieldDescriptions)
                ApplyFieldDescription(schema, path, description);
        }

        return JsonSerializer.Serialize(schema, SerializerOptions);
    }

    /// <summary>
    /// Attaches a "description" to the schema node at a dotted property path
    /// (for example "inputs.prompt"). The proto-derived schemas are otherwise
    /// type-only, which leaves the model with zero guidance on field
    /// semantics — for contract-sensitive fields that guidance is the primary
    /// defense against malformed tool calls.
    /// </summary>
    private static void ApplyFieldDescription(
        Dictionary<string, object> schema,
        string dottedPath,
        string description)
    {
        var node = schema;
        var segments = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            if (node.TryGetValue("properties", out var propertiesRaw) &&
                propertiesRaw is Dictionary<string, object> properties &&
                properties.TryGetValue(segments[index], out var childRaw) &&
                childRaw is Dictionary<string, object> child)
            {
                node = child;
                continue;
            }

            return;
        }

        node["description"] = description;
    }

    private static Dictionary<string, object> BuildMessageSchema(
        MessageDescriptor descriptor,
        IReadOnlySet<string>? requiredFields,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? stringEnums,
        int depth)
    {
        if (depth > 8)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
            };
        }

        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            if (field.ContainingOneof != null && !field.ContainingOneof.IsSynthetic)
                continue;

            properties[field.Name] = BuildFieldSchema(field, stringEnums, depth);
        }

        foreach (var oneof in descriptor.Oneofs)
        {
            if (oneof.IsSynthetic)
                continue;

            foreach (var field in oneof.Fields)
                properties[field.Name] = BuildFieldSchema(field, stringEnums, depth);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };

        if (requiredFields is { Count: > 0 })
            schema["required"] = requiredFields.ToArray();

        return schema;
    }

    private static Dictionary<string, object> BuildFieldSchema(
        FieldDescriptor field,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? stringEnums,
        int depth)
    {
        if (stringEnums != null && stringEnums.TryGetValue(field.Name, out var values))
        {
            return new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = values,
            };
        }

        if (field.IsMap)
        {
            var valueField = field.MessageType.FindFieldByName("value");
            return new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = BuildSingleFieldTypeSchema(valueField, stringEnums, depth + 1),
            };
        }

        if (field.IsRepeated)
        {
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = BuildSingleFieldTypeSchema(field, stringEnums, depth + 1),
            };
        }

        return BuildSingleFieldTypeSchema(field, stringEnums, depth);
    }

    private static Dictionary<string, object> BuildSingleFieldTypeSchema(
        FieldDescriptor field,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? stringEnums,
        int depth)
    {
        return field.FieldType switch
        {
            FieldType.String => new Dictionary<string, object> { ["type"] = "string" },
            FieldType.Bool => new Dictionary<string, object> { ["type"] = "boolean" },
            FieldType.Int32 or FieldType.SInt32 or FieldType.UInt32 or FieldType.Fixed32 or FieldType.SFixed32 =>
                new Dictionary<string, object> { ["type"] = "integer" },
            FieldType.Int64 or FieldType.SInt64 or FieldType.UInt64 or FieldType.Fixed64 or FieldType.SFixed64 =>
                new Dictionary<string, object> { ["type"] = "integer" },
            FieldType.Float or FieldType.Double => new Dictionary<string, object> { ["type"] = "number" },
            FieldType.Bytes => new Dictionary<string, object> { ["type"] = "string", ["format"] = "byte" },
            FieldType.Enum => BuildEnumSchema(field),
            FieldType.Message => BuildMessageSchema(field.MessageType, null, stringEnums, depth + 1),
            _ => new Dictionary<string, object> { ["type"] = "string" },
        };
    }

    private static Dictionary<string, object> BuildEnumSchema(FieldDescriptor field)
    {
        var values = field.EnumType.Values
            .Where(static value => value.Number != 0)
            .Select(static value => value.Name.ToLowerInvariant())
            .ToArray();
        return new Dictionary<string, object>
        {
            ["type"] = "string",
            ["enum"] = values,
        };
    }
}
