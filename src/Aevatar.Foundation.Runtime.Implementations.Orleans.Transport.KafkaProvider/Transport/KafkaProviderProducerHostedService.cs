using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider;

internal sealed class KafkaProviderProducerHostedService : IHostedService
{
    private readonly KafkaProviderProducer _transport;

    public KafkaProviderProducerHostedService(KafkaProviderProducer transport)
    {
        _transport = transport;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _transport.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _transport.StopAsync(cancellationToken);
}
