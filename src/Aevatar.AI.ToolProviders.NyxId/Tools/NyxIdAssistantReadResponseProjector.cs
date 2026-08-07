using System.Text.Json;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

internal static class NyxIdAssistantReadResponseProjector
{
    private const string InvalidResponseJson = "{\"error\":\"invalid_nyxid_response\"}";

    private static readonly string[] PendingCredentialProperties =
    [
        "id",
        "node_id",
        "service_slug",
        "injection_method",
        "field_name",
        "created_at",
        "expires_at",
        "consumed_at",
        "declined_at",
        "remote_state",
        "is_active",
    ];

    private static readonly string[] ServicePoolProperties =
    [
        "id",
        "user_id",
        "slug",
        "strategy",
        "rr_counter",
        "is_active",
        "created_at",
        "updated_at",
    ];

    private static readonly string[] ServicePoolMemberProperties =
    [
        "user_service_id",
        "weight",
        "enabled",
    ];

    public static string ProjectPendingNodeCredentials(string json) =>
        ProjectCollectionResponse(json, "pending_credentials", ProjectPendingCredential);

    public static string ProjectServicePools(string json, bool isList)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryProjectError(document.RootElement, out var error))
                return error;

            if (isList)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("pools", out var pools) ||
                    pools.ValueKind != JsonValueKind.Array ||
                    pools.EnumerateArray().Any(static pool => pool.ValueKind != JsonValueKind.Object))
                {
                    return InvalidResponseJson;
                }

                return JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["pools"] = pools.EnumerateArray().Select(ProjectServicePool).ToArray(),
                });
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return InvalidResponseJson;

            return JsonSerializer.Serialize(ProjectServicePool(document.RootElement));
        }
        catch (JsonException)
        {
            return InvalidResponseJson;
        }
    }

    private static string ProjectCollectionResponse(
        string json,
        string collectionProperty,
        Func<JsonElement, Dictionary<string, object?>> projectItem)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryProjectError(document.RootElement, out var error))
                return error;

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(collectionProperty, out var collection) ||
                collection.ValueKind != JsonValueKind.Array ||
                collection.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.Object))
            {
                return InvalidResponseJson;
            }

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [collectionProperty] = collection.EnumerateArray().Select(projectItem).ToArray(),
            });
        }
        catch (JsonException)
        {
            return InvalidResponseJson;
        }
    }

    private static Dictionary<string, object?> ProjectPendingCredential(JsonElement item) =>
        ProjectProperties(item, PendingCredentialProperties);

    private static Dictionary<string, object?> ProjectServicePool(JsonElement item)
    {
        var projected = ProjectProperties(item, ServicePoolProperties);
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("members", out var members) &&
            members.ValueKind == JsonValueKind.Array)
        {
            projected["members"] = members.EnumerateArray()
                .Select(member => ProjectProperties(member, ServicePoolMemberProperties))
                .ToArray();
        }

        return projected;
    }

    private static Dictionary<string, object?> ProjectProperties(
        JsonElement source,
        IEnumerable<string> properties)
    {
        var projected = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source.ValueKind != JsonValueKind.Object)
            return projected;

        foreach (var property in properties)
        {
            if (source.TryGetProperty(property, out var value))
                projected[property] = value.Clone();
        }

        return projected;
    }

    private static bool TryProjectError(JsonElement root, out string errorJson)
    {
        errorJson = string.Empty;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        var projected = new Dictionary<string, object?>
        {
            ["error"] = true,
        };
        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
            projected["status"] = status.Clone();
        if (root.TryGetProperty("retry_after_seconds", out var retryAfter) &&
            retryAfter.ValueKind == JsonValueKind.Number)
        {
            projected["retry_after_seconds"] = retryAfter.Clone();
        }

        errorJson = JsonSerializer.Serialize(projected);
        return true;
    }
}
