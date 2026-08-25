using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Hosting.NyxId;

internal static class NyxIdModelSourceInventoryParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    internal static NyxIdPlatformModelSourceInventory ParsePlatformCatalogServices(string json)
    {
        var response = Deserialize<PlatformServiceListResponse>(json, "platform service inventory");
        if (response.Services is null)
            throw Invalid("platform service inventory", "services is required");

        var services = new List<NyxIdPlatformModelSourceService>(response.Services.Count);
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < response.Services.Count; index++)
        {
            var service = response.Services[index]
                ?? throw Invalid("platform service inventory", $"services[{index}] must be an object");
            var catalogServiceId = RequireNonBlank(
                service.Id,
                "platform service inventory",
                $"services[{index}].id");
            if (!serviceIds.Add(catalogServiceId))
            {
                throw Invalid(
                    "platform service inventory",
                    $"services[{index}].id duplicates catalog service '{catalogServiceId}'");
            }

            services.Add(new NyxIdPlatformModelSourceService(
                catalogServiceId,
                RequireNonBlank(service.Slug, "platform service inventory", $"services[{index}].slug"),
                RequireNonBlank(service.Name, "platform service inventory", $"services[{index}].name"),
                service.IsActive
                    ?? throw Invalid("platform service inventory", $"services[{index}].is_active is required"),
                ParseServiceType(
                    service.ServiceType,
                    required: true,
                    "platform service inventory",
                    $"services[{index}].service_type"),
                ParseVisibility(
                    service.Visibility,
                    "platform service inventory",
                    $"services[{index}].visibility"),
                ParseAuthMethod(
                    service.AuthMethod,
                    "platform service inventory",
                    $"services[{index}].auth_method"),
                ParseServiceCategory(
                    service.ServiceCategory,
                    "platform service inventory",
                    $"services[{index}].service_category"),
                service.RequiresUserCredential
                    ?? throw Invalid(
                        "platform service inventory",
                        $"services[{index}].requires_user_credential is required")));
        }

        return new NyxIdPlatformModelSourceInventory(services);
    }

    internal static NyxIdScopeModelSourceInventory ParseScopeKeys(string json)
    {
        var response = Deserialize<ScopeKeyListResponse>(json, "scope key inventory");
        if (response.Keys is null)
            throw Invalid("scope key inventory", "keys is required");

        var services = new List<NyxIdScopeModelSourceService>(response.Keys.Count);
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < response.Keys.Count; index++)
        {
            var service = response.Keys[index]
                ?? throw Invalid("scope key inventory", $"keys[{index}] must be an object");
            var userServiceId = RequireNonBlank(
                service.Id,
                "scope key inventory",
                $"keys[{index}].id");
            if (!serviceIds.Add(userServiceId))
            {
                throw Invalid(
                    "scope key inventory",
                    $"keys[{index}].id duplicates user service '{userServiceId}'");
            }

            var catalogServiceId = OptionalNonBlank(
                service.CatalogServiceId,
                "scope key inventory",
                $"keys[{index}].catalog_service_id");
            services.Add(new NyxIdScopeModelSourceService(
                userServiceId,
                catalogServiceId,
                RequireNonBlank(service.Slug, "scope key inventory", $"keys[{index}].slug"),
                service.Label,
                service.CatalogServiceName,
                service.IsActive
                    ?? throw Invalid("scope key inventory", $"keys[{index}].is_active is required"),
                ParseServiceType(
                    service.ServiceType,
                    required: true,
                    "scope key inventory",
                    $"keys[{index}].service_type"),
                ParseCredentialSource(
                    service.CredentialSource,
                    "scope key inventory",
                    $"keys[{index}].credential_source"),
                ParseCredentialStatus(
                    service.Status,
                    "scope key inventory",
                    $"keys[{index}].status"),
                service.CredentialMissing
                    ?? throw Invalid("scope key inventory", $"keys[{index}].credential_missing is required"),
                ParseConnectionStatus(
                    service.ConnectionStatus,
                    "scope key inventory",
                    $"keys[{index}].connection_status"),
                OptionalNonBlank(
                    service.NodeId,
                    "scope key inventory",
                    $"keys[{index}].node_id"),
                ParseNodeStatus(
                    service.NodeStatus,
                    "scope key inventory",
                    $"keys[{index}].node_status")));
        }

        return new NyxIdScopeModelSourceInventory(services);
    }

    private static NyxIdModelSourceCredentialStatus ParseCredentialStatus(
        string? value,
        string inventoryName,
        string fieldName)
    {
        var wireValue = RequireNonBlank(value, inventoryName, fieldName);
        var kind = wireValue switch
        {
            "active" => NyxIdModelSourceCredentialStatusKind.Active,
            "expired" => NyxIdModelSourceCredentialStatusKind.Expired,
            "revoked" => NyxIdModelSourceCredentialStatusKind.Revoked,
            "failed" => NyxIdModelSourceCredentialStatusKind.Failed,
            "refresh_failed" => NyxIdModelSourceCredentialStatusKind.RefreshFailed,
            "pending_auth" => NyxIdModelSourceCredentialStatusKind.PendingAuth,
            _ => NyxIdModelSourceCredentialStatusKind.Unknown,
        };
        return new NyxIdModelSourceCredentialStatus(kind, wireValue);
    }

    private static NyxIdModelSourceConnectionStatus ParseConnectionStatus(
        string? value,
        string inventoryName,
        string fieldName)
    {
        if (value is null)
        {
            return new NyxIdModelSourceConnectionStatus(
                NyxIdModelSourceConnectionStatusKind.NotApplicable,
                WireValue: null);
        }
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(inventoryName, $"{fieldName} must not be blank");

        var kind = value switch
        {
            "active" => NyxIdModelSourceConnectionStatusKind.Active,
            "expired" => NyxIdModelSourceConnectionStatusKind.Expired,
            _ => NyxIdModelSourceConnectionStatusKind.Unknown,
        };
        return new NyxIdModelSourceConnectionStatus(kind, value);
    }

    private static NyxIdModelSourceNodeStatus ParseNodeStatus(
        string? value,
        string inventoryName,
        string fieldName)
    {
        if (value is null)
        {
            return new NyxIdModelSourceNodeStatus(
                NyxIdModelSourceNodeStatusKind.NotApplicable,
                WireValue: null);
        }
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(inventoryName, $"{fieldName} must not be blank");

        var kind = value switch
        {
            "online" => NyxIdModelSourceNodeStatusKind.Online,
            "offline" => NyxIdModelSourceNodeStatusKind.Offline,
            "draining" => NyxIdModelSourceNodeStatusKind.Draining,
            "inaccessible" => NyxIdModelSourceNodeStatusKind.Inaccessible,
            _ => NyxIdModelSourceNodeStatusKind.Unknown,
        };
        return new NyxIdModelSourceNodeStatus(kind, value);
    }

    private static T Deserialize<T>(string json, string inventoryName)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw Invalid(inventoryName, "response body must be an object");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"NyxID {inventoryName} response is invalid JSON.", ex);
        }
    }

    private static NyxIdModelSourceServiceType ParseServiceType(
        string? value,
        bool required,
        string inventoryName,
        string fieldName)
    {
        if (value is null)
        {
            if (required)
                throw Invalid(inventoryName, $"{fieldName} is required");

            return new NyxIdModelSourceServiceType(
                NyxIdModelSourceServiceTypeKind.Unknown,
                WireValue: null);
        }

        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(inventoryName, $"{fieldName} must not be blank");

        var kind = value switch
        {
            "http" => NyxIdModelSourceServiceTypeKind.HTTP,
            "ssh" => NyxIdModelSourceServiceTypeKind.SSH,
            _ => NyxIdModelSourceServiceTypeKind.Unknown,
        };
        return new NyxIdModelSourceServiceType(kind, value);
    }

    private static NyxIdCatalogServiceVisibility ParseVisibility(
        string? value,
        string inventoryName,
        string fieldName)
    {
        var wireValue = RequireNonBlank(value, inventoryName, fieldName);
        var kind = wireValue switch
        {
            "public" => NyxIdCatalogServiceVisibilityKind.Public,
            "private" => NyxIdCatalogServiceVisibilityKind.Private,
            _ => NyxIdCatalogServiceVisibilityKind.Unknown,
        };
        return new NyxIdCatalogServiceVisibility(kind, wireValue);
    }

    private static NyxIdCatalogServiceAuthMethod ParseAuthMethod(
        string? value,
        string inventoryName,
        string fieldName)
    {
        var wireValue = RequireNonBlank(value, inventoryName, fieldName);
        var kind = wireValue switch
        {
            "header" => NyxIdCatalogServiceAuthMethodKind.Header,
            "bearer" => NyxIdCatalogServiceAuthMethodKind.Bearer,
            "bot_bearer" => NyxIdCatalogServiceAuthMethodKind.BotBearer,
            "query" => NyxIdCatalogServiceAuthMethodKind.Query,
            "basic" => NyxIdCatalogServiceAuthMethodKind.Basic,
            "body" => NyxIdCatalogServiceAuthMethodKind.Body,
            "token_exchange" => NyxIdCatalogServiceAuthMethodKind.TokenExchange,
            "path" => NyxIdCatalogServiceAuthMethodKind.Path,
            "oidc" => NyxIdCatalogServiceAuthMethodKind.OIDC,
            "none" => NyxIdCatalogServiceAuthMethodKind.None,
            "aws_sigv4" => NyxIdCatalogServiceAuthMethodKind.AWSSigV4,
            _ => NyxIdCatalogServiceAuthMethodKind.Unknown,
        };
        return new NyxIdCatalogServiceAuthMethod(kind, wireValue);
    }

    private static NyxIdCatalogServiceCategory ParseServiceCategory(
        string? value,
        string inventoryName,
        string fieldName)
    {
        var wireValue = RequireNonBlank(value, inventoryName, fieldName);
        var kind = wireValue switch
        {
            "provider" => NyxIdCatalogServiceCategoryKind.Provider,
            "connection" => NyxIdCatalogServiceCategoryKind.Connection,
            "internal" => NyxIdCatalogServiceCategoryKind.Internal,
            _ => NyxIdCatalogServiceCategoryKind.Unknown,
        };
        return new NyxIdCatalogServiceCategory(kind, wireValue);
    }

    private static NyxIdScopeCredentialSource ParseCredentialSource(
        JsonElement? value,
        string inventoryName,
        string fieldName)
    {
        if (value is not { ValueKind: JsonValueKind.Object } source)
            throw Invalid(inventoryName, $"{fieldName} must be an object");

        var type = RequireJsonString(source, "type", inventoryName, $"{fieldName}.type");
        return type switch
        {
            "personal" => new NyxIdPersonalCredentialSource(),
            "org" => new NyxIdOrganizationCredentialSource(
                RequireJsonString(source, "org_id", inventoryName, $"{fieldName}.org_id"),
                RequireJsonString(source, "org_name", inventoryName, $"{fieldName}.org_name"),
                ReadOptionalJsonString(source, "avatar_url", inventoryName, $"{fieldName}.avatar_url"),
                ParseOrganizationRole(
                    RequireJsonString(source, "role", inventoryName, $"{fieldName}.role"),
                    inventoryName,
                    $"{fieldName}.role"),
                RequireJsonBoolean(source, "allowed", inventoryName, $"{fieldName}.allowed")),
            _ => throw Invalid(inventoryName, $"{fieldName}.type is unsupported"),
        };
    }

    private static NyxIdScopeOrganizationRole ParseOrganizationRole(
        string value,
        string inventoryName,
        string fieldName) =>
        value switch
        {
            "admin" => NyxIdScopeOrganizationRole.Admin,
            "member" => NyxIdScopeOrganizationRole.Member,
            "viewer" => NyxIdScopeOrganizationRole.Viewer,
            _ => throw Invalid(inventoryName, $"{fieldName} is unsupported"),
        };

    private static string RequireJsonString(
        JsonElement owner,
        string propertyName,
        string inventoryName,
        string fieldName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw Invalid(inventoryName, $"{fieldName} must be a string");

        return RequireNonBlank(value.GetString(), inventoryName, fieldName);
    }

    private static string? ReadOptionalJsonString(
        JsonElement owner,
        string propertyName,
        string inventoryName,
        string fieldName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(inventoryName, $"{fieldName} must be a string or null");

        return value.GetString();
    }

    private static bool RequireJsonBoolean(
        JsonElement owner,
        string propertyName,
        string inventoryName,
        string fieldName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Invalid(inventoryName, $"{fieldName} must be a boolean");
        }

        return value.GetBoolean();
    }

    private static string RequireNonBlank(string? value, string inventoryName, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(inventoryName, $"{fieldName} is required");

        return value;
    }

    private static string? OptionalNonBlank(string? value, string inventoryName, string fieldName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw Invalid(inventoryName, $"{fieldName} must not be blank");

        return value;
    }

    private static InvalidDataException Invalid(string inventoryName, string detail) =>
        new($"NyxID {inventoryName} response is invalid: {detail}.");

    private sealed class PlatformServiceListResponse
    {
        [JsonPropertyName("services")]
        public List<PlatformService?>? Services { get; init; }
    }

    private sealed class PlatformService
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("is_active")]
        public bool? IsActive { get; init; }

        [JsonPropertyName("service_type")]
        public string? ServiceType { get; init; }

        [JsonPropertyName("visibility")]
        public string? Visibility { get; init; }

        [JsonPropertyName("auth_method")]
        public string? AuthMethod { get; init; }

        [JsonPropertyName("service_category")]
        public string? ServiceCategory { get; init; }

        [JsonPropertyName("requires_user_credential")]
        public bool? RequiresUserCredential { get; init; }
    }

    private sealed class ScopeKeyListResponse
    {
        [JsonPropertyName("keys")]
        public List<ScopeKey?>? Keys { get; init; }
    }

    private sealed class ScopeKey
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("catalog_service_id")]
        public string? CatalogServiceId { get; init; }

        [JsonPropertyName("slug")]
        public string? Slug { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }

        [JsonPropertyName("catalog_service_name")]
        public string? CatalogServiceName { get; init; }

        [JsonPropertyName("is_active")]
        public bool? IsActive { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("connection_status")]
        public string? ConnectionStatus { get; init; }

        [JsonPropertyName("credential_missing")]
        public bool? CredentialMissing { get; init; }

        [JsonPropertyName("node_id")]
        public string? NodeId { get; init; }

        [JsonPropertyName("node_status")]
        public string? NodeStatus { get; init; }

        [JsonPropertyName("service_type")]
        public string? ServiceType { get; init; }

        [JsonPropertyName("credential_source")]
        public JsonElement? CredentialSource { get; init; }
    }
}
