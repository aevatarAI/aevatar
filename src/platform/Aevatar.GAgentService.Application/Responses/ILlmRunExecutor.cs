using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;

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

    Task ExecuteAsync(
        LlmRunExecutorRequest request,
        CancellationToken ct = default);
}

public interface ILlmRunExecutorClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken ct = default);
}

public sealed class SystemLlmRunExecutorClock : ILlmRunExecutorClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default) =>
        Task.Delay(delay, ct);
}
