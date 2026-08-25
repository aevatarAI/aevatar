namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Reads the current caller's permission-scoped NyxID UserService inventory.
/// The caller supplies its own bearer token; credentials never cross this boundary.
/// </summary>
public interface INyxIdUserServiceInventoryPort
{
    Task<IReadOnlyList<NyxIdUserServiceInventoryItem>> ListAsync(
        string bearerToken,
        CancellationToken ct = default);
}

public enum NyxIdInventoryCredentialSourceKind
{
    Unspecified = 0,
    Personal = 1,
    Organization = 2,
}

public enum NyxIdInventoryCredentialStatus
{
    Unspecified = 0,
    Active = 1,
    Expired = 2,
    Revoked = 3,
    Failed = 4,
    RefreshFailed = 5,
    PendingAuthorization = 6,
}

public enum NyxIdInventoryNodeStatus
{
    Unspecified = 0,
    NotBound = 1,
    Online = 2,
    Offline = 3,
    Draining = 4,
    Unknown = 5,
    Inaccessible = 6,
}

public sealed record NyxIdUserServiceInventoryItem(
    string UserServiceId,
    string InstanceSlug,
    string? CatalogServiceSlug,
    string? Label,
    bool IsActive,
    NyxIdInventoryCredentialSourceKind CredentialSource,
    bool Allowed,
    NyxIdInventoryCredentialStatus CredentialStatus,
    string? NodeId,
    NyxIdInventoryNodeStatus NodeStatus,
    bool Connected);

public enum NyxIdUserServiceInventoryFailureKind
{
    AuthenticationRejected = 1,
    Forbidden = 2,
    RateLimited = 3,
    ResponseInvalid = 4,
    Unavailable = 5,
}

public sealed class NyxIdUserServiceInventoryException : Exception
{
    public NyxIdUserServiceInventoryException(
        NyxIdUserServiceInventoryFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public NyxIdUserServiceInventoryFailureKind Kind { get; }
}
