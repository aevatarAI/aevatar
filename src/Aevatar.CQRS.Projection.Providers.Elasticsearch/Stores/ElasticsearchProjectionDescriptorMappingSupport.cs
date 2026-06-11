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
            if (ShouldSkipField(field) || properties.ContainsKey(field.Name))
                continue;

            if (IsTimestampField(field))
            {
                properties[field.Name] = CreateTypeMapping("date");
                continue;
            }

            if (field.FieldType == FieldType.String && IsStableKeywordFieldName(field.Name))
                properties[field.Name] = CreateTypeMapping("keyword");
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

    private static bool ShouldSkipField(FieldDescriptor field)
    {
        if (field.IsMap || field.IsRepeated)
            return true;

        return field.FieldType == FieldType.Message &&
               field.MessageType != null &&
               (field.MessageType.FullName == Any.Descriptor.FullName ||
                field.MessageType.FullName == Struct.Descriptor.FullName);
    }

    private static bool IsTimestampField(FieldDescriptor field)
    {
        return field.FieldType == FieldType.Message &&
               field.MessageType != null &&
               field.MessageType.FullName == Timestamp.Descriptor.FullName;
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
}
