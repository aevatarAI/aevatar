using System.Text.Json;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal static class ElasticsearchProjectionDocumentStorePayloadSupport
{
    private const string DefaultListPrimarySortField = "CreatedAt";
    private const string DefaultListTiebreakSortField = "_id";

    internal static string BuildListPayloadJson(string listSortField, int size)
    {
        var sort = string.IsNullOrWhiteSpace(listSortField)
            ? BuildDefaultSortSpec()
            : BuildConfiguredSortSpec(listSortField.Trim());

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
}
