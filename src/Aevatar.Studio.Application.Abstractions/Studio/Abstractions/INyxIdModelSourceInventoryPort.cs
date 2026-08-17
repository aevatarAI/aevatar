using System.Net;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface INyxIdModelSourceInventoryPort
{
    Task<NyxIdPlatformModelSourceInventory> GetPlatformCatalogServicesAsync(
        string bearerToken,
        CancellationToken ct);

    Task<NyxIdScopeModelSourceInventory> GetScopeModelSourcesAsync(
        string bearerToken,
        CancellationToken ct);
}

public enum NyxIdModelSourceInventoryFailureKind
{
    AuthenticationRejected = 1,
    Forbidden = 2,
    Unavailable = 3,
}

public sealed class NyxIdModelSourceInventoryException : Exception
{
    public NyxIdModelSourceInventoryException(
        NyxIdModelSourceInventoryFailureKind kind,
        HttpStatusCode? statusCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public NyxIdModelSourceInventoryFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }
}

public sealed record NyxIdPlatformModelSourceInventory(
    IReadOnlyList<NyxIdPlatformModelSourceService> Services);

public sealed record NyxIdPlatformModelSourceService(
    string CatalogServiceId,
    string Slug,
    string DisplayName,
    bool IsActive,
    NyxIdModelSourceServiceType ServiceType,
    NyxIdCatalogServiceVisibility Visibility,
    NyxIdCatalogServiceAuthMethod AuthMethod,
    NyxIdCatalogServiceCategory ServiceCategory,
    bool RequiresUserCredential)
{
    public NyxIdPlatformModelSourceAvailabilityReason AvailabilityReason =>
        !NyxIdServiceSlugPolicy.IsCanonical(Slug)
            ? NyxIdPlatformModelSourceAvailabilityReason.InvalidServiceSlug
            : Visibility.Kind != NyxIdCatalogServiceVisibilityKind.Public
            ? NyxIdPlatformModelSourceAvailabilityReason.NotPublic
            : !IsActive
                ? NyxIdPlatformModelSourceAvailabilityReason.ServiceInactive
                : ServiceType.Kind != NyxIdModelSourceServiceTypeKind.HTTP
                    ? NyxIdPlatformModelSourceAvailabilityReason.UnsupportedServiceType
                    : ServiceCategory.Kind == NyxIdCatalogServiceCategoryKind.Provider
                        ? NyxIdPlatformModelSourceAvailabilityReason.ProviderService
                        : ServiceCategory.Kind is not (
                            NyxIdCatalogServiceCategoryKind.Connection or
                            NyxIdCatalogServiceCategoryKind.Internal)
                            ? NyxIdPlatformModelSourceAvailabilityReason.UnsupportedServiceCategory
                            : RequiresUserCredential
                                ? NyxIdPlatformModelSourceAvailabilityReason.UserCredentialRequired
                                : AuthMethod.Kind == NyxIdCatalogServiceAuthMethodKind.TokenExchange
                                    ? NyxIdPlatformModelSourceAvailabilityReason.TokenExchangeUnsupported
                                    : AuthMethod.Kind is NyxIdCatalogServiceAuthMethodKind.Unspecified or
                                        NyxIdCatalogServiceAuthMethodKind.Unknown
                                        ? NyxIdPlatformModelSourceAvailabilityReason.UnsupportedAuthMethod
                                        : NyxIdPlatformModelSourceAvailabilityReason.Available;

    public bool IsSelectable => AvailabilityReason == NyxIdPlatformModelSourceAvailabilityReason.Available;
}

public enum NyxIdPlatformModelSourceAvailabilityReason
{
    Available = 0,
    NotPublic = 1,
    ServiceInactive = 2,
    UnsupportedServiceType = 3,
    InvalidServiceSlug = 4,
    ProviderService = 5,
    UnsupportedServiceCategory = 6,
    UserCredentialRequired = 7,
    TokenExchangeUnsupported = 8,
    UnsupportedAuthMethod = 9,
}

public sealed record NyxIdScopeModelSourceInventory(
    IReadOnlyList<NyxIdScopeModelSourceService> Services);

public sealed record NyxIdScopeModelSourceService(
    string UserServiceId,
    string? CatalogServiceId,
    string Slug,
    string? DisplayName,
    string? CatalogServiceDisplayName,
    bool IsActive,
    NyxIdModelSourceServiceType ServiceType,
    NyxIdScopeCredentialSource CredentialSource,
    NyxIdModelSourceCredentialStatus CredentialStatus,
    bool CredentialMissing,
    NyxIdModelSourceConnectionStatus ConnectionStatus,
    string? NodeId,
    NyxIdModelSourceNodeStatus NodeStatus)
{
    public NyxIdModelSourceAvailabilityReason AvailabilityReason =>
        !NyxIdServiceSlugPolicy.IsCanonical(Slug)
            ? NyxIdModelSourceAvailabilityReason.UnsupportedServiceSlug
            : ServiceType.Kind != NyxIdModelSourceServiceTypeKind.HTTP
            ? NyxIdModelSourceAvailabilityReason.UnsupportedServiceType
            : !IsActive
                ? NyxIdModelSourceAvailabilityReason.ServiceInactive
                : CredentialSource is NyxIdOrganizationCredentialSource { Allowed: false }
                    ? NyxIdModelSourceAvailabilityReason.OrganizationAccessDenied
                    : NodeId is not null
                        ? NodeStatus.Kind == NyxIdModelSourceNodeStatusKind.Online
                            ? NyxIdModelSourceAvailabilityReason.Available
                            : NyxIdModelSourceAvailabilityReason.NodeUnavailable
                        : CredentialMissing
                            ? NyxIdModelSourceAvailabilityReason.CredentialMissing
                            : CredentialStatus.Kind != NyxIdModelSourceCredentialStatusKind.Active
                                ? NyxIdModelSourceAvailabilityReason.CredentialInactive
                                : ConnectionStatus.Kind == NyxIdModelSourceConnectionStatusKind.Expired
                                    ? NyxIdModelSourceAvailabilityReason.ConnectionExpired
                                    : ConnectionStatus.Kind == NyxIdModelSourceConnectionStatusKind.Unknown
                                        ? NyxIdModelSourceAvailabilityReason.ConnectionUnavailable
                                        : NyxIdModelSourceAvailabilityReason.Available;

    public bool IsCallable => AvailabilityReason == NyxIdModelSourceAvailabilityReason.Available;
}

public enum NyxIdModelSourceAvailabilityReason
{
    Available = 0,
    UnsupportedServiceType = 1,
    ServiceInactive = 2,
    CredentialMissing = 3,
    CredentialInactive = 4,
    ConnectionExpired = 5,
    OrganizationAccessDenied = 6,
    NodeUnavailable = 7,
    ConnectionUnavailable = 8,
    UnsupportedServiceSlug = 9,
}

public sealed record NyxIdModelSourceServiceType(
    NyxIdModelSourceServiceTypeKind Kind,
    string? WireValue);

public enum NyxIdModelSourceServiceTypeKind
{
    Unspecified = 0,
    Unknown = 1,
    HTTP = 2,
    SSH = 3,
}

public sealed record NyxIdModelSourceCredentialStatus(
    NyxIdModelSourceCredentialStatusKind Kind,
    string WireValue);

public enum NyxIdModelSourceCredentialStatusKind
{
    Unspecified = 0,
    Unknown = 1,
    Active = 2,
    Expired = 3,
    Revoked = 4,
    Failed = 5,
    RefreshFailed = 6,
    PendingAuth = 7,
}

public sealed record NyxIdModelSourceConnectionStatus(
    NyxIdModelSourceConnectionStatusKind Kind,
    string? WireValue);

public enum NyxIdModelSourceConnectionStatusKind
{
    NotApplicable = 0,
    Unknown = 1,
    Active = 2,
    Expired = 3,
}

public sealed record NyxIdModelSourceNodeStatus(
    NyxIdModelSourceNodeStatusKind Kind,
    string? WireValue);

public enum NyxIdModelSourceNodeStatusKind
{
    NotApplicable = 0,
    Unknown = 1,
    Online = 2,
    Offline = 3,
    Draining = 4,
    Inaccessible = 5,
}

public sealed record NyxIdCatalogServiceVisibility(
    NyxIdCatalogServiceVisibilityKind Kind,
    string WireValue);

public enum NyxIdCatalogServiceVisibilityKind
{
    Unspecified = 0,
    Unknown = 1,
    Public = 2,
    Private = 3,
}

public sealed record NyxIdCatalogServiceAuthMethod(
    NyxIdCatalogServiceAuthMethodKind Kind,
    string WireValue);

public enum NyxIdCatalogServiceAuthMethodKind
{
    Unspecified = 0,
    Unknown = 1,
    Header = 2,
    Bearer = 3,
    BotBearer = 4,
    Query = 5,
    Basic = 6,
    Body = 7,
    TokenExchange = 8,
    Path = 9,
    OIDC = 10,
    None = 11,
    AWSSigV4 = 12,
}

public sealed record NyxIdCatalogServiceCategory(
    NyxIdCatalogServiceCategoryKind Kind,
    string WireValue);

public enum NyxIdCatalogServiceCategoryKind
{
    Unspecified = 0,
    Unknown = 1,
    Provider = 2,
    Connection = 3,
    Internal = 4,
}

public abstract record NyxIdScopeCredentialSource;

public sealed record NyxIdPersonalCredentialSource : NyxIdScopeCredentialSource;

public sealed record NyxIdOrganizationCredentialSource(
    string OrganizationId,
    string OrganizationName,
    string? AvatarUrl,
    NyxIdScopeOrganizationRole Role,
    bool Allowed) : NyxIdScopeCredentialSource;

public enum NyxIdScopeOrganizationRole
{
    Unspecified = 0,
    Admin = 1,
    Member = 2,
    Viewer = 3,
}
