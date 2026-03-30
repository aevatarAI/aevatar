using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDocumentStoreMetadataSupport
{
    internal static DocumentIndexMetadata NormalizeMetadata(DocumentIndexMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var normalizedMappings = NormalizeObjectMap(metadata.Mappings, "DocumentIndexMetadata.Mappings");
        EnsureStableSortFieldMapping(normalizedMappings);
        var normalizedSettings = NormalizeObjectMap(metadata.Settings, "DocumentIndexMetadata.Settings");
        var normalizedAliases = NormalizeObjectMap(metadata.Aliases, "DocumentIndexMetadata.Aliases");
        return new DocumentIndexMetadata(
            metadata.IndexName?.Trim() ?? "",
            normalizedMappings,
            normalizedSettings,
            normalizedAliases);
    }

    internal static Dictionary<string, object?> NormalizeObjectMap(
        IReadOnlyDictionary<string, object?> source,
        string context)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            var key = pair.Key?.Trim() ?? "";
            if (key.Length == 0)
                throw new InvalidOperationException($"{context} contains an empty key.");

            normalized[key] = NormalizeObjectValue(pair.Value, $"{context}['{key}']");
        }

        return normalized;
    }

    private static object? NormalizeObjectValue(object? value, string context)
    {
        if (value == null)
            return null;

        if (value is string ||
            value is bool ||
            value is byte ||
            value is sbyte ||
            value is short ||
            value is ushort ||
            value is int ||
            value is uint ||
            value is long ||
            value is ulong ||
            value is float ||
            value is double ||
            value is decimal)
        {
            return value;
        }

        if (value is JsonElement jsonElement)
            return NormalizeJsonElement(jsonElement, context);

        if (value is IReadOnlyDictionary<string, object?> readonlyObjectMap)
            return NormalizeObjectMap(readonlyObjectMap, context);

        if (value is IDictionary<string, object?> mutableObjectMap)
        {
            return NormalizeObjectMap(
                new Dictionary<string, object?>(mutableObjectMap, StringComparer.Ordinal),
                context);
        }

        if (value is IReadOnlyDictionary<string, string> readonlyStringMap)
        {
            var converted = readonlyStringMap.ToDictionary(
                x => x.Key,
                x => (object?)x.Value,
                StringComparer.Ordinal);
            return NormalizeObjectMap(converted, context);
        }

        if (value is IDictionary<string, string> mutableStringMap)
        {
            var converted = mutableStringMap.ToDictionary(
                x => x.Key,
                x => (object?)x.Value,
                StringComparer.Ordinal);
            return NormalizeObjectMap(converted, context);
        }

        if (value is IEnumerable<object?> objectSequence)
            return objectSequence.Select((x, i) => NormalizeObjectValue(x, $"{context}[{i}]")).ToList();

        if (value is IEnumerable<string> stringSequence)
            return stringSequence.Cast<object?>().ToList();

        throw new InvalidOperationException(
            $"{context} contains unsupported value type '{value.GetType().FullName}'.");
    }

    private static object? NormalizeJsonElement(JsonElement element, string context)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element
                .EnumerateObject()
                .ToDictionary(
                    x => x.Name,
                    x => NormalizeJsonElement(x.Value, $"{context}['{x.Name}']"),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element
                .EnumerateArray()
                .Select((x, i) => NormalizeJsonElement(x, $"{context}[{i}]"))
                .ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => NormalizeJsonNumber(element, context),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => throw new InvalidOperationException(
                $"{context} contains unsupported json value kind '{element.ValueKind}'."),
        };
    }

    private static object NormalizeJsonNumber(JsonElement numberElement, string context)
    {
        if (numberElement.TryGetInt64(out var int64Value))
            return int64Value;
        if (numberElement.TryGetDecimal(out var decimalValue))
            return decimalValue;
        if (numberElement.TryGetDouble(out var doubleValue))
            return doubleValue;

        throw new InvalidOperationException($"{context} contains an invalid JSON number value.");
    }

    private static void EnsureStableSortFieldMapping(Dictionary<string, object?> mappings)
    {
        if (!mappings.TryGetValue("properties", out var propertiesValue) || propertiesValue == null)
        {
            mappings["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ElasticsearchProjectionDocumentStorePayloadSupport.StableSortDocumentIdField] =
                    CreateStableSortFieldMapping(),
            };
            return;
        }

        if (propertiesValue is not IReadOnlyDictionary<string, object?> properties)
        {
            throw new InvalidOperationException(
                "DocumentIndexMetadata.Mappings['properties'] must be an object map.");
        }

        var normalizedProperties = new Dictionary<string, object?>(properties, StringComparer.Ordinal);
        if (normalizedProperties.TryGetValue(
                ElasticsearchProjectionDocumentStorePayloadSupport.StableSortDocumentIdField,
                out var existingMapping))
        {
            if (!IsKeywordFieldMapping(existingMapping))
            {
                throw new InvalidOperationException(
                    $"DocumentIndexMetadata.Mappings reserves '{ElasticsearchProjectionDocumentStorePayloadSupport.StableSortDocumentIdField}' for Elasticsearch pagination and it must remain a keyword field.");
            }
        }
        else
        {
            normalizedProperties[ElasticsearchProjectionDocumentStorePayloadSupport.StableSortDocumentIdField] =
                CreateStableSortFieldMapping();
        }

        mappings["properties"] = normalizedProperties;
    }

    private static Dictionary<string, object?> CreateStableSortFieldMapping()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "keyword",
        };
    }

    internal static bool IsKeywordFieldMapping(object? mapping)
    {
        if (mapping is not IReadOnlyDictionary<string, object?> mappingObject)
            return false;

        return mappingObject.TryGetValue("type", out var typeValue) &&
               typeValue is string typeName &&
               string.Equals(typeName, "keyword", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasKeywordMultiField(object? mapping)
    {
        if (mapping is not IReadOnlyDictionary<string, object?> mappingObject)
            return false;

        if (!mappingObject.TryGetValue("fields", out var fieldsValue) ||
            fieldsValue is not IReadOnlyDictionary<string, object?> fields)
        {
            return false;
        }

        return fields.TryGetValue("keyword", out var keywordMapping) &&
               IsKeywordFieldMapping(keywordMapping);
    }

    internal static bool TryGetFieldMapping(
        IReadOnlyDictionary<string, object?> mappings,
        string fieldPath,
        out IReadOnlyDictionary<string, object?>? fieldMapping)
    {
        fieldMapping = null;
        if (string.IsNullOrWhiteSpace(fieldPath))
            return false;

        if (!mappings.TryGetValue("properties", out var propertiesValue) ||
            propertiesValue is not IReadOnlyDictionary<string, object?> properties)
        {
            return false;
        }

        IReadOnlyDictionary<string, object?> currentProperties = properties;
        var segments = fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (!currentProperties.TryGetValue(segment, out var segmentValue) ||
                segmentValue is not IReadOnlyDictionary<string, object?> segmentMapping)
            {
                return false;
            }

            if (index == segments.Length - 1)
            {
                fieldMapping = segmentMapping;
                return true;
            }

            if (!segmentMapping.TryGetValue("properties", out var nestedPropertiesValue) ||
                nestedPropertiesValue is not IReadOnlyDictionary<string, object?> nestedProperties)
            {
                return false;
            }

            currentProperties = nestedProperties;
        }

        return false;
    }
}
