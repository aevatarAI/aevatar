using Confluent.Kafka;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal interface IKafkaReceiverConsumer : IDisposable
{
    void Assign(TopicPartitionOffset partition);

    ConsumeResult<Ignore, byte[]>? Consume(TimeSpan timeout);

    void Pause(IReadOnlyCollection<TopicPartition> partitions);

    void Resume(IReadOnlyCollection<TopicPartition> partitions);

    void Commit(IReadOnlyCollection<TopicPartitionOffset> offsets);

    void Seek(TopicPartitionOffset offset);

    void Close();
}

internal sealed class ConfluentKafkaReceiverConsumer(IConsumer<Ignore, byte[]> consumer)
    : IKafkaReceiverConsumer
{
    public void Assign(TopicPartitionOffset partition) => consumer.Assign(partition);

    public ConsumeResult<Ignore, byte[]>? Consume(TimeSpan timeout) => consumer.Consume(timeout);

    public void Pause(IReadOnlyCollection<TopicPartition> partitions) => consumer.Pause(partitions);

    public void Resume(IReadOnlyCollection<TopicPartition> partitions) => consumer.Resume(partitions);

    public void Commit(IReadOnlyCollection<TopicPartitionOffset> offsets) => consumer.Commit(offsets);

    public void Seek(TopicPartitionOffset offset) => consumer.Seek(offset);

    public void Close() => consumer.Close();

    public void Dispose() => consumer.Dispose();
}
