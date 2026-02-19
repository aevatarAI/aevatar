using System.Collections.Concurrent;
using Aevatar.CQRS.Sagas.Abstractions.Configuration;
using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.CQRS.Sagas.Core.Hosting;

public sealed class ActorSagaSubscriptionHostedService : IHostedService, IAsyncDisposable
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly ISagaRuntime _sagaRuntime;
    private readonly IReadOnlyList<ISaga> _sagas;
    private readonly SagaRuntimeOptions _options;
    private readonly ILogger<ActorSagaSubscriptionHostedService> _logger;

    private readonly ConcurrentDictionary<string, IAsyncDisposable> _subscriptionsByActorId = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private IDisposable? _createdSubscription;
    private IDisposable? _removedSubscription;
    private int _disposed;

    public ActorSagaSubscriptionHostedService(
        IActorRuntime runtime,
        IStreamProvider streams,
        ISagaRuntime sagaRuntime,
        IEnumerable<ISaga> sagas,
        IOptions<SagaRuntimeOptions> options,
        ILogger<ActorSagaSubscriptionHostedService> logger)
    {
        _runtime = runtime;
        _streams = streams;
        _sagaRuntime = sagaRuntime;
        _sagas = sagas?.ToList() ?? throw new ArgumentNullException(nameof(sagas));
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _sagas.Count == 0)
            return Task.CompletedTask;

        return StartCoreAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        DisposeLifecycleSubscriptions();

        if (_loopTask != null)
        {
            await Task.WhenAny(_loopTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }

        await DisposeSubscriptionsAsync();
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);

        await SyncSubscriptionsAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SyncSubscriptionsAsync(ct);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await SyncSubscriptionsAsync(cancellationToken);

        if (_streams is IStreamLifecycleNotifier notifier)
        {
            _createdSubscription = notifier.SubscribeCreated(actorId =>
            {
                _ = SubscribeActorSafeAsync(actorId);
            });
            _removedSubscription = notifier.SubscribeRemoved(actorId =>
            {
                _ = UnsubscribeActorSafeAsync(actorId);
            });
            return;
        }

        var intervalMs = Math.Max(_options.ActorScanIntervalMs, 100);
        _loopTask = Task.Run(() => RunLoopAsync(TimeSpan.FromMilliseconds(intervalMs), _cts.Token), CancellationToken.None);
    }

    private async Task SyncSubscriptionsAsync(CancellationToken ct)
    {
        IReadOnlyList<IActor> actors;
        try
        {
            actors = await _runtime.GetAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list actors for saga subscriptions.");
            return;
        }

        var activeActorIds = actors
            .Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var actorId in activeActorIds)
        {
            await SubscribeActorAsync(actorId, ct);
        }

        var staleActorIds = _subscriptionsByActorId.Keys
            .Where(x => !activeActorIds.Contains(x))
            .ToList();

        foreach (var staleActorId in staleActorIds)
        {
            await UnsubscribeActorAsync(staleActorId);
        }
    }

    private async Task SubscribeActorSafeAsync(string actorId)
    {
        try
        {
            await SubscribeActorAsync(actorId, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task SubscribeActorAsync(string actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorId) || _subscriptionsByActorId.ContainsKey(actorId))
            return;

        try
        {
            var stream = _streams.GetStream(actorId);
            var subscription = await stream.SubscribeAsync<EventEnvelope>(
                envelope => _sagaRuntime.ObserveAsync(actorId, envelope, _cts.Token),
                ct);

            if (!_subscriptionsByActorId.TryAdd(actorId, subscription))
                await subscription.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe actor stream for saga runtime. actorId={ActorId}", actorId);
        }
    }

    private async Task UnsubscribeActorSafeAsync(string actorId)
    {
        try
        {
            await UnsubscribeActorAsync(actorId);
        }
        catch
        {
        }
    }

    private async Task UnsubscribeActorAsync(string actorId)
    {
        if (_subscriptionsByActorId.TryRemove(actorId, out var subscription))
        {
            try
            {
                await subscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to dispose saga subscription for actor {ActorId}", actorId);
            }
        }
    }

    private async Task DisposeSubscriptionsAsync()
    {
        var subscriptions = _subscriptionsByActorId.ToArray();
        _subscriptionsByActorId.Clear();

        foreach (var (_, disposable) in subscriptions)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch
            {
                // ignore best-effort cleanup
            }
        }
    }

    private void DisposeLifecycleSubscriptions()
    {
        _createdSubscription?.Dispose();
        _removedSubscription?.Dispose();
        _createdSubscription = null;
        _removedSubscription = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _cts.Cancel();
        _cts.Dispose();
        DisposeLifecycleSubscriptions();

        if (_loopTask != null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposeSubscriptionsAsync();
    }
}
