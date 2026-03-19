using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaStrictProvider;

internal sealed class KafkaStrictProviderTransportHostedService : IHostedService
{
    private readonly IKafkaStrictProviderEnvelopeTransport _transport;

    public KafkaStrictProviderTransportHostedService(IKafkaStrictProviderEnvelopeTransport transport)
    {
        _transport = transport;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _transport.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _transport.StopAsync(cancellationToken);
}
