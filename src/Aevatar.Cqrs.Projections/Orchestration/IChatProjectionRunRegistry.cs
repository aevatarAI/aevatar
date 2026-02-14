namespace Aevatar.Cqrs.Projections.Orchestration;

/// <summary>
/// Manages projection run registration and actor stream subscription lifecycle.
/// </summary>
public interface IChatProjectionRunRegistry
{
    Task RegisterAsync(ChatProjectionContext context, CancellationToken ct = default);

    Task UnregisterAsync(string actorId, string runId, CancellationToken ct = default);

    Task<bool> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default);
}
