using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Responses;

public interface ILlmSessionRunObservationService
{
    Task<LlmSessionRunObservedResult> ObserveAsync(
        LlmSessionRunObservationRequest request,
        Func<LlmSessionRunObservedDelta, CancellationToken, ValueTask>? onDelta,
        CancellationToken ct = default);
}

public sealed record LlmSessionRunObservationRequest(
    string ActorId,
    string ResponseId,
    string RunId,
    Func<CancellationToken, Task<DispatchAdmission>> DispatchAsync,
    TimeSpan Timeout);

public sealed record LlmSessionRunObservedDelta(
    string? TextDelta,
    TokenUsage? Usage);

public sealed record LlmSessionRunObservedError(
    LlmSessionRunObservedTerminalKind Kind,
    int StatusCode,
    string Code,
    string Message);

public sealed record LlmSessionRunObservedResult(
    DispatchAdmission? Admission,
    LlmSessionCompletionSnapshot? Completion,
    LlmSessionRunObservedError? Error);

public enum LlmSessionRunObservedTerminalKind
{
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    ObservationUnavailable,
}
