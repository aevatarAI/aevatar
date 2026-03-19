namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

public sealed class PartitionOwnedReceiverRegistry : IPartitionOwnedReceiverRegistry
{
    private readonly IPartitionOwnedReceiverFactory _factory;
    private readonly Lock _lock = new();

    private Dictionary<int, IPartitionOwnedReceiver> _receivers = [];
    private Dictionary<int, Task<IPartitionOwnedReceiver>> _startingReceivers = [];

    public PartitionOwnedReceiverRegistry(IPartitionOwnedReceiverFactory factory)
    {
        _factory = factory;
    }

    public async Task EnsureStartedAsync(int partitionId, CancellationToken ct = default)
    {
        Task<IPartitionOwnedReceiver>? startingTask;
        lock (_lock)
        {
            if (_receivers.ContainsKey(partitionId))
                return;

            if (_startingReceivers.TryGetValue(partitionId, out startingTask))
                goto AwaitStart;

            startingTask = StartReceiverCoreAsync(partitionId, ct);
            var nextStarting = new Dictionary<int, Task<IPartitionOwnedReceiver>>(_startingReceivers)
            {
                [partitionId] = startingTask
            };
            _startingReceivers = nextStarting;
        }

AwaitStart:
        var receiver = await startingTask.ConfigureAwait(false);

        lock (_lock)
        {
            if (_receivers.ContainsKey(partitionId))
                return;

            var next = new Dictionary<int, IPartitionOwnedReceiver>(_receivers)
            {
                [partitionId] = receiver
            };
            _receivers = next;

            if (_startingReceivers.ContainsKey(partitionId))
            {
                var nextStarting = new Dictionary<int, Task<IPartitionOwnedReceiver>>(_startingReceivers);
                nextStarting.Remove(partitionId);
                _startingReceivers = nextStarting;
            }
        }
    }

    private async Task<IPartitionOwnedReceiver> StartReceiverCoreAsync(int partitionId, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var receiver = await _factory.CreateAsync(partitionId, ct);
            await receiver.StartAsync(ct);
            return receiver;
        }
        catch
        {
            lock (_lock)
            {
                if (_startingReceivers.ContainsKey(partitionId))
                {
                    var nextStarting = new Dictionary<int, Task<IPartitionOwnedReceiver>>(_startingReceivers);
                    nextStarting.Remove(partitionId);
                    _startingReceivers = nextStarting;
                }
            }

            throw;
        }
    }

    public async Task BeginClosingAsync(int partitionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IPartitionOwnedReceiver? receiver;
        lock (_lock)
        {
            _receivers.TryGetValue(partitionId, out receiver);
        }

        if (receiver != null)
            await receiver.BeginClosingAsync(ct);
    }

    public async Task DrainAndCloseAsync(
        int partitionId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IPartitionOwnedReceiver? receiver;
        lock (_lock)
        {
            _receivers.TryGetValue(partitionId, out receiver);
        }

        if (receiver == null)
            return;

        await receiver.DrainAsync(timeout, ct);
        await receiver.DisposeAsync();

        lock (_lock)
        {
            if (!_receivers.ContainsKey(partitionId))
                return;

            var next = new Dictionary<int, IPartitionOwnedReceiver>(_receivers);
            next.Remove(partitionId);
            _receivers = next;
        }
    }
}
