using Aevatar.GAgentService.Abstractions.ScopeGAgents;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IStaticGAgentStreamInvocationPort<TFrame>
{
    Task<StaticGAgentStreamInvocationResult> InvokeAsync(
        StaticGAgentStreamInvocationRequest request,
        Func<TFrame, CancellationToken, ValueTask> emitAsync,
        Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}

public sealed record StaticGAgentStreamInvocationRequest(
    ServiceIdentity Identity,
    string EndpointId,
    StaticGAgentStreamInvocationInput Input);

public sealed record StaticGAgentStreamInvocationInput(
    string Prompt,
    string? PreferredActorId = null,
    string? SessionId = null,
    string? RevisionId = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<GAgentDraftRunInputPart>? InputParts = null,
    ServiceInvocationCaller? Caller = null,
    TimeSpan? Timeout = null);

public sealed record StaticGAgentStreamAcceptedReceipt(
    ServiceInvocationAcceptedReceipt ServiceReceipt,
    GAgentDraftRunAcceptedReceipt GAgentReceipt);

public sealed record StaticGAgentStreamInvocationResult(
    StaticGAgentStreamAcceptedReceipt? Accepted,
    GAgentDraftRunStartError StartError,
    GAgentDraftRunCompletionStatus CompletionStatus,
    bool CompletionObserved)
{
    public bool Succeeded => StartError == GAgentDraftRunStartError.None && Accepted is not null;
}
