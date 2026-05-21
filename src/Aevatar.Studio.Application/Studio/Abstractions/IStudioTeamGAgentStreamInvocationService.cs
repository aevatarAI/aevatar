using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Presentation.AGUI;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IStudioTeamGAgentStreamInvocationService
{
    Task<StaticGAgentStreamInvocationResult> InvokeAsync(
        StudioTeamGAgentStreamInvocationRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}

public sealed record StudioTeamGAgentStreamInvocationRequest(
    string ScopeId,
    string TeamId,
    string EndpointId,
    StaticGAgentStreamInvocationInput Input);
