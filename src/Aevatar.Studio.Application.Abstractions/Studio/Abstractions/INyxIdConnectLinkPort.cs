using System.Net;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Narrow boundary for NyxID hosted service connections. The caller supplies its
/// own bearer token; Aevatar never receives or persists the connected service's
/// credential.
/// </summary>
public interface INyxIdConnectLinkPort
{
    Task<NyxIdConnectLinkCreated> CreateAsync(
        string bearerToken,
        NyxIdConnectLinkCreateRequest request,
        CancellationToken ct = default);

    Task<NyxIdConnectLinkSnapshot> GetAsync(
        string bearerToken,
        string connectLinkId,
        CancellationToken ct = default);
}

public sealed record NyxIdConnectLinkCreateRequest(
    string ServiceSlug,
    string? Label = null,
    string? RequestedBy = null,
    Uri? CallbackUrl = null,
    long? ExpiresInSeconds = null);

/// <summary>
/// Ephemeral hand-off returned only to the authenticated browser. ConnectUrl
/// contains a hosted-link token and must never be logged or persisted.
/// </summary>
public sealed class NyxIdConnectLinkCreated
{
    public NyxIdConnectLinkCreated(string connectLinkId, string connectUrl, DateTimeOffset expiresAt)
    {
        ConnectLinkId = connectLinkId;
        ConnectUrl = connectUrl;
        ExpiresAt = expiresAt;
    }

    public string ConnectLinkId { get; }

    public string ConnectUrl { get; }

    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() =>
        $"{nameof(NyxIdConnectLinkCreated)} {{ ConnectLinkId = {ConnectLinkId}, ConnectUrl = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
}

public enum NyxIdConnectLinkStatus
{
    Pending = 1,
    Completed = 2,
    Expired = 3,
    Cancelled = 4,
}

public sealed record NyxIdConnectLinkSnapshot(
    string ConnectLinkId,
    NyxIdConnectLinkStatus Status,
    string ServiceSlug,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    string? UserServiceId);

public enum NyxIdConnectLinkFailureKind
{
    AuthenticationRejected = 1,
    Forbidden = 2,
    NotFound = 3,
    RateLimited = 4,
    ResponseInvalid = 5,
    ResponseTooLarge = 6,
    Unavailable = 7,
}

public sealed class NyxIdConnectLinkException : Exception
{
    public NyxIdConnectLinkException(
        NyxIdConnectLinkFailureKind kind,
        HttpStatusCode? statusCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public NyxIdConnectLinkFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }
}
