using Aevatar.AGUI.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public interface IStudioTeamGAgentStreamInvocationService
{
    Task<StudioTeamStreamInvocationResult> InvokeAsync(
        StudioTeamStreamInvocationRequest request,
        Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
        Func<StudioTeamStreamInvocationAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
        CancellationToken ct = default);
}

public sealed record StudioTeamStreamInvocationRequest(
    string ScopeId,
    string TeamId,
    string EndpointId,
    StudioTeamStreamInvocationInput Input);

public sealed record StudioTeamStreamInvocationInput(
    string Prompt,
    string? PreferredActorId = null,
    string? SessionId = null,
    string? RevisionId = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<StudioTeamStreamInvocationInputPart>? InputParts = null,
    TimeSpan? Timeout = null);

public sealed record StudioTeamStreamInvocationInputPart(
    string Type,
    string? Text = null,
    string? DataBase64 = null,
    string? MediaType = null,
    string? Uri = null,
    string? Name = null);

public sealed record StudioTeamStreamInvocationAcceptedReceipt(
    string RunId,
    string ThreadId,
    string CorrelationId);

public sealed record StudioTeamStreamInvocationResult(
    StudioTeamStreamInvocationAcceptedReceipt? AcceptedReceipt,
    string StartError,
    string CompletionStatus,
    bool CompletionObserved);
