using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;

namespace Aevatar.GAgentService.Application.Responses;

public sealed record LlmRunExecutorRequest(
    string SessionActorId,
    string ResponseId,
    string RunId,
    LlmRunRequested Command,
    string? OriginPlatform);

public interface ILlmRunExecutor
{
    Task<DispatchAdmission> StartAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default);
}
