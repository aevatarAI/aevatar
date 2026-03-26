namespace Aevatar.AppPlatform.Abstractions.Ports;

public interface IAppOperationStore
{
    Task<AppOperationSnapshot> AcceptAsync(
        AppOperationSnapshot snapshot,
        CancellationToken ct = default);

    Task<AppOperationSnapshot> AdvanceAsync(
        AppOperationUpdate update,
        CancellationToken ct = default);

    Task<AppOperationSnapshot?> GetAsync(
        string operationId,
        CancellationToken ct = default);

    Task<AppOperationResult?> GetResultAsync(
        string operationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<AppOperationEvent>> ListEventsAsync(
        string operationId,
        CancellationToken ct = default);

    IAsyncEnumerable<AppOperationEvent> WatchAsync(
        string operationId,
        ulong afterSequence = 0,
        CancellationToken ct = default);
}
