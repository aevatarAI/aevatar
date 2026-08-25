using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aevatar.AI.ToolProviders.NyxId;

public enum NyxIdApiAccessFailureKind
{
    Unspecified = 0,
    Unauthorized = 1,
    Forbidden = 2,
    NotFound = 3,
    Conflict = 4,
    RateLimited = 5,
    Transport = 6,
    Transient = 7,
    Provider = 8,
    MalformedResponse = 9,
}

public sealed record NyxIdApiAccessFailure(
    NyxIdApiAccessFailureKind Kind,
    string Code,
    int HttpStatus = 0,
    int ProviderErrorCode = 0);

public sealed record NyxIdApiAccessResult<T>(
    T? Value,
    NyxIdApiAccessFailure? Failure)
    where T : class
{
    public bool Succeeded => Value is not null && Failure is null;

    internal static NyxIdApiAccessResult<T> Success(T value) => new(value, null);

    internal static NyxIdApiAccessResult<T> Failed(NyxIdApiAccessFailure failure) =>
        new(null, failure);
}

public enum NyxIdUserServiceCredentialSourceKind
{
    Unspecified = 0,
    Personal = 1,
    Organization = 2,
}

public enum NyxIdOrganizationRole
{
    Unspecified = 0,
    Admin = 1,
    Member = 2,
    Viewer = 3,
}

public sealed record NyxIdUserServiceCredentialSource(
    NyxIdUserServiceCredentialSourceKind Kind,
    string? OrganizationId = null,
    string? OrganizationName = null,
    string? AvatarUrl = null,
    NyxIdOrganizationRole OrganizationRole = NyxIdOrganizationRole.Unspecified,
    bool Allowed = true);

public sealed record NyxIdUserService(
    string Id,
    string Slug,
    string? Label,
    string? CatalogServiceName,
    bool IsActive,
    NyxIdUserServiceCredentialSource CredentialSource,
    string? DefaultModel = null,
    string? CatalogServiceId = null,
    bool? ForwardAccessToken = null,
    bool? InjectDelegationToken = null,
    string? DelegationTokenScope = null,
    bool AutoConnected = false);

public sealed record NyxIdUserServices(IReadOnlyList<NyxIdUserService> Services);

public enum NyxIdUserServiceCredentialStatus
{
    Unspecified = 0,
    Active = 1,
    Expired = 2,
    Revoked = 3,
    Failed = 4,
    RefreshFailed = 5,
    PendingAuthorization = 6,
}

public enum NyxIdOAuthConnectionStatus
{
    Unspecified = 0,
    Active = 1,
    Expired = 2,
}

public enum NyxIdUserServiceNodeStatus
{
    Unspecified = 0,
    NotBound = 1,
    Online = 2,
    Offline = 3,
    Draining = 4,
    Unknown = 5,
    Inaccessible = 6,
}

public sealed record NyxIdUserServiceKey(
    string Id,
    string Slug,
    string? Label,
    string? CatalogServiceName,
    bool IsActive,
    NyxIdUserServiceCredentialStatus CredentialStatus,
    string? NodeId,
    NyxIdUserServiceNodeStatus NodeStatus,
    NyxIdUserServiceCredentialSource CredentialSource,
    string? CatalogServiceId,
    string? CatalogServiceSlug,
    bool Connected,
    bool? AutoConnected = null);

public sealed record NyxIdUserServiceKeys(IReadOnlyList<NyxIdUserServiceKey> Services);

public sealed record NyxIdUserServiceAuthorizationEvidence(
    string UserServiceId,
    string? ApiKeyId,
    bool IsActive,
    NyxIdUserServiceCredentialStatus CredentialStatus,
    NyxIdOAuthConnectionStatus OAuthConnectionStatus,
    IReadOnlyList<string>? GrantedScopes,
    DateTimeOffset? LastAuthorizedAtUtc);

public sealed record NyxIdServiceAccessEvidence(
    string UserServiceId,
    string ServiceSlug);

public sealed record NyxIdApiKeyVersionEvidence(
    string? RotationPredecessorId,
    long StateVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record NyxIdAgentApiKeyEvidence(
    string Id,
    string Name,
    IReadOnlyList<string> Scopes,
    string? Platform,
    bool IsActive,
    IReadOnlyList<string> AllowedServiceIds,
    bool AllowAllServices,
    IReadOnlyList<string> AllowedNodeIds,
    bool AllowAllNodes,
    DateTimeOffset CreatedAtUtc,
    NyxIdApiKeyVersionEvidence? VersionEvidence);

public enum NyxIdScopePlanPrincipalKind
{
    Unspecified = 0,
    Personal = 1,
    Organization = 2,
}

public sealed record NyxIdScopePlanPrincipal(
    string Id,
    NyxIdScopePlanPrincipalKind Kind);

public enum NyxIdScopePlanNodeGrantKind
{
    Unspecified = 0,
    NotRequired = 1,
    Required = 2,
}

public sealed record NyxIdScopePlanNodeGrant(
    NyxIdScopePlanNodeGrantKind Kind,
    IReadOnlyList<string> NodeIds);

public sealed record NyxIdScopePlanServiceGrant(
    string UserServiceId,
    NyxIdScopePlanPrincipal ResourceOwner,
    NyxIdScopePlanNodeGrant NodeGrant);

public enum NyxIdScopePlanFreshnessMode
{
    Unspecified = 0,
    MutationRevalidatedSnapshot = 1,
}

public enum NyxIdScopePlanPostCreationDrift
{
    Unspecified = 0,
    FailClosed = 1,
}

public sealed record NyxIdScopePlanFreshness(
    NyxIdScopePlanFreshnessMode Mode,
    string PreconditionField,
    NyxIdScopePlanPostCreationDrift PostCreationDrift);

public enum NyxIdScopePlanRouteCandidateBasis
{
    Unspecified = 0,
    ActiveConfiguredRoutes = 1,
}

public sealed record NyxIdScopePlanCompleteness(
    bool ListComplete,
    bool NoDuplicates,
    NyxIdScopePlanRouteCandidateBasis RouteCandidateBasis,
    bool TransientNodeStateExcluded);

public sealed record NyxIdApiKeyScopePlan(
    string Authority,
    string ContractVersion,
    string PolicyVersion,
    NyxIdScopePlanPrincipal AuthenticatedActor,
    NyxIdScopePlanPrincipal IntendedKeyOwner,
    IReadOnlyList<NyxIdScopePlanServiceGrant> Services,
    IReadOnlyList<string> AllowedServiceIds,
    IReadOnlyList<string> AllowedNodeIds,
    DateTimeOffset EvaluatedAtUtc,
    string NormalizedGrantDigest,
    NyxIdScopePlanFreshness Freshness,
    NyxIdScopePlanCompleteness Completeness);

/// <summary>
/// Strictly validates the published NyxID inventory and scope-plan JSON at the
/// external adapter boundary. Unknown additive fields are ignored; every field
/// that contributes authorization semantics is required and typed.
/// </summary>
public static class NyxIdApiAccessResponseParser
{
    public const string ScopePlanAuthority = "nyxid";
    public const string ScopePlanContractVersion = "1";
    public const string ScopePlanPolicyVersion = "api-key-scope-v1";

    private const string ScopePlanPreconditionField = "scope_plan_digest";
    private const string UserServicesFailurePrefix = "nyxid_user_services";
    private const string UserServiceKeysFailurePrefix = "nyxid_user_service_keys";
    private const string UserServiceAuthorizationFailurePrefix =
        "nyxid_user_service_authorization";
    private const string AgentApiKeyFailurePrefix = "nyxid_agent_api_key";
    private const string ScopePlanFailurePrefix = "nyxid_scope_plan";

    private static readonly HashSet<string> PublishedErrorCodes = new(StringComparer.Ordinal)
    {
        "authentication_failed",
        "unauthorized",
        "token_expired",
        "forbidden",
        "bad_request",
        "validation_error",
        "not_found",
        "conflict",
        "rate_limited",
        "internal_error",
        "api_key_scope_plan_not_found",
        "api_key_scope_plan_denied",
        "api_key_scope_plan_owner_unsupported",
        "api_key_scope_plan_route_unresolved",
        "api_key_scope_plan_stale",
    };

    private static readonly HashSet<string> SecretBearingReadFieldNames = new(StringComparer.Ordinal)
    {
        "apikey",
        "fullkey",
        "keyhash",
        "credential",
        "credentials",
        "accesstoken",
        "refreshtoken",
        "authorization",
        "cookie",
        "cookies",
        "secret",
        "secrets",
        "clientsecret",
        "password",
        "token",
        "passphrase",
        "usercode",
        "devicecode",
        "rawbody",
        "rawupstreambody",
    };

    private static readonly Regex Rfc3339Pattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ScopePlanDigestPattern = new(
        @"^sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex SecretBearingReadValuePattern = new(
        @"(?:Bearer\s+\S+|nyxid_(?:ag_)?[A-Za-z0-9_-]{16,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static NyxIdApiAccessResult<NyxIdUserServices> ParseUserServices(string response) =>
        Parse(response, UserServicesFailurePrefix, ParseUserServicesDocument);

    public static NyxIdApiAccessResult<NyxIdUserServices> ParseUserServiceRoutes(
        string response) =>
        Parse(
            response,
            UserServicesFailurePrefix,
            static root => ParseUserServicesDocument(root, includeCodeExecutionRouteFields: true));

    public static NyxIdApiAccessResult<NyxIdUserServiceKeys> ParseUserServiceKeys(string response) =>
        Parse(response, UserServiceKeysFailurePrefix, ParseUserServiceKeysDocument);

    public static NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>
        ParseUserServiceAuthorization(string response) =>
        Parse(
            response,
            UserServiceAuthorizationFailurePrefix,
            ParseUserServiceAuthorizationDocument);

    public static NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence> ParseAgentApiKey(
        string response) =>
        Parse(response, AgentApiKeyFailurePrefix, ParseAgentApiKeyDocument);

    public static NyxIdApiAccessResult<NyxIdApiKeyScopePlan> ParseScopePlan(string response) =>
        Parse(response, ScopePlanFailurePrefix, ParseScopePlanDocument);

    private static NyxIdApiAccessResult<T> Parse<T>(
        string response,
        string failurePrefix,
        Func<JsonElement, T> parseDocument)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(response))
            return Malformed<T>(failurePrefix);
        if (TryParseProviderFailure(response, failurePrefix, out var providerFailure))
            return NyxIdApiAccessResult<T>.Failed(providerFailure);

        try
        {
            using var document = JsonDocument.Parse(response);
            RequireKind(document.RootElement, JsonValueKind.Object);
            return NyxIdApiAccessResult<T>.Success(parseDocument(document.RootElement));
        }
        catch (JsonException)
        {
            return Malformed<T>(failurePrefix);
        }
        catch (NyxIdContractException)
        {
            return Malformed<T>(failurePrefix);
        }
    }

    private static NyxIdUserServices ParseUserServicesDocument(JsonElement root) =>
        ParseUserServicesDocument(root, includeCodeExecutionRouteFields: false);

    private static NyxIdUserServices ParseUserServicesDocument(
        JsonElement root,
        bool includeCodeExecutionRouteFields)
    {
        var servicesElement = RequireProperty(root, "services", JsonValueKind.Array);
        var services = new List<NyxIdUserService>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var serviceElement in servicesElement.EnumerateArray())
        {
            RequireKind(serviceElement, JsonValueKind.Object);
            var id = RequireNormalizedString(serviceElement, "id", "_id", "service_id");
            if (!serviceIds.Add(id))
                throw new NyxIdContractException();

            services.Add(new NyxIdUserService(
                id,
                RequireNormalizedString(serviceElement, "slug"),
                ReadOptionalString(serviceElement, "label"),
                ReadOptionalString(serviceElement, "catalog_service_name"),
                RequireBoolean(serviceElement, "is_active"),
                ParseCredentialSource(RequireProperty(
                    serviceElement,
                    "credential_source",
                    JsonValueKind.Object)),
                ReadOptionalString(serviceElement, "default_model", "defaultModel"),
                includeCodeExecutionRouteFields
                    ? ReadOptionalNormalizedString(serviceElement, "catalog_service_id")
                    : null,
                includeCodeExecutionRouteFields
                    ? ReadOptionalBoolean(serviceElement, "forward_access_token")
                    : null,
                includeCodeExecutionRouteFields
                    ? ReadOptionalBoolean(serviceElement, "inject_delegation_token")
                    : null,
                includeCodeExecutionRouteFields
                    ? ReadOptionalNormalizedString(serviceElement, "delegation_token_scope")
                    : null));
        }

        return new NyxIdUserServices(services);
    }

    private static NyxIdUserServiceKeys ParseUserServiceKeysDocument(JsonElement root)
    {
        var keysElement = RequireProperty(root, "keys", JsonValueKind.Array);
        var services = new List<NyxIdUserServiceKey>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var serviceElement in keysElement.EnumerateArray())
        {
            RequireKind(serviceElement, JsonValueKind.Object);
            var id = RequireNormalizedString(serviceElement, "id");
            if (!serviceIds.Add(id))
                throw new NyxIdContractException();

            var nodeId = ReadOptionalNormalizedString(serviceElement, "node_id");
            services.Add(new NyxIdUserServiceKey(
                id,
                RequireNormalizedString(serviceElement, "slug"),
                ReadOptionalString(serviceElement, "label"),
                ReadOptionalString(serviceElement, "catalog_service_name"),
                RequireBoolean(serviceElement, "is_active"),
                ParseCredentialStatus(RequireNormalizedString(serviceElement, "status")),
                nodeId,
                ParseNodeStatus(serviceElement, nodeId),
                ParseCredentialSource(RequireProperty(
                    serviceElement,
                    "credential_source",
                    JsonValueKind.Object)),
                ReadOptionalNormalizedString(serviceElement, "catalog_service_id"),
                ReadOptionalNormalizedString(serviceElement, "catalog_service_slug"),
                RequireBoolean(serviceElement, "connected"),
                ReadOptionalBoolean(serviceElement, "auto_connected")));
        }

        return new NyxIdUserServiceKeys(services);
    }

    private static NyxIdUserServiceAuthorizationEvidence
        ParseUserServiceAuthorizationDocument(JsonElement root)
    {
        RejectSecretBearingRead(root);
        return new NyxIdUserServiceAuthorizationEvidence(
            RequireNormalizedString(root, "id"),
            ReadOptionalNormalizedString(root, "api_key_id"),
            RequireBoolean(root, "is_active"),
            ParseCredentialStatus(RequireNormalizedString(root, "status")),
            ParseRequiredOAuthConnectionStatus(root),
            ReadRequiredNullableNormalizedStringArray(root, "granted_scopes"),
            ReadRequiredNullableTimestamp(root, "last_authorized_at"));
    }

    private static NyxIdAgentApiKeyEvidence ParseAgentApiKeyDocument(JsonElement root)
    {
        RejectSecretBearingRead(root);
        var createdAt = ParseRfc3339(RequireNormalizedString(root, "created_at"));
        var versionEvidence = ParseOptionalVersionEvidence(root);
        if (versionEvidence is not null && versionEvidence.UpdatedAtUtc < createdAt)
            throw new NyxIdContractException();

        return new NyxIdAgentApiKeyEvidence(
            RequireNormalizedString(root, "id"),
            RequireNormalizedString(root, "name"),
            ParseSpaceSeparatedValues(RequireNormalizedString(root, "scopes")),
            ReadOptionalNormalizedString(root, "platform"),
            RequireBoolean(root, "is_active"),
            ReadRequiredNormalizedStringArray(root, "allowed_service_ids"),
            RequireBoolean(root, "allow_all_services"),
            ReadRequiredNormalizedStringArray(root, "allowed_node_ids"),
            RequireBoolean(root, "allow_all_nodes"),
            createdAt,
            versionEvidence);
    }

    private static NyxIdApiKeyVersionEvidence? ParseOptionalVersionEvidence(JsonElement root)
    {
        var hasPrevious = root.TryGetProperty("rotation_predecessor_id", out _);
        var hasVersion = root.TryGetProperty("state_version", out _);
        var hasUpdatedAt = root.TryGetProperty("updated_at", out _);
        if (!hasPrevious && !hasVersion && !hasUpdatedAt)
            return null;
        if (!hasPrevious || !hasVersion || !hasUpdatedAt)
            throw new NyxIdContractException();

        var versionElement = RequireProperty(root, "state_version", JsonValueKind.Number);
        if (!versionElement.TryGetInt64(out var stateVersion) || stateVersion <= 0)
            throw new NyxIdContractException();

        var predecessorElement = RequireProperty(root, "rotation_predecessor_id");
        var predecessorId = predecessorElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when IsNormalizedValue(predecessorElement.GetString()) =>
                predecessorElement.GetString(),
            _ => throw new NyxIdContractException(),
        };

        return new NyxIdApiKeyVersionEvidence(
            predecessorId,
            stateVersion,
            ParseRfc3339(RequireNormalizedString(root, "updated_at")));
    }

    private static IReadOnlyList<string> ParseSpaceSeparatedValues(string value)
    {
        var values = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0 ||
            values.Any(static item => !IsNormalizedValue(item)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new NyxIdContractException();
        }

        return values;
    }

    private static IReadOnlyList<string> ReadRequiredNormalizedStringArray(
        JsonElement root,
        string propertyName)
    {
        var property = RequireProperty(root, propertyName, JsonValueKind.Array);
        return ReadNormalizedStringArray(property);
    }

    private static IReadOnlyList<string>? ReadRequiredNullableNormalizedStringArray(
        JsonElement root,
        string propertyName)
    {
        var property = RequireProperty(root, propertyName);
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        RequireKind(property, JsonValueKind.Array);
        return ReadNormalizedStringArray(property);
    }

    private static IReadOnlyList<string> ReadNormalizedStringArray(JsonElement array)
    {
        var values = array.EnumerateArray()
            .Select(static item =>
            {
                RequireKind(item, JsonValueKind.String);
                var value = item.GetString();
                return IsNormalizedValue(value)
                    ? value!
                    : throw new NyxIdContractException();
            })
            .ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new NyxIdContractException();
        return values;
    }

    private static DateTimeOffset? ReadRequiredNullableTimestamp(
        JsonElement root,
        string propertyName)
    {
        var property = RequireProperty(root, propertyName);
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        RequireKind(property, JsonValueKind.String);
        var value = property.GetString();
        if (!IsNormalizedValue(value))
            throw new NyxIdContractException();
        return ParseRfc3339(value!);
    }

    private static bool IsNormalizedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static void RejectSecretBearingRead(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject())
                {
                    if (SecretBearingReadFieldNames.Contains(
                            NormalizeReadFieldName(property.Name)))
                        throw new NyxIdContractException();
                    RejectSecretBearingRead(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in root.EnumerateArray())
                    RejectSecretBearingRead(item);
                break;
            case JsonValueKind.String:
                if (SecretBearingReadValuePattern.IsMatch(root.GetString() ?? string.Empty))
                    throw new NyxIdContractException();
                break;
        }
    }

    private static string NormalizeReadFieldName(string value) =>
        string.Concat(value
            .Where(static character => char.IsAsciiLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character)));

    private static NyxIdUserServiceCredentialStatus ParseCredentialStatus(string value) => value switch
    {
        "active" => NyxIdUserServiceCredentialStatus.Active,
        "expired" => NyxIdUserServiceCredentialStatus.Expired,
        "revoked" => NyxIdUserServiceCredentialStatus.Revoked,
        "failed" => NyxIdUserServiceCredentialStatus.Failed,
        "refresh_failed" => NyxIdUserServiceCredentialStatus.RefreshFailed,
        "pending_auth" => NyxIdUserServiceCredentialStatus.PendingAuthorization,
        _ => throw new NyxIdContractException(),
    };

    private static NyxIdOAuthConnectionStatus ParseRequiredOAuthConnectionStatus(
        JsonElement root)
    {
        var property = RequireProperty(root, "connection_status");
        if (property.ValueKind == JsonValueKind.Null)
            return NyxIdOAuthConnectionStatus.Unspecified;
        RequireKind(property, JsonValueKind.String);
        return property.GetString() switch
        {
            "active" => NyxIdOAuthConnectionStatus.Active,
            "expired" => NyxIdOAuthConnectionStatus.Expired,
            _ => throw new NyxIdContractException(),
        };
    }

    private static NyxIdUserServiceNodeStatus ParseNodeStatus(JsonElement service, string? nodeId)
    {
        if (nodeId is null)
        {
            if (service.TryGetProperty("node_status", out _))
                throw new NyxIdContractException();
            return NyxIdUserServiceNodeStatus.NotBound;
        }

        return RequireNormalizedString(service, "node_status") switch
        {
            "online" => NyxIdUserServiceNodeStatus.Online,
            "offline" => NyxIdUserServiceNodeStatus.Offline,
            "draining" => NyxIdUserServiceNodeStatus.Draining,
            "unknown" => NyxIdUserServiceNodeStatus.Unknown,
            "inaccessible" => NyxIdUserServiceNodeStatus.Inaccessible,
            _ => throw new NyxIdContractException(),
        };
    }

    private static NyxIdUserServiceCredentialSource ParseCredentialSource(JsonElement source)
    {
        return RequireNormalizedString(source, "type") switch
        {
            "personal" => new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Personal),
            "org" => new NyxIdUserServiceCredentialSource(
                NyxIdUserServiceCredentialSourceKind.Organization,
                RequireNormalizedString(source, "org_id"),
                RequireNormalizedString(source, "org_name"),
                ReadOptionalString(source, "avatar_url"),
                ParseOrganizationRole(RequireNormalizedString(source, "role")),
                RequireBoolean(source, "allowed")),
            _ => throw new NyxIdContractException(),
        };
    }

    private static NyxIdOrganizationRole ParseOrganizationRole(string value) => value switch
    {
        "admin" => NyxIdOrganizationRole.Admin,
        "member" => NyxIdOrganizationRole.Member,
        "viewer" => NyxIdOrganizationRole.Viewer,
        _ => throw new NyxIdContractException(),
    };

    private static NyxIdApiKeyScopePlan ParseScopePlanDocument(JsonElement root)
    {
        var authority = RequireExactString(root, "authority", ScopePlanAuthority);
        var contractVersion = RequireExactString(root, "contract_version", ScopePlanContractVersion);
        var policyVersion = RequireExactString(root, "policy_version", ScopePlanPolicyVersion);
        var actor = ParsePrincipal(RequireProperty(root, "authenticated_actor", JsonValueKind.Object));
        if (actor.Kind != NyxIdScopePlanPrincipalKind.Personal)
            throw new NyxIdContractException();

        var intendedOwner = ParsePrincipal(RequireProperty(
            root,
            "intended_key_owner",
            JsonValueKind.Object));
        if (intendedOwner.Kind == NyxIdScopePlanPrincipalKind.Personal &&
            !string.Equals(intendedOwner.Id, actor.Id, StringComparison.Ordinal))
        {
            throw new NyxIdContractException();
        }

        var services = ParseScopePlanServices(RequireProperty(root, "services", JsonValueKind.Array));
        var allowedServiceIds = ParseCanonicalIds(
            RequireProperty(root, "allowed_service_ids", JsonValueKind.Array),
            allowEmpty: true);
        var allowedNodeIds = ParseCanonicalIds(
            RequireProperty(root, "allowed_node_ids", JsonValueKind.Array),
            allowEmpty: true);
        if (!services.Select(static service => service.UserServiceId)
                .SequenceEqual(allowedServiceIds, StringComparer.Ordinal))
        {
            throw new NyxIdContractException();
        }

        var serviceNodeIds = services
            .SelectMany(static service => service.NodeGrant.NodeIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!serviceNodeIds.SequenceEqual(allowedNodeIds, StringComparer.Ordinal))
            throw new NyxIdContractException();

        var evaluatedAt = ParseRfc3339(RequireNormalizedString(root, "evaluated_at"));
        var digest = RequireNormalizedString(root, "normalized_grant_digest");
        if (!ScopePlanDigestPattern.IsMatch(digest))
            throw new NyxIdContractException();

        var freshness = ParseFreshness(RequireProperty(root, "freshness", JsonValueKind.Object));
        var completeness = ParseCompleteness(RequireProperty(
            root,
            "completeness",
            JsonValueKind.Object));

        return new NyxIdApiKeyScopePlan(
            authority,
            contractVersion,
            policyVersion,
            actor,
            intendedOwner,
            services,
            allowedServiceIds,
            allowedNodeIds,
            evaluatedAt,
            digest,
            freshness,
            completeness);
    }

    private static IReadOnlyList<NyxIdScopePlanServiceGrant> ParseScopePlanServices(
        JsonElement servicesElement)
    {
        var services = new List<NyxIdScopePlanServiceGrant>();
        string? previousServiceId = null;
        foreach (var serviceElement in servicesElement.EnumerateArray())
        {
            RequireKind(serviceElement, JsonValueKind.Object);
            var serviceId = RequireNormalizedString(serviceElement, "user_service_id");
            if (previousServiceId is not null &&
                StringComparer.Ordinal.Compare(previousServiceId, serviceId) >= 0)
            {
                throw new NyxIdContractException();
            }
            previousServiceId = serviceId;

            services.Add(new NyxIdScopePlanServiceGrant(
                serviceId,
                ParsePrincipal(RequireProperty(
                    serviceElement,
                    "resource_owner",
                    JsonValueKind.Object)),
                ParseNodeGrant(RequireProperty(
                    serviceElement,
                    "node_grant",
                    JsonValueKind.Object))));
        }

        return services;
    }

    private static NyxIdScopePlanPrincipal ParsePrincipal(JsonElement principal)
    {
        var id = RequireNormalizedString(principal, "id");
        var kind = RequireNormalizedString(principal, "type") switch
        {
            "personal" => NyxIdScopePlanPrincipalKind.Personal,
            "organization" => NyxIdScopePlanPrincipalKind.Organization,
            _ => throw new NyxIdContractException(),
        };
        return new NyxIdScopePlanPrincipal(id, kind);
    }

    private static NyxIdScopePlanNodeGrant ParseNodeGrant(JsonElement nodeGrant)
    {
        var kind = RequireNormalizedString(nodeGrant, "type");
        if (string.Equals(kind, "not_required", StringComparison.Ordinal))
        {
            if (nodeGrant.TryGetProperty("node_ids", out _))
                throw new NyxIdContractException();
            return new NyxIdScopePlanNodeGrant(NyxIdScopePlanNodeGrantKind.NotRequired, []);
        }

        if (!string.Equals(kind, "required", StringComparison.Ordinal))
            throw new NyxIdContractException();
        var nodeIds = ParseCanonicalIds(
            RequireProperty(nodeGrant, "node_ids", JsonValueKind.Array),
            allowEmpty: false);
        return new NyxIdScopePlanNodeGrant(NyxIdScopePlanNodeGrantKind.Required, nodeIds);
    }

    private static NyxIdScopePlanFreshness ParseFreshness(JsonElement freshness)
    {
        RequireExactString(freshness, "mode", "mutation_revalidated_snapshot");
        RequireExactString(freshness, "precondition_field", ScopePlanPreconditionField);
        RequireExactString(freshness, "post_creation_drift", "fail_closed");
        return new NyxIdScopePlanFreshness(
            NyxIdScopePlanFreshnessMode.MutationRevalidatedSnapshot,
            ScopePlanPreconditionField,
            NyxIdScopePlanPostCreationDrift.FailClosed);
    }

    private static NyxIdScopePlanCompleteness ParseCompleteness(JsonElement completeness)
    {
        if (!RequireBoolean(completeness, "list_complete") ||
            !RequireBoolean(completeness, "no_duplicates") ||
            !RequireBoolean(completeness, "transient_node_state_excluded"))
        {
            throw new NyxIdContractException();
        }
        RequireExactString(completeness, "route_candidate_basis", "active_configured_routes");
        return new NyxIdScopePlanCompleteness(
            true,
            true,
            NyxIdScopePlanRouteCandidateBasis.ActiveConfiguredRoutes,
            true);
    }

    private static IReadOnlyList<string> ParseCanonicalIds(
        JsonElement array,
        bool allowEmpty)
    {
        var ids = new List<string>();
        string? previous = null;
        foreach (var item in array.EnumerateArray())
        {
            RequireKind(item, JsonValueKind.String);
            var id = item.GetString();
            if (string.IsNullOrWhiteSpace(id) ||
                !string.Equals(id, id.Trim(), StringComparison.Ordinal) ||
                previous is not null && StringComparer.Ordinal.Compare(previous, id) >= 0)
            {
                throw new NyxIdContractException();
            }
            previous = id;
            ids.Add(id);
        }

        if (!allowEmpty && ids.Count == 0)
            throw new NyxIdContractException();
        return ids;
    }

    private static DateTimeOffset ParseRfc3339(string value)
    {
        if (!Rfc3339Pattern.IsMatch(value) ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timestamp))
        {
            throw new NyxIdContractException();
        }
        return timestamp.ToUniversalTime();
    }

    private static bool TryParseProviderFailure(
        string response,
        string failurePrefix,
        out NyxIdApiAccessFailure failure)
    {
        failure = default!;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("error", out var errorMarker) ||
                errorMarker.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (!root.TryGetProperty("status", out var statusElement) ||
                statusElement.ValueKind != JsonValueKind.Number ||
                !statusElement.TryGetInt32(out var status) ||
                status < 0)
            {
                failure = MalformedFailure(failurePrefix);
                return true;
            }

            var providerErrorCode = 0;
            string? providerErrorKey = null;
            if (root.TryGetProperty("body", out var bodyElement) &&
                bodyElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(bodyElement.GetString()))
            {
                TryReadProviderError(
                    bodyElement.GetString()!,
                    out providerErrorKey,
                    out providerErrorCode);
            }

            failure = new NyxIdApiAccessFailure(
                ClassifyFailure(status),
                providerErrorKey is not null && PublishedErrorCodes.Contains(providerErrorKey)
                    ? providerErrorKey
                    : failurePrefix + "_failed",
                status,
                providerErrorCode);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryReadProviderError(
        string body,
        out string? errorKey,
        out int errorCode)
    {
        errorKey = null;
        errorCode = 0;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (root.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                errorKey = errorElement.GetString();
            }
            if (root.TryGetProperty("error_code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.Number &&
                codeElement.TryGetInt32(out var parsedCode) &&
                parsedCode >= 0)
            {
                errorCode = parsedCode;
            }
        }
        catch (JsonException)
        {
            // Raw provider text is deliberately discarded at the adapter boundary.
        }
    }

    private static NyxIdApiAccessFailureKind ClassifyFailure(int status) => status switch
    {
        0 => NyxIdApiAccessFailureKind.Transport,
        401 => NyxIdApiAccessFailureKind.Unauthorized,
        403 => NyxIdApiAccessFailureKind.Forbidden,
        404 => NyxIdApiAccessFailureKind.NotFound,
        409 => NyxIdApiAccessFailureKind.Conflict,
        429 => NyxIdApiAccessFailureKind.RateLimited,
        >= 500 => NyxIdApiAccessFailureKind.Transient,
        _ => NyxIdApiAccessFailureKind.Provider,
    };

    private static string RequireExactString(
        JsonElement root,
        string propertyName,
        string expected)
    {
        var value = RequireNormalizedString(root, propertyName);
        return string.Equals(value, expected, StringComparison.Ordinal)
            ? value
            : throw new NyxIdContractException();
    }

    private static string RequireNormalizedString(
        JsonElement root,
        string propertyName,
        params string[] alternativePropertyNames)
    {
        if (TryReadNormalizedString(root, propertyName, out var value))
            return value;
        foreach (var alternativePropertyName in alternativePropertyNames)
        {
            if (TryReadNormalizedString(root, alternativePropertyName, out value))
                return value;
        }

        throw new NyxIdContractException();
    }

    private static bool TryReadNormalizedString(
        JsonElement root,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        RequireKind(root, JsonValueKind.Object);
        if (!root.TryGetProperty(propertyName, out var property))
            return false;
        RequireKind(property, JsonValueKind.String);
        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) ||
            !string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal))
        {
            throw new NyxIdContractException();
        }
        value = candidate;
        return true;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName, params string[] alternativePropertyNames)
    {
        if (TryReadOptionalString(root, propertyName, out var value))
            return value;
        foreach (var alternativePropertyName in alternativePropertyNames)
        {
            if (TryReadOptionalString(root, alternativePropertyName, out value))
                return value;
        }

        return null;
    }

    private static bool TryReadOptionalString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Null)
            return true;
        RequireKind(property, JsonValueKind.String);
        value = property.GetString();
        return true;
    }

    private static string? ReadOptionalNormalizedString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        RequireKind(property, JsonValueKind.String);
        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new NyxIdContractException();
        }
        return value;
    }

    private static bool RequireBoolean(JsonElement root, string propertyName)
    {
        var property = RequireProperty(root, propertyName);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new NyxIdContractException(),
        };
    }

    private static bool? ReadOptionalBoolean(JsonElement root, string propertyName)
    {
        RequireKind(root, JsonValueKind.Object);
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new NyxIdContractException(),
        };
    }

    private static JsonElement RequireProperty(
        JsonElement root,
        string propertyName,
        JsonValueKind? expectedKind = null)
    {
        RequireKind(root, JsonValueKind.Object);
        if (!root.TryGetProperty(propertyName, out var property) ||
            expectedKind.HasValue && property.ValueKind != expectedKind.Value)
        {
            throw new NyxIdContractException();
        }
        return property;
    }

    private static void RequireKind(JsonElement element, JsonValueKind expectedKind)
    {
        if (element.ValueKind != expectedKind)
            throw new NyxIdContractException();
    }

    private static NyxIdApiAccessResult<T> Malformed<T>(string failurePrefix)
        where T : class =>
        NyxIdApiAccessResult<T>.Failed(MalformedFailure(failurePrefix));

    private static NyxIdApiAccessFailure MalformedFailure(string failurePrefix) =>
        new(
            NyxIdApiAccessFailureKind.MalformedResponse,
            failurePrefix + "_response_malformed");

    private sealed class NyxIdContractException : Exception
    {
    }
}
