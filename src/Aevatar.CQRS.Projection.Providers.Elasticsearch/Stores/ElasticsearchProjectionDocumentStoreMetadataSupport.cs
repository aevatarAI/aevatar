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

    internal static object? NormalizeJsonElement(JsonElement element, string context)
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

    /// <summary>
    /// Extracts the field-mapping dictionary from an Elasticsearch <c>GET &lt;index&gt;/_mapping</c>
    /// response so the query path can resolve keyword/text field paths from physical index truth.
    /// The returned dictionary is shaped like <see cref="DocumentIndexMetadata.Mappings"/> (it carries
    /// the <c>properties</c> map) and is safe to pass to <see cref="TryGetFieldMapping"/>.
    /// Returns <c>null</c> when the payload is empty or not a recognizable mapping response.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? TryExtractFieldMappingsFromMappingResponse(
        string mappingResponseJson,
        string indexName)
    {
        if (string.IsNullOrWhiteSpace(mappingResponseJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(mappingResponseJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            // GET <index>/_mapping returns { "<concrete-index>": { "mappings": { "properties": {...} } } }.
            if (!TryResolveIndexNode(document.RootElement, indexName, out var indexNode) ||
                !indexNode.TryGetProperty("mappings", out var mappingsNode) ||
                mappingsNode.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return NormalizeJsonElement(mappingsNode, "Elasticsearch _mapping response")
                as IReadOnlyDictionary<string, object?>;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool TryResolveIndexNode(JsonElement root, string indexName, out JsonElement indexNode)
    {
        if (!string.IsNullOrWhiteSpace(indexName) &&
            root.TryGetProperty(indexName, out var namedNode) &&
            namedNode.ValueKind == JsonValueKind.Object)
        {
            indexNode = namedNode;
            return true;
        }

        // A single-index _mapping request keys the body by the concrete index name; when the
        // caller's logical name differs (prefix/normalization), fall back to the sole object entry.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                indexNode = property.Value;
                return true;
            }
        }

        indexNode = default;
        return false;
    }
}
