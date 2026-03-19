using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;

public sealed class StrictQueuePartitionMapper : IStreamQueueMapper, IConsistentRingStreamQueueMapper
{
    private readonly HashRingBasedStreamQueueMapper _sourceMapper;
    private readonly QueueId[] _queues;

    public StrictQueuePartitionMapper(string providerName, int partitionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        _sourceMapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = partitionCount },
            providerName);
        _queues = _sourceMapper.GetAllQueues().ToArray();
    }

    public QueueId GetQueueForStream(StreamId streamId)
    {
        var partitionId = GetPartitionId(
            streamId.GetNamespace() ?? OrleansRuntimeConstants.ActorEventStreamNamespace,
            streamId.GetKeyAsString());
        return GetQueueId(partitionId);
    }

    public IEnumerable<QueueId> GetAllQueues() => _queues;

    public IEnumerable<QueueId> GetQueuesForRange(IRingRange range) =>
        _sourceMapper.GetQueuesForRange(range);

    public int GetPartitionId(string streamNamespace, string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);

        var bytes = Encoding.UTF8.GetBytes(streamNamespace + "\n" + streamId);
        var hash = SHA256.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value % (uint)_queues.Length);
    }

    public QueueId GetQueueId(int partitionId)
    {
        if (partitionId < 0 || partitionId >= _queues.Length)
            throw new ArgumentOutOfRangeException(nameof(partitionId));

        return _queues[partitionId];
    }

    public int GetPartitionId(QueueId queueId)
    {
        for (var i = 0; i < _queues.Length; i++)
        {
            if (_queues[i] == queueId)
                return i;
        }

        throw new InvalidOperationException($"Queue '{queueId}' is not part of the strict queue-partition mapping.");
    }
}
