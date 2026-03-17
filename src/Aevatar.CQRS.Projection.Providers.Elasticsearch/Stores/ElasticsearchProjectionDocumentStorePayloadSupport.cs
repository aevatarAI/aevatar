using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDocumentStorePayloadSupport
{
    private const string DefaultQueryPrimarySortField = "CreatedAt";
    private const string DefaultQueryTiebreakSortField = "Id.keyword";

    internal static string BuildQueryPayloadJson(
        ProjectionDocumentQuery query,
        string defaultSortField,
        int size)
    {
        var root = new Dictionary<string, object?>
        {
            ["size"] = size,
            ["sort"] = BuildSortSpec(query, defaultSortField),
            ["query"] = BuildFilterSpec(query),
        };

        var searchAfter = DecodeCursor(query.Cursor);
        if (searchAfter != null)
            root["search_after"] = searchAfter;

        if (query.IncludeTotalCount)
            root["track_total_hits"] = true;

        return JsonSerializer.Serialize(root);
    }

    internal static bool TryReadTotalCount(JsonElement root, out long totalCount)
    {
        totalCount = 0;
        if (!root.TryGetProperty("hits", out var hitsNode) ||
            !hitsNode.TryGetProperty("total", out var totalNode))
        {
            return false;
        }

        if (totalNode.ValueKind == JsonValueKind.Number && totalNode.TryGetInt64(out totalCount))
            return true;

        if (totalNode.ValueKind == JsonValueKind.Object &&
            totalNode.TryGetProperty("value", out var valueNode) &&
            valueNode.TryGetInt64(out totalCount))
        {
            return true;
        }

        return false;
    }

    internal static string? BuildNextCursor(JsonElement hit)
    {
        if (!hit.TryGetProperty("sort", out var sortNode) || sortNode.ValueKind != JsonValueKind.Array)
            return null;

        var payload = Encoding.UTF8.GetBytes(sortNode.GetRawText());
        return Convert.ToBase64String(payload);
    }

    internal static string BuildIndexInitializationPayload(
        DocumentIndexMetadata indexMetadata,
        JsonSerializerOptions jsonOptions)
    {
        var mappings = indexMetadata.Mappings.Count == 0
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["dynamic"] = true,
            }
            : new Dictionary<string, object?>(indexMetadata.Mappings, StringComparer.Ordinal);

        var root = new Dictionary<string, object?>
        {
            ["mappings"] = mappings,
        };

        if (indexMetadata.Settings.Count > 0)
        {
            root["settings"] = new Dictionary<string, object?>(
                indexMetadata.Settings,
                StringComparer.Ordinal);
        }

        if (indexMetadata.Aliases.Count > 0)
        {
            root["aliases"] = new Dictionary<string, object?>(
                indexMetadata.Aliases,
                StringComparer.Ordinal);
        }

        return JsonSerializer.Serialize(root, jsonOptions);
    }

    private static object BuildFilterSpec(ProjectionDocumentQuery query)
    {
        if (query.Filters.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["match_all"] = new Dictionary<string, object?>(),
            };
        }

        return new Dictionary<string, object?>
        {
            ["bool"] = new Dictionary<string, object?>
            {
                ["filter"] = query.Filters.Select(BuildSingleFilterSpec).ToArray(),
            },
        };
    }

    private static object BuildSingleFilterSpec(ProjectionDocumentFilter filter)
    {
        return filter.Operator switch
        {
            ProjectionDocumentFilterOperator.Exists => new Dictionary<string, object?>
            {
                ["exists"] = new Dictionary<string, object?>
                {
                    ["field"] = filter.FieldPath,
                },
            },
            ProjectionDocumentFilterOperator.Eq => new Dictionary<string, object?>
            {
                ["term"] = new Dictionary<string, object?>
                {
                    [filter.FieldPath] = ConvertScalarValue(filter.Value),
                },
            },
            ProjectionDocumentFilterOperator.In => new Dictionary<string, object?>
            {
                ["terms"] = new Dictionary<string, object?>
                {
                    [filter.FieldPath] = ConvertCollectionValue(filter.Value),
                },
            },
            ProjectionDocumentFilterOperator.Gt or
            ProjectionDocumentFilterOperator.Gte or
            ProjectionDocumentFilterOperator.Lt or
            ProjectionDocumentFilterOperator.Lte => new Dictionary<string, object?>
            {
                ["range"] = new Dictionary<string, object?>
                {
                    [filter.FieldPath] = new Dictionary<string, object?>
                    {
                        [ResolveRangeOperator(filter.Operator)] = ConvertScalarValue(filter.Value),
                    },
                },
            },
            _ => throw new InvalidOperationException(
                $"Unsupported projection document filter operator '{filter.Operator}'."),
        };
    }

    private static object[] BuildSortSpec(ProjectionDocumentQuery query, string defaultSortField)
    {
        if (query.Sorts.Count == 0)
        {
            var primarySortField = string.IsNullOrWhiteSpace(defaultSortField)
                ? DefaultQueryPrimarySortField
                : defaultSortField.Trim();
            return
            [
                BuildSortClause(primarySortField, ProjectionDocumentSortDirection.Desc, includeMissingHints: true),
                BuildTiebreakSortClause(),
            ];
        }

        var clauses = query.Sorts
            .Select(sort => BuildSortClause(sort.FieldPath, sort.Direction, includeMissingHints: false))
            .ToList();
        clauses.Add(BuildTiebreakSortClause());
        return clauses.ToArray();
    }

    private static object BuildSortClause(
        string fieldPath,
        ProjectionDocumentSortDirection direction,
        bool includeMissingHints)
    {
        var spec = new Dictionary<string, object?>
        {
            ["order"] = direction == ProjectionDocumentSortDirection.Asc ? "asc" : "desc",
        };

        if (includeMissingHints)
        {
            spec["missing"] = "_last";
            spec["unmapped_type"] = "date";
        }

        return new Dictionary<string, object?>
        {
            [fieldPath] = spec,
        };
    }

    private static object BuildTiebreakSortClause()
    {
        var spec = new Dictionary<string, object?>
        {
            ["order"] = "desc",
            ["missing"] = "_last",
            ["unmapped_type"] = "keyword",
        };

        return new Dictionary<string, object?>
        {
            [DefaultQueryTiebreakSortField] = spec,
        };
    }

    private static string ResolveRangeOperator(ProjectionDocumentFilterOperator filterOperator)
    {
        return filterOperator switch
        {
            ProjectionDocumentFilterOperator.Gt => "gt",
            ProjectionDocumentFilterOperator.Gte => "gte",
            ProjectionDocumentFilterOperator.Lt => "lt",
            ProjectionDocumentFilterOperator.Lte => "lte",
            _ => throw new InvalidOperationException(
                $"Unsupported range projection document filter operator '{filterOperator}'."),
        };
    }

    private static object? ConvertScalarValue(ProjectionDocumentValue value)
    {
        return value.Kind switch
        {
            ProjectionDocumentValueKind.String => value.RawValue as string,
            ProjectionDocumentValueKind.Int64 => value.RawValue,
            ProjectionDocumentValueKind.Double => value.RawValue,
            ProjectionDocumentValueKind.Bool => value.RawValue,
            ProjectionDocumentValueKind.DateTime when value.RawValue is DateTime dateTime => dateTime.ToString("O"),
            ProjectionDocumentValueKind.StringList when value.RawValue is string[] stringValues && stringValues.Length > 0 => stringValues[0],
            ProjectionDocumentValueKind.Int64List when value.RawValue is long[] int64Values && int64Values.Length > 0 => int64Values[0],
            ProjectionDocumentValueKind.DoubleList when value.RawValue is double[] doubleValues && doubleValues.Length > 0 => doubleValues[0],
            ProjectionDocumentValueKind.BoolList when value.RawValue is bool[] boolValues && boolValues.Length > 0 => boolValues[0],
            ProjectionDocumentValueKind.DateTimeList when value.RawValue is DateTime[] dateTimes && dateTimes.Length > 0 => dateTimes[0].ToString("O"),
            _ => null,
        };
    }

    private static object[] ConvertCollectionValue(ProjectionDocumentValue value)
    {
        return value.Kind switch
        {
            ProjectionDocumentValueKind.StringList when value.RawValue is string[] stringValues =>
                stringValues.Cast<object>().ToArray(),
            ProjectionDocumentValueKind.Int64List when value.RawValue is long[] int64Values =>
                int64Values.Cast<object>().ToArray(),
            ProjectionDocumentValueKind.DoubleList when value.RawValue is double[] doubleValues =>
                doubleValues.Cast<object>().ToArray(),
            ProjectionDocumentValueKind.BoolList when value.RawValue is bool[] boolValues =>
                boolValues.Cast<object>().ToArray(),
            ProjectionDocumentValueKind.DateTimeList when value.RawValue is DateTime[] dateTimes =>
                dateTimes.Select(x => (object)x.ToString("O")).ToArray(),
            _ => [ConvertScalarValue(value) ?? string.Empty],
        };
    }

    private static object?[]? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        try
        {
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            using var jsonDoc = JsonDocument.Parse(payload);
            if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Projection document cursor payload must be a JSON array.");

            return jsonDoc.RootElement.EnumerateArray()
                .Select(ConvertCursorValue)
                .ToArray();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("Invalid Elasticsearch projection document query cursor.", ex);
        }
    }

    private static object? ConvertCursorValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }
}
