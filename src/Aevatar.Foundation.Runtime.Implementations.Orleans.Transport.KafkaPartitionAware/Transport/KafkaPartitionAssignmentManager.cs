namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

public sealed class KafkaPartitionAssignmentManager : IPartitionAssignmentManager
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly IPartitionOwnedReceiverRegistry _receiverRegistry;
    private readonly Lock _lock = new();
    private HashSet<int> _ownedPartitions = [];
    private List<Func<IReadOnlyCollection<int>, Task>> _subscribers = [];

    public KafkaPartitionAssignmentManager(IPartitionOwnedReceiverRegistry receiverRegistry)
    {
        _receiverRegistry = receiverRegistry;
    }

    public async Task OnAssignedAsync(
        IReadOnlyList<int> partitionIds,
        CancellationToken ct = default)
    {
        foreach (var partitionId in partitionIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            await _receiverRegistry.EnsureStartedAsync(partitionId, ct);
        }

        lock (_lock)
        {
            var next = new HashSet<int>(_ownedPartitions);
            foreach (var partitionId in partitionIds.Distinct())
                next.Add(partitionId);
            _ownedPartitions = next;
        }

        await NotifyOwnedPartitionsChangedAsync();
    }

    public async Task OnRevokedAsync(
        IReadOnlyList<int> partitionIds,
        CancellationToken ct = default)
    {
        foreach (var partitionId in partitionIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            await _receiverRegistry.BeginClosingAsync(partitionId, ct);
            await _receiverRegistry.DrainAndCloseAsync(partitionId, DefaultDrainTimeout, ct);
        }

        lock (_lock)
        {
            foreach (var partitionId in partitionIds)
                _ownedPartitions.Remove(partitionId);
        }

        await NotifyOwnedPartitionsChangedAsync();
    }

    public IReadOnlyCollection<int> GetOwnedPartitions()
    {
        lock (_lock)
        {
            return _ownedPartitions.ToArray();
        }
    }

    public Task<IAsyncDisposable> SubscribeOwnedPartitionsChangedAsync(
        Func<IReadOnlyCollection<int>, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var next = new List<Func<IReadOnlyCollection<int>, Task>>(_subscribers)
            {
                handler
            };
            _subscribers = next;
        }

        return Task.FromResult<IAsyncDisposable>(new Subscription(this, handler));
    }

    private async Task NotifyOwnedPartitionsChangedAsync()
    {
        IReadOnlyCollection<int> ownedPartitions;
        List<Func<IReadOnlyCollection<int>, Task>> subscribers;
        lock (_lock)
        {
            ownedPartitions = _ownedPartitions.ToArray();
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
            await subscriber(ownedPartitions);
    }

    private void RemoveSubscriber(Func<IReadOnlyCollection<int>, Task> handler)
    {
        lock (_lock)
        {
            if (!_subscribers.Contains(handler))
                return;

            var next = new List<Func<IReadOnlyCollection<int>, Task>>(_subscribers);
            next.Remove(handler);
            _subscribers = next;
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly KafkaPartitionAssignmentManager _owner;
        private readonly Func<IReadOnlyCollection<int>, Task> _handler;
        private int _disposed;

        public Subscription(
            KafkaPartitionAssignmentManager owner,
            Func<IReadOnlyCollection<int>, Task> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return ValueTask.CompletedTask;

            _owner.RemoveSubscriber(_handler);
            return ValueTask.CompletedTask;
        }
    }
}
