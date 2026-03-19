using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;

internal sealed class KafkaStrictProviderProducerHostedService : IHostedService
{
    private readonly KafkaStrictProviderProducer _transport;

    public KafkaStrictProviderProducerHostedService(KafkaStrictProviderProducer transport)
    {
        _transport = transport;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _transport.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _transport.StopAsync(cancellationToken);
}
