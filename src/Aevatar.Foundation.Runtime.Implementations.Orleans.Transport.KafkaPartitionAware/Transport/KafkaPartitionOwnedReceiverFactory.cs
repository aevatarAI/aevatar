namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionOwnedReceiverFactory : IPartitionOwnedReceiverFactory
{
    private readonly IKafkaPartitionAwareEnvelopeTransport _transport;
    private readonly ILocalDeliveryAckPort _localDeliveryAckPort;

    public KafkaPartitionOwnedReceiverFactory(
        IKafkaPartitionAwareEnvelopeTransport transport,
        ILocalDeliveryAckPort localDeliveryAckPort)
    {
        _transport = transport;
        _localDeliveryAckPort = localDeliveryAckPort;
    }

    public Task<IPartitionOwnedReceiver> CreateAsync(int partitionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IPartitionOwnedReceiver>(
            new KafkaPartitionOwnedReceiver(partitionId, _transport, _localDeliveryAckPort));
    }
}
