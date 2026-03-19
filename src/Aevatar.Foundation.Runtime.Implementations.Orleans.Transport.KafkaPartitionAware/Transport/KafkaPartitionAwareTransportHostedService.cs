using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionAwareTransportHostedService : IHostedService
{
    private readonly IKafkaPartitionAwareEnvelopeTransport _transport;

    public KafkaPartitionAwareTransportHostedService(IKafkaPartitionAwareEnvelopeTransport transport)
    {
        _transport = transport;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _transport.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _transport.StopAsync(cancellationToken);
}
