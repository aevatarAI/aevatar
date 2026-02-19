using Aevatar.CQRS.Sagas.Abstractions.Actions;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.CQRS.Sagas.Runtime.FileSystem.Timeouts;

namespace Aevatar.CQRS.Sagas.Runtime.FileSystem.Runtime;

internal sealed class FileSystemSagaTimeoutScheduler : ISagaTimeoutScheduler
{
    private readonly FileSystemSagaTimeoutStore _store;

    public FileSystemSagaTimeoutScheduler(FileSystemSagaTimeoutStore store)
    {
        _store = store;
    }

    public Task ScheduleAsync(
        string sagaName,
        string correlationId,
        string actorId,
        SagaScheduleTimeoutAction action,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();

        var dueAt = DateTimeOffset.UtcNow + (action.Delay < TimeSpan.Zero ? TimeSpan.Zero : action.Delay);

        var record = new SagaTimeoutScheduleRecord
        {
            TimeoutId = Guid.NewGuid().ToString("N"),
            SagaName = sagaName,
            CorrelationId = correlationId,
            ActorId = actorId,
            TimeoutName = action.TimeoutName,
            DueAt = dueAt,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = action.Metadata == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(action.Metadata, StringComparer.Ordinal),
        };

        return _store.EnqueueAsync(record, ct);
    }
}
