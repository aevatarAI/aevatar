namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionObservationSubscriber : IAsyncDisposable
{
    private IAsyncDisposable? _subscription;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsAttached => _subscription != null;

    public async Task<bool> EnsureAttachedAsync(
        IStreamProvider streamProvider,
        string rootActorId,
        Func<EventEnvelope, Task> onObservation,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_subscription != null)
                return false;

            var stream = streamProvider.GetStream(rootActorId);
            _subscription = await stream.SubscribeAsync<EventEnvelope>(
                envelope => onObservation(envelope),
                ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DetachAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_subscription == null)
                return false;

            await _subscription.DisposeAsync();
            _subscription = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DetachAsync(CancellationToken.None);
        _gate.Dispose();
    }
}
