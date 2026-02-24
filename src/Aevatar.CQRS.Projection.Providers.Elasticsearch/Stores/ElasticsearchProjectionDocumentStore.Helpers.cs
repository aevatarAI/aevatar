using System.Net;
using System.Text;
using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

public sealed partial class ElasticsearchProjectionDocumentStore<TReadModel, TKey>
{
    private string BuildListPayloadJson(int size)
    {
        var sort = _listSortField.Length == 0
            ? BuildDefaultSortSpec()
            : BuildConfiguredSortSpec(_listSortField);

        return JsonSerializer.Serialize(new
        {
            size,
            sort,
            query = new
            {
                match_all = new { },
            },
        });
    }

    private static object[] BuildConfiguredSortSpec(string sortField)
    {
        return
        [
            new Dictionary<string, object>
            {
                [sortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                },
            },
            new Dictionary<string, object>
            {
                [DefaultListTiebreakSortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                },
            },
        ];
    }

    private static object[] BuildDefaultSortSpec()
    {
        return
        [
            new Dictionary<string, object>
            {
                [DefaultListPrimarySortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                    ["missing"] = "_last",
                    ["unmapped_type"] = "date",
                },
            },
            new Dictionary<string, object>
            {
                [DefaultListTiebreakSortField] = new Dictionary<string, object>
                {
                    ["order"] = "desc",
                },
            },
        ];
    }

    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (!_autoCreateIndex || _indexInitialized)
            return;

        await _indexInitializationLock.WaitAsync(ct);
        try
        {
            if (_indexInitialized)
                return;

            var payload = BuildIndexInitializationPayload();
            using var request = new HttpRequestMessage(HttpMethod.Put, _indexName)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                _indexInitialized = true;
                return;
            }

            var responsePayload = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                responsePayload.Contains("resource_already_exists_exception", StringComparison.OrdinalIgnoreCase))
            {
                _indexInitialized = true;
                return;
            }

            throw new InvalidOperationException(
                $"Elasticsearch index initialization failed for '{_indexName}': {(int)response.StatusCode} {response.ReasonPhrase}. body={responsePayload}");
        }
        finally
        {
            _indexInitializationLock.Release();
        }
    }

    private string BuildIndexInitializationPayload()
    {
        var mappings = _indexMetadata.Mappings.Count == 0
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dynamic"] = true,
            }
            : new Dictionary<string, object?>(_indexMetadata.Mappings, StringComparer.Ordinal);

        var root = new Dictionary<string, object?>
        {
            ["mappings"] = mappings,
        };

        if (_indexMetadata.Settings.Count > 0)
            root["settings"] = new Dictionary<string, object?>(_indexMetadata.Settings, StringComparer.Ordinal);
        if (_indexMetadata.Aliases.Count > 0)
            root["aliases"] = new Dictionary<string, object?>(_indexMetadata.Aliases, StringComparer.Ordinal);

        return JsonSerializer.Serialize(root, _jsonOptions);
    }

    private static DocumentIndexMetadata NormalizeMetadata(DocumentIndexMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var normalizedMappings = NormalizeObjectMap(metadata.Mappings, "DocumentIndexMetadata.Mappings");
        var normalizedSettings = NormalizeObjectMap(metadata.Settings, "DocumentIndexMetadata.Settings");
        var normalizedAliases = NormalizeObjectMap(metadata.Aliases, "DocumentIndexMetadata.Aliases");
        return new DocumentIndexMetadata(
            metadata.IndexName?.Trim() ?? "",
            normalizedMappings,
            normalizedSettings,
            normalizedAliases);
    }

    private static Dictionary<string, object?> NormalizeObjectMap(
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
            return NormalizeObjectMap(
                new Dictionary<string, object?>(mutableObjectMap, StringComparer.Ordinal),
                context);

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

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var payload = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Elasticsearch {operation} failed: {(int)response.StatusCode} {response.ReasonPhrase}. body={payload}");
    }

    private static Uri ResolvePrimaryEndpoint(IReadOnlyList<string>? endpoints)
    {
        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException("Elasticsearch provider requires at least one endpoint.");

        var endpoint = endpoints[0].Trim();
        if (endpoint.Length == 0)
            throw new InvalidOperationException("Elasticsearch endpoint cannot be empty.");
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "http://" + endpoint;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid Elasticsearch endpoint '{endpoints[0]}'.");

        return uri;
    }

    private static string BuildIndexName(string indexPrefix, string indexScope)
    {
        var prefix = NormalizeToken(indexPrefix);
        if (prefix.Length == 0)
            prefix = "aevatar";
        return $"{prefix}-{indexScope}";
    }

    private static string NormalizeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        var chars = token
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string TruncatePayload(string payload)
    {
        const int maxLength = 512;
        if (payload.Length <= maxLength)
            return payload;

        return payload[..maxLength] + "...(truncated)";
    }
}
