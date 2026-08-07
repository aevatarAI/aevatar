using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

internal static partial class NyxIdAssistantReadResponseProjector
{
    private const string InvalidResponseJson = "{\"error\":\"invalid_nyxid_response\"}";
    private const string OAuthBindingNotFoundJson = "{\"error\":\"oauth_binding_not_found\"}";

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

    public static bool IsExactOAuthBindingSelector(string selector) =>
        OAuthBindingSelectorRegex().IsMatch(selector);

    public static string ProjectDeveloperOAuthClients(string json, bool isList)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryProjectError(document.RootElement, out var error))
                return error;

            if (!isList)
            {
                return TryProjectDeveloperOAuthClient(document.RootElement, out var client)
                    ? JsonSerializer.Serialize(client)
                    : InvalidResponseJson;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("clients", out var clients) ||
                clients.ValueKind != JsonValueKind.Array)
            {
                return InvalidResponseJson;
            }

            var projected = new List<Dictionary<string, object?>>();
            foreach (var item in clients.EnumerateArray())
            {
                if (!TryProjectDeveloperOAuthClient(item, out var client))
                    return InvalidResponseJson;
                projected.Add(client);
            }

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["clients"] = projected,
            });
        }
        catch (JsonException)
        {
            return InvalidResponseJson;
        }
    }

    public static string ProjectOAuthBindings(string json, string? exactBindingHash)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryProjectError(document.RootElement, out var error))
                return error;
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("bindings", out var bindings) ||
                bindings.ValueKind != JsonValueKind.Array)
            {
                return InvalidResponseJson;
            }

            var projected = new List<Dictionary<string, object?>>();
            foreach (var item in bindings.EnumerateArray())
            {
                if (!TryProjectOAuthBinding(item, out var binding))
                    return InvalidResponseJson;
                projected.Add(binding);
            }

            if (exactBindingHash is null)
            {
                return JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["bindings"] = projected,
                });
            }

            var matches = projected.Where(binding =>
                    string.Equals(binding["binding_hash"] as string, exactBindingHash, StringComparison.Ordinal))
                .ToArray();
            return matches.Length switch
            {
                0 => OAuthBindingNotFoundJson,
                1 => JsonSerializer.Serialize(matches[0]),
                _ => InvalidResponseJson,
            };
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

    private static bool TryProjectDeveloperOAuthClient(
        JsonElement source,
        out Dictionary<string, object?> projected)
    {
        projected = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source.ValueKind != JsonValueKind.Object ||
            !TryCopyRequiredString(source, projected, "id") ||
            !TryCopyOptionalString(source, projected, "client_type") ||
            !TryCopyOptionalString(source, projected, "allowed_scopes") ||
            !TryCopyOptionalString(source, projected, "delegation_scopes") ||
            !TryCopyOptionalBoolean(source, projected, "broker_capability_enabled") ||
            !TryCopyOptionalBoolean(source, projected, "connection_webhook_enabled") ||
            !TryCopyOptionalBoolean(source, projected, "is_active") ||
            !TryCopyOptionalStringArray(source, projected, "default_service_catalog_slugs") ||
            !TryCopyOptionalString(source, projected, "created_at"))
        {
            projected.Clear();
            return false;
        }

        return true;
    }

    private static bool TryProjectOAuthBinding(
        JsonElement source,
        out Dictionary<string, object?> projected)
    {
        projected = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source.ValueKind != JsonValueKind.Object ||
            !TryCopyRequiredString(source, projected, "binding_hash") ||
            projected["binding_hash"] is not string bindingHash ||
            !IsExactOAuthBindingSelector(bindingHash) ||
            !TryCopyRequiredString(source, projected, "client_id") ||
            !TryCopyOptionalStringArray(source, projected, "scopes") ||
            !TryCopyOptionalString(source, projected, "created_at") ||
            !TryCopyOptionalNullableString(source, projected, "last_used_at") ||
            !TryCopyExternalSubject(source, projected))
        {
            projected.Clear();
            return false;
        }

        return true;
    }

    private static bool TryCopyExternalSubject(
        JsonElement source,
        IDictionary<string, object?> projected)
    {
        if (!source.TryGetProperty("external_subject", out var subject))
            return true;
        if (subject.ValueKind == JsonValueKind.Null)
        {
            projected["external_subject"] = null;
            return true;
        }
        if (subject.ValueKind != JsonValueKind.Object)
            return false;

        var safeSubject = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!TryCopyOptionalString(subject, safeSubject, "platform") ||
            !TryCopyOptionalNullableString(subject, safeSubject, "tenant") ||
            !TryCopyOptionalString(subject, safeSubject, "external_user_id"))
        {
            return false;
        }
        projected["external_subject"] = safeSubject;
        return true;
    }

    private static bool TryCopyRequiredString(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            return false;
        }
        projected[property] = value.GetString();
        return true;
    }

    private static bool TryCopyOptionalString(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value))
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        projected[property] = value.GetString();
        return true;
    }

    private static bool TryCopyOptionalNullableString(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value))
            return true;
        if (value.ValueKind == JsonValueKind.Null)
        {
            projected[property] = null;
            return true;
        }
        if (value.ValueKind != JsonValueKind.String)
            return false;
        projected[property] = value.GetString();
        return true;
    }

    private static bool TryCopyOptionalBoolean(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value))
            return true;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        projected[property] = value.GetBoolean();
        return true;
    }

    private static bool TryCopyOptionalStringArray(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            return false;
        }
        projected[property] = value.EnumerateArray().Select(static item => item.GetString()).ToArray();
        return true;
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

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex OAuthBindingSelectorRegex();
}
