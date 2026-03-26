using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;

namespace Aevatar.AppPlatform.Application.Services;

public sealed class OperationQueryApplicationService : IOperationQueryPort
{
    private readonly IAppOperationStore _operationStore;

    public OperationQueryApplicationService(IAppOperationStore operationStore)
    {
        _operationStore = operationStore ?? throw new ArgumentNullException(nameof(operationStore));
    }

    public Task<AppOperationSnapshot?> GetAsync(string operationId, CancellationToken ct = default) =>
        _operationStore.GetAsync(operationId, ct);

    public Task<AppOperationResult?> GetResultAsync(string operationId, CancellationToken ct = default) =>
        _operationStore.GetResultAsync(operationId, ct);

    public Task<IReadOnlyList<AppOperationEvent>> ListEventsAsync(string operationId, CancellationToken ct = default) =>
        _operationStore.ListEventsAsync(operationId, ct);

    public IAsyncEnumerable<AppOperationEvent> WatchAsync(
        string operationId,
        ulong afterSequence = 0,
        CancellationToken ct = default) =>
        _operationStore.WatchAsync(operationId, afterSequence, ct);
}
