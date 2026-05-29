namespace Aevatar.Foundation.Runtime.Maintenance;

public interface IRetiredActorCleanupCoordinatorContinuationPort
{
    Task<IAsyncDisposable> SubscribeAsync(
        Func<RetiredActorCleanupCoordinatorContinuation, Task> handler,
        CancellationToken ct = default);
}
