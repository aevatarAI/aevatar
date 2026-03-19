using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;

public interface IKafkaStrictProviderEnvelopeTransport
{
    Task PublishAsync(
        string streamNamespace,
        string streamId,
        byte[] payload,
        CancellationToken ct = default);

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

public interface IStrictOrleansStreamQueueMapper
{
    int GetPartitionId(string streamNamespace, string streamId);

    QueueId GetQueueId(int partitionId);

    int GetPartitionId(QueueId queueId);

    IReadOnlyList<QueueId> GetAllQueues();
}
