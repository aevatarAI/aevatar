using Microsoft.Extensions.Hosting;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class KafkaPartitionLifecycleBridgeHostedService : IHostedService, IAsyncDisposable
{
    private readonly IKafkaPartitionAwareEnvelopeTransport _transport;
    private readonly IPartitionAssignmentManager _assignmentManager;
    private IAsyncDisposable? _subscription;

    public KafkaPartitionLifecycleBridgeHostedService(
        IKafkaPartitionAwareEnvelopeTransport transport,
        IPartitionAssignmentManager assignmentManager)
    {
        _transport = transport;
        _assignmentManager = assignmentManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await _transport.SubscribePartitionLifecycleAsync(
            async lifecycleEvent =>
            {
                if (lifecycleEvent.Kind == PartitionLifecycleEventKind.Assigned)
                {
                    await _assignmentManager.OnAssignedAsync([lifecycleEvent.PartitionId], cancellationToken);
                    return;
                }

                await _assignmentManager.OnRevokedAsync([lifecycleEvent.PartitionId], cancellationToken);
            },
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_subscription == null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription == null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }
}
