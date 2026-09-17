using System.Net;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface INyxIdModelDiscoveryPort
{
    Task<NyxIdDiscoveredModels> GetScopeModelsAsync(
        string bearerToken,
        string serviceSlug,
        string userServiceId,
        CancellationToken ct);

    Task<NyxIdDiscoveredModels> GetPlatformModelsAsync(
        string bearerToken,
        string catalogServiceId,
        CancellationToken ct);
}

public sealed record NyxIdDiscoveredModels(
    IReadOnlyList<string> ModelIds,
    string? DefaultModelId);

public enum NyxIdModelDiscoveryFailureKind
{
    EndpointNotFound = 1,
    ResponseInvalid = 2,
    ResponseTooLarge = 3,
    UpstreamRejected = 4,
    Unavailable = 5,
}

public sealed class NyxIdModelDiscoveryException : Exception
{
    public NyxIdModelDiscoveryException(
        NyxIdModelDiscoveryFailureKind kind,
        HttpStatusCode? statusCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public NyxIdModelDiscoveryFailureKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }
}
