using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.NyxId.Tools;

internal static partial class NyxIdAssistantReadResponseProjector
{
    private const int MaxAssistantListItems = 20;
    private const int MaxAssistantProjectionBytes = 32 * 1024;
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

    public static string ProjectDurableGrants(string json, string expectedApiKeyId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryProjectError(document.RootElement, out var error))
                return error;
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("grants", out var grants) ||
                grants.ValueKind != JsonValueKind.Array)
            {
                return InvalidResponseJson;
            }

            var projected = new List<DurableGrantProjection>();
            var grantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grant in grants.EnumerateArray())
            {
                if (!TryProjectDurableGrant(grant, expectedApiKeyId, out var receipt) ||
                    !grantIds.Add(receipt.Id))
                {
                    return InvalidResponseJson;
                }
                projected.Add(receipt);
            }

            return ProjectDurableGrantList(projected);
        }
        catch (JsonException)
        {
            return InvalidResponseJson;
        }
    }

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
                return ProjectOAuthBindingList(projected);

            var matches = projected.Where(binding =>
                    string.Equals(binding["binding_hash"] as string, exactBindingHash, StringComparison.Ordinal))
                .ToArray();
            return matches.Length switch
            {
                0 => OAuthBindingNotFoundJson,
                1 => SerializeWithinAssistantProjectionBudget(matches[0]),
                _ => InvalidResponseJson,
            };
        }
        catch (JsonException)
        {
            return InvalidResponseJson;
        }
    }

    private static string ProjectOAuthBindingList(
        IReadOnlyCollection<Dictionary<string, object?>> projected)
    {
        var returned = new List<Dictionary<string, object?>>();
        string result = SerializeOAuthBindingList(returned, projected.Count);
        foreach (var binding in projected.Take(MaxAssistantListItems))
        {
            returned.Add(binding);
            var candidate = SerializeOAuthBindingList(returned, projected.Count);
            if (Encoding.UTF8.GetByteCount(candidate) > MaxAssistantProjectionBytes)
            {
                returned.RemoveAt(returned.Count - 1);
                break;
            }
            result = candidate;
        }

        return result;
    }

    private static string SerializeOAuthBindingList(
        IReadOnlyCollection<Dictionary<string, object?>> returned,
        int total) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["bindings"] = returned,
            ["total"] = total,
            ["returned"] = returned.Count,
            ["truncated"] = returned.Count < total,
        });

    private static string SerializeWithinAssistantProjectionBudget(
        Dictionary<string, object?> binding)
    {
        var result = JsonSerializer.Serialize(binding);
        return Encoding.UTF8.GetByteCount(result) <= MaxAssistantProjectionBytes
            ? result
            : InvalidResponseJson;
    }

    public static string ProjectServiceAccounts(string json, bool isList, int expectedPage)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryProjectError(document.RootElement, out var error))
                return error;

            if (!isList)
            {
                return TryProjectServiceAccount(document.RootElement, out var serviceAccount)
                    ? SerializeWithinAssistantProjectionBudget(serviceAccount)
                    : InvalidResponseJson;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("service_accounts", out var serviceAccounts) ||
                serviceAccounts.ValueKind != JsonValueKind.Array)
            {
                return InvalidResponseJson;
            }

            var projected = new List<Dictionary<string, object?>>();
            foreach (var item in serviceAccounts.EnumerateArray())
            {
                if (!TryProjectServiceAccount(item, out var serviceAccount))
                    return InvalidResponseJson;
                projected.Add(serviceAccount);
            }

            if (!TryGetRequiredUnsignedInteger(document.RootElement, "total", out var total) ||
                !TryGetRequiredUnsignedInteger(document.RootElement, "page", out var page) ||
                !TryGetRequiredUnsignedInteger(document.RootElement, "per_page", out var perPage) ||
                page != (ulong)expectedPage ||
                perPage != NyxIdServiceAccountsTool.AssistantPageSize ||
                projected.Count > (int)perPage ||
                projected.Count > 0 &&
                (page - 1) * perPage + (ulong)projected.Count > total)
            {
                return InvalidResponseJson;
            }

            return ProjectServiceAccountList(projected, total, page, perPage);
        }
        catch (JsonException)
        {
            return InvalidResponseJson;
        }
    }

    private static string ProjectServiceAccountList(
        IReadOnlyCollection<Dictionary<string, object?>> projected,
        ulong total,
        ulong page,
        ulong perPage)
    {
        var returned = new List<Dictionary<string, object?>>();
        string result = SerializeServiceAccountList(returned, projected.Count, total, page, perPage);
        foreach (var serviceAccount in projected.Take(MaxAssistantListItems))
        {
            returned.Add(serviceAccount);
            var candidate = SerializeServiceAccountList(returned, projected.Count, total, page, perPage);
            if (Encoding.UTF8.GetByteCount(candidate) > MaxAssistantProjectionBytes)
            {
                returned.RemoveAt(returned.Count - 1);
                break;
            }
            result = candidate;
        }

        return result;
    }

    private static string SerializeServiceAccountList(
        IReadOnlyCollection<Dictionary<string, object?>> returned,
        int pageItemCount,
        ulong total,
        ulong page,
        ulong perPage) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["service_accounts"] = returned,
            ["total"] = total,
            ["page"] = page,
            ["per_page"] = perPage,
            ["returned"] = returned.Count,
            ["truncated"] = returned.Count < pageItemCount || page * perPage < total,
        });

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

    private static bool TryProjectDurableGrant(
        JsonElement source,
        string expectedApiKeyId,
        out DurableGrantProjection receipt)
    {
        receipt = default!;
        if (source.ValueKind != JsonValueKind.Object ||
            !TryReadRequiredString(source, "id", out var id) ||
            !TryReadRequiredString(source, "api_key_id", out var apiKeyId) ||
            !string.Equals(apiKeyId, expectedApiKeyId, StringComparison.Ordinal) ||
            !TryReadRequiredString(source, "user_service_id", out var userServiceId) ||
            !TryReadRequiredString(source, "endpoint_id", out var endpointId) ||
            !TryReadRequiredString(source, "method", out var method) ||
            method is not ("POST" or "PUT" or "PATCH") ||
            !TryReadRequiredString(source, "normalized_path_template", out var pathTemplate) ||
            !TryReadRequiredString(source, "contract_digest", out var contractDigest) ||
            !Sha256DigestRegex().IsMatch(contractDigest) ||
            !TryReadRequiredTimestamp(source, "valid_from", out var validFrom) ||
            !TryReadRequiredTimestamp(source, "expires_at", out var expiresAt) ||
            expiresAt <= validFrom ||
            !TryReadDurableGrantCounters(
                source,
                out var totalLimit,
                out var totalUsed,
                out var windowUsed,
                out var stateVersion) ||
            !TryReadRequiredString(source, "replay_policy", out var replayPolicy) ||
            replayPolicy is not ("non_replayable" or "downstream_idempotency_key") ||
            !TryReadOptionalTimestamp(source, "revoked_at", out var revokedAt) ||
            !TryReadOptionalString(source, "reauthorized_from", out var reauthorizedFrom) ||
            !TryReadRequiredTimestamp(source, "created_at", out var createdAt))
        {
            return false;
        }

        receipt = new DurableGrantProjection(
            id,
            apiKeyId,
            userServiceId,
            endpointId,
            method,
            pathTemplate,
            contractDigest,
            validFrom,
            expiresAt,
            totalLimit,
            totalUsed,
            windowUsed,
            replayPolicy,
            revokedAt,
            stateVersion,
            reauthorizedFrom,
            createdAt);
        return true;
    }

    private static bool TryReadDurableGrantCounters(
        JsonElement source,
        out long totalLimit,
        out long totalUsed,
        out long windowUsed,
        out long stateVersion)
    {
        totalLimit = 0;
        totalUsed = 0;
        windowUsed = 0;
        stateVersion = 0;
        return TryReadRequiredNonNegativeInteger(source, "total_limit", out totalLimit) &&
               totalLimit > 0 &&
               TryReadRequiredNonNegativeInteger(source, "total_used", out totalUsed) &&
               totalUsed <= totalLimit &&
               TryReadRequiredNonNegativeInteger(source, "window_used", out windowUsed) &&
               TryReadRequiredPositiveInteger(source, "state_version", out stateVersion);
    }

    private static string ProjectDurableGrantList(
        IReadOnlyCollection<DurableGrantProjection> projected)
    {
        var returned = new List<DurableGrantProjection>();
        string result = SerializeDurableGrantList(returned, projected.Count);
        foreach (var grant in projected.Take(MaxAssistantListItems))
        {
            returned.Add(grant);
            var candidate = SerializeDurableGrantList(returned, projected.Count);
            if (Encoding.UTF8.GetByteCount(candidate) > MaxAssistantProjectionBytes)
            {
                returned.RemoveAt(returned.Count - 1);
                break;
            }
            result = candidate;
        }

        return result;
    }

    private static string SerializeDurableGrantList(
        IReadOnlyCollection<DurableGrantProjection> returned,
        int total) =>
        JsonSerializer.Serialize(new DurableGrantListProjection(
            returned,
            total,
            returned.Count,
            returned.Count < total));

    private static bool TryReadRequiredString(
        JsonElement source,
        string property,
        out string value)
    {
        value = string.Empty;
        if (!source.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }
        value = element.GetString()!;
        return string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
               value.Length <= 2048 &&
               !value.Any(char.IsControl);
    }

    private static bool TryReadOptionalString(
        JsonElement source,
        string property,
        out string? value)
    {
        value = null;
        if (!source.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString();
        return value is not null &&
               string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
               value.Length <= 2048 &&
               !value.Any(char.IsControl);
    }

    private static bool TryReadRequiredTimestamp(
        JsonElement source,
        string property,
        out DateTimeOffset value)
    {
        value = default;
        return TryReadRequiredString(source, property, out var text) &&
               Rfc3339TimestampRegex().IsMatch(text) &&
               DateTimeOffset.TryParse(
                   text,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind,
                   out value);
    }

    private static bool TryReadOptionalTimestamp(
        JsonElement source,
        string property,
        out DateTimeOffset? value)
    {
        value = null;
        if (!source.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { } text ||
            !Rfc3339TimestampRegex().IsMatch(text) ||
            !DateTimeOffset.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }
        value = parsed;
        return true;
    }

    private static bool TryReadRequiredNonNegativeInteger(
        JsonElement source,
        string property,
        out long value)
    {
        value = 0;
        return source.TryGetProperty(property, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out value) &&
               value >= 0;
    }

    private static bool TryReadRequiredPositiveInteger(
        JsonElement source,
        string property,
        out long value) =>
        TryReadRequiredNonNegativeInteger(source, property, out value) && value > 0;

    private sealed record DurableGrantListProjection(
        [property: System.Text.Json.Serialization.JsonPropertyName("grants")]
        IReadOnlyCollection<DurableGrantProjection> Grants,
        [property: System.Text.Json.Serialization.JsonPropertyName("total")] int Total,
        [property: System.Text.Json.Serialization.JsonPropertyName("returned")] int Returned,
        [property: System.Text.Json.Serialization.JsonPropertyName("truncated")] bool Truncated);

    private sealed record DurableGrantProjection(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("api_key_id")] string ApiKeyId,
        [property: System.Text.Json.Serialization.JsonPropertyName("user_service_id")] string UserServiceId,
        [property: System.Text.Json.Serialization.JsonPropertyName("endpoint_id")] string EndpointId,
        [property: System.Text.Json.Serialization.JsonPropertyName("method")] string Method,
        [property: System.Text.Json.Serialization.JsonPropertyName("normalized_path_template")] string NormalizedPathTemplate,
        [property: System.Text.Json.Serialization.JsonPropertyName("contract_digest")] string ContractDigest,
        [property: System.Text.Json.Serialization.JsonPropertyName("valid_from")] DateTimeOffset ValidFrom,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("total_limit")] long TotalLimit,
        [property: System.Text.Json.Serialization.JsonPropertyName("total_used")] long TotalUsed,
        [property: System.Text.Json.Serialization.JsonPropertyName("window_used")] long WindowUsed,
        [property: System.Text.Json.Serialization.JsonPropertyName("replay_policy")] string ReplayPolicy,
        [property: System.Text.Json.Serialization.JsonPropertyName("revoked_at")]
        DateTimeOffset? RevokedAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("state_version")] long StateVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("reauthorized_from")]
        string? ReauthorizedFrom,
        [property: System.Text.Json.Serialization.JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

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

    private static bool TryProjectServiceAccount(
        JsonElement source,
        out Dictionary<string, object?> projected)
    {
        projected = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source.ValueKind != JsonValueKind.Object ||
            !TryCopyRequiredString(source, projected, "id") ||
            !TryCopyRequiredString(source, projected, "client_id") ||
            !TryCopyRequiredString(source, projected, "allowed_scopes") ||
            !TryCopyRequiredStringArray(source, projected, "role_ids") ||
            !TryCopyRequiredBoolean(source, projected, "is_active") ||
            !TryCopyRequiredNullableUnsignedInteger(source, projected, "rate_limit_override") ||
            !TryCopyRequiredString(source, projected, "created_at") ||
            !TryCopyRequiredString(source, projected, "updated_at") ||
            !TryCopyRequiredNullableString(source, projected, "last_authenticated_at"))
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

    private static bool TryCopyRequiredNullableString(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value))
            return false;
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

    private static bool TryCopyRequiredBoolean(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
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

    private static bool TryCopyRequiredStringArray(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            return false;
        }
        projected[property] = value.EnumerateArray().Select(static item => item.GetString()).ToArray();
        return true;
    }

    private static bool TryGetRequiredUnsignedInteger(
        JsonElement source,
        string property,
        out ulong number)
    {
        number = 0;
        if (!source.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetUInt64(out number))
        {
            return false;
        }
        return true;
    }

    private static bool TryCopyRequiredNullableUnsignedInteger(
        JsonElement source,
        IDictionary<string, object?> projected,
        string property)
    {
        if (!source.TryGetProperty(property, out var value))
            return false;
        if (value.ValueKind == JsonValueKind.Null)
        {
            projected[property] = null;
            return true;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
            return false;
        projected[property] = number;
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

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256DigestRegex();

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339TimestampRegex();
}
