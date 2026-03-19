using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

public interface IKafkaPartitionAwareEnvelopeTransport
{
    Task PublishAsync(
        string streamNamespace,
        string streamId,
        byte[] payload,
        CancellationToken ct = default);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task<IAsyncDisposable> SubscribePartitionLifecycleAsync(
        Func<PartitionLifecycleEvent, Task> handler,
        CancellationToken ct = default);

    Task<IAsyncDisposable> SubscribePartitionRecordsAsync(
        int partitionId,
        Func<PartitionEnvelopeRecord, Task> handler,
        CancellationToken ct = default);
}

public sealed record PartitionLifecycleEvent(int PartitionId, PartitionLifecycleEventKind Kind);

public enum PartitionLifecycleEventKind
{
    Assigned = 1,
    Revoked = 2,
}

public sealed class PartitionEnvelopeRecord
{
    public required int PartitionId { get; init; }

    public required string StreamNamespace { get; init; }

    public required string StreamId { get; init; }

    public required byte[] Payload { get; init; }
}

internal sealed class PartitionRecordHandoffAbortedException : Exception
{
    public PartitionRecordHandoffAbortedException(int partitionId, string reason)
        : base($"Partition {partitionId} handoff aborted: {reason}")
    {
        PartitionId = partitionId;
    }

    public int PartitionId { get; }
}

public interface IPartitionAssignmentManager
{
    Task OnAssignedAsync(
        IReadOnlyList<int> partitionIds,
        CancellationToken ct = default);

    Task OnRevokedAsync(
        IReadOnlyList<int> partitionIds,
        CancellationToken ct = default);

    IReadOnlyCollection<int> GetOwnedPartitions();

    Task<IAsyncDisposable> SubscribeOwnedPartitionsChangedAsync(
        Func<IReadOnlyCollection<int>, Task> handler,
        CancellationToken ct = default);
}

public interface IPartitionOwnedReceiverRegistry
{
    Task EnsureStartedAsync(int partitionId, CancellationToken ct = default);

    Task BeginClosingAsync(int partitionId, CancellationToken ct = default);

    Task DrainAndCloseAsync(
        int partitionId,
        TimeSpan timeout,
        CancellationToken ct = default);
}

public interface IPartitionOwnedReceiverFactory
{
    Task<IPartitionOwnedReceiver> CreateAsync(int partitionId, CancellationToken ct = default);
}

public interface IStrictOrleansStreamQueueMapper
{
    int GetPartitionId(string streamNamespace, string streamId);

    QueueId GetQueueId(int partitionId);

    int GetPartitionId(QueueId queueId);

    IReadOnlyList<QueueId> GetAllQueues();
}

public interface ILocalDeliveryAckPort
{
    Task DeliverAsync(
        int partitionId,
        PartitionEnvelopeRecord record,
        CancellationToken ct = default);
}

public interface IPartitionOwnedReceiver : IAsyncDisposable
{
    int PartitionId { get; }

    Task StartAsync(CancellationToken ct = default);

    Task BeginClosingAsync(CancellationToken ct = default);

    Task DrainAsync(TimeSpan timeout, CancellationToken ct = default);
}
