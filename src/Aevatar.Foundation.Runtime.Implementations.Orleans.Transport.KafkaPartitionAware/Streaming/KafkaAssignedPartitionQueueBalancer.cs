using Orleans.Streams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

public sealed class KafkaAssignedPartitionQueueBalancer : IStreamQueueBalancer
{
    private static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int ListenerRetryAttempts = 3;
    private readonly IPartitionAssignmentManager _assignmentManager;
    private readonly ILogger<KafkaAssignedPartitionQueueBalancer> _logger;
    private readonly Lock _lock = new();

    private IStrictOrleansStreamQueueMapper? _mapper;
    private HashSet<QueueId> _queues = [];
    private List<IStreamQueueBalanceListener> _listeners = [];
    private IAsyncDisposable? _subscription;

    public KafkaAssignedPartitionQueueBalancer(
        IPartitionAssignmentManager assignmentManager,
        ILoggerFactory? loggerFactory = null)
    {
        _assignmentManager = assignmentManager;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<KafkaAssignedPartitionQueueBalancer>();
    }

    public async Task Initialize(IStreamQueueMapper queueMapper)
    {
        _mapper = queueMapper as IStrictOrleansStreamQueueMapper
                  ?? throw new InvalidOperationException("Kafka partition-aware queue balancer requires IStrictOrleansStreamQueueMapper.");
        ReplaceQueues(_assignmentManager.GetOwnedPartitions());
        _subscription = await _assignmentManager.SubscribeOwnedPartitionsChangedAsync(HandleOwnedPartitionsChangedAsync);
    }

    public async Task Shutdown()
    {
        if (_subscription == null)
            return;

        await _subscription.DisposeAsync();
        _subscription = null;
    }

    public IEnumerable<QueueId> GetMyQueues()
    {
        lock (_lock)
        {
            return _queues.ToArray();
        }
    }

    public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        var added = false;
        lock (_lock)
        {
            if (_listeners.Contains(observer))
                return false;

            var next = new List<IStreamQueueBalanceListener>(_listeners)
            {
                observer
            };
            _listeners = next;
            added = true;
        }

        if (added)
        {
            _ = Task.Run(() => NotifyListenerWithRetriesAsync(observer));
        }

        return true;
    }

    public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_lock)
        {
            if (!_listeners.Contains(observer))
                return false;

            var next = new List<IStreamQueueBalanceListener>(_listeners);
            next.Remove(observer);
            _listeners = next;
            return true;
        }
    }

    private Task HandleOwnedPartitionsChangedAsync(IReadOnlyCollection<int> ownedPartitions)
    {
        ReplaceQueues(ownedPartitions);
        return NotifyListenersAsync();
    }

    private void ReplaceQueues(IReadOnlyCollection<int> ownedPartitions)
    {
        var mapper = _mapper;
        if (mapper == null)
            return;

        lock (_lock)
        {
            _queues = ownedPartitions
                .Select(mapper.GetQueueId)
                .ToHashSet();
        }
    }

    private async Task NotifyListenersAsync()
    {
        List<IStreamQueueBalanceListener> listeners;
        lock (_lock)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
            await NotifyListenerWithRetriesAsync(listener);
    }

    private async Task NotifyListenerWithRetriesAsync(IStreamQueueBalanceListener listener)
    {
        for (var attempt = 1; attempt <= ListenerRetryAttempts; attempt++)
        {
            try
            {
                await listener.QueueDistributionChangeNotification();
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Kafka queue balancer listener notification failed on attempt {Attempt}/{MaxAttempts}.",
                    attempt,
                    ListenerRetryAttempts);

                if (attempt == ListenerRetryAttempts)
                    return;

                await Task.Delay(ListenerRetryDelay);
            }
        }
    }
}
