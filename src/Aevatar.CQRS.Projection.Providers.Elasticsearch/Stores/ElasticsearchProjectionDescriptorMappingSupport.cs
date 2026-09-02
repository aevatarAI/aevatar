using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDescriptorMappingSupport
{
    internal static DocumentIndexMetadata AugmentMetadata(
        DocumentIndexMetadata metadata,
        MessageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(descriptor);

        var mappings = new Dictionary<string, object?>(metadata.Mappings, StringComparer.Ordinal);
        var properties = ResolveProperties(mappings);

        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            if (TryAugmentFieldMapping(field, properties, new HashSet<MessageDescriptor>()))
                continue;

            if (!properties.ContainsKey(field.Name) &&
                field.FieldType == FieldType.String &&
                IsStableKeywordFieldName(field.Name))
            {
                properties[field.Name] = CreateTypeMapping("keyword");
            }
        }

        mappings["properties"] = properties;
        return metadata with { Mappings = mappings };
    }

    private static Dictionary<string, object?> ResolveProperties(Dictionary<string, object?> mappings)
    {
        if (!mappings.TryGetValue("properties", out var propertiesValue) || propertiesValue == null)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        if (propertiesValue is not IReadOnlyDictionary<string, object?> properties)
        {
            throw new InvalidOperationException(
                "DocumentIndexMetadata.Mappings['properties'] must be an object map.");
        }

        return new Dictionary<string, object?>(properties, StringComparer.Ordinal);
    }

    private static bool TryAugmentFieldMapping(
        FieldDescriptor field,
        Dictionary<string, object?> properties,
        HashSet<MessageDescriptor> ancestry)
    {
        if (field.IsMap)
        {
            properties.TryAdd(field.Name, CreateDisabledObjectMapping());
            return true;
        }

        if (field.FieldType != FieldType.Message || field.MessageType == null)
            return field.IsRepeated;

        if (IsTimestampField(field))
        {
            if (!field.IsRepeated)
                properties.TryAdd(field.Name, CreateTypeMapping("date"));

            return true;
        }

        if (IsOpenMessage(field.MessageType))
            return true;

        if (!ContainsMapField(field.MessageType, ancestry))
            return field.IsRepeated;

        AugmentMessageObjectMapping(field.Name, field.MessageType, properties, ancestry);
        return true;
    }

    private static bool IsTimestampField(FieldDescriptor field)
    {
        return field.FieldType == FieldType.Message &&
               field.MessageType != null &&
               field.MessageType.FullName == Timestamp.Descriptor.FullName;
    }

    private static bool IsOpenMessage(MessageDescriptor descriptor)
    {
        return descriptor.FullName == Any.Descriptor.FullName ||
               descriptor.FullName == Struct.Descriptor.FullName;
    }

    private static bool ContainsMapField(MessageDescriptor descriptor, HashSet<MessageDescriptor> ancestry)
    {
        if (!ancestry.Add(descriptor))
            return false;

        try
        {
            foreach (var field in descriptor.Fields.InDeclarationOrder())
            {
                if (field.IsMap)
                    return true;

                if (field.FieldType == FieldType.Message &&
                    field.MessageType != null &&
                    !IsOpenMessage(field.MessageType) &&
                    ContainsMapField(field.MessageType, ancestry))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            ancestry.Remove(descriptor);
        }
    }

    private static void AugmentMessageObjectMapping(
        string fieldName,
        MessageDescriptor descriptor,
        Dictionary<string, object?> properties,
        HashSet<MessageDescriptor> ancestry)
    {
        if (properties.TryGetValue(fieldName, out var existingMapping))
        {
            TryAugmentExistingObjectMapping(fieldName, descriptor, properties, ancestry, existingMapping);
            return;
        }

        var childProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
        AugmentMessageProperties(descriptor, childProperties, ancestry);
        properties[fieldName] = CreateObjectMapping(childProperties);
    }

    private static void TryAugmentExistingObjectMapping(
        string fieldName,
        MessageDescriptor descriptor,
        Dictionary<string, object?> properties,
        HashSet<MessageDescriptor> ancestry,
        object? existingMapping)
    {
        if (existingMapping is not IReadOnlyDictionary<string, object?> existingObject ||
            IsDisabledObjectMapping(existingObject) ||
            HasExplicitNonObjectType(existingObject))
        {
            return;
        }

        var augmentedChildProperties = ResolveChildProperties(existingObject);
        AugmentMessageProperties(descriptor, augmentedChildProperties, ancestry);

        var augmentedObject = new Dictionary<string, object?>(existingObject, StringComparer.Ordinal)
        {
            ["properties"] = augmentedChildProperties,
        };

        if (!augmentedObject.ContainsKey("type"))
            augmentedObject["type"] = "object";

        properties[fieldName] = augmentedObject;
    }

    private static Dictionary<string, object?> ResolveChildProperties(
        IReadOnlyDictionary<string, object?> existingObject)
    {
        if (!existingObject.TryGetValue("properties", out var childPropertiesValue) ||
            childPropertiesValue is not IReadOnlyDictionary<string, object?> existingChildProperties)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return new Dictionary<string, object?>(existingChildProperties, StringComparer.Ordinal);
    }

    private static bool IsDisabledObjectMapping(IReadOnlyDictionary<string, object?> mapping)
    {
        return mapping.TryGetValue("enabled", out var enabledValue) &&
               enabledValue is bool enabled &&
               !enabled;
    }

    private static bool HasExplicitNonObjectType(IReadOnlyDictionary<string, object?> mapping)
    {
        return mapping.TryGetValue("type", out var typeValue) &&
               typeValue is string typeName &&
               !string.Equals(typeName, "object", StringComparison.OrdinalIgnoreCase);
    }

    private static void AugmentMessageProperties(
        MessageDescriptor descriptor,
        Dictionary<string, object?> properties,
        HashSet<MessageDescriptor> ancestry)
    {
        if (!ancestry.Add(descriptor))
            return;

        try
        {
            foreach (var field in descriptor.Fields.InDeclarationOrder())
            {
                if (TryAugmentFieldMapping(field, properties, ancestry))
                    continue;

                if (!properties.ContainsKey(field.Name) &&
                    field.FieldType == FieldType.String &&
                    IsStableKeywordFieldName(field.Name))
                {
                    properties[field.Name] = CreateTypeMapping("keyword");
                }
            }
        }
        finally
        {
            ancestry.Remove(descriptor);
        }
    }

    private static bool IsStableKeywordFieldName(string fieldName)
    {
        return string.Equals(fieldName, "id", StringComparison.Ordinal) ||
               string.Equals(fieldName, "actor_id", StringComparison.Ordinal) ||
               string.Equals(fieldName, "last_event_id", StringComparison.Ordinal) ||
               fieldName.EndsWith("_id", StringComparison.Ordinal) ||
               fieldName.EndsWith("_actor_id", StringComparison.Ordinal) ||
               fieldName.EndsWith("_key", StringComparison.Ordinal) ||
               fieldName.EndsWith("_hash", StringComparison.Ordinal) ||
               fieldName.EndsWith("_revision", StringComparison.Ordinal) ||
               fieldName.EndsWith("_revision_id", StringComparison.Ordinal) ||
               fieldName.EndsWith("_status", StringComparison.Ordinal) ||
               fieldName.EndsWith("_kind", StringComparison.Ordinal) ||
               fieldName.EndsWith("_type", StringComparison.Ordinal) ||
               fieldName.EndsWith("_type_url", StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> CreateTypeMapping(string type)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
        };
    }

    private static Dictionary<string, object?> CreateDisabledObjectMapping()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["enabled"] = false,
        };
    }

    private static Dictionary<string, object?> CreateObjectMapping(Dictionary<string, object?> properties)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
    }
}
