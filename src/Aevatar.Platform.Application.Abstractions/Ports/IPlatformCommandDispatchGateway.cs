using Aevatar.Platform.Application.Abstractions.Commands;

namespace Aevatar.Platform.Application.Abstractions.Ports;

public sealed record PlatformCommandDispatchRequest(
    string Method,
    Uri TargetEndpoint,
    string PayloadJson,
    string ContentType);

public sealed record PlatformCommandDispatchResult(
    bool Succeeded,
    int? ResponseStatusCode,
    string ResponseContentType,
    string ResponseBody,
    string Error);

public interface IPlatformCommandDispatchGateway
{
    Task<PlatformCommandDispatchResult> DispatchAsync(
        PlatformCommandDispatchRequest request,
        CancellationToken ct = default);
}
