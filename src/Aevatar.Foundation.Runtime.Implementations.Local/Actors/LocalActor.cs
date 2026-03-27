using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Deduplication;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActor : IActor
{
    private readonly SemaphoreSlim _mailbox = new(1, 1);
    private readonly HashSet<string> _childrenIds = [];
    private readonly IStreamProvider _streams;
    private readonly ILogger _logger;
    private readonly IActorDeactivationHookDispatcher? _deactivationHookDispatcher;
    private readonly IEventDeduplicator? _deduplicator;
    private IAsyncDisposable? _selfSubscription;
    private string? _parentId;

    public LocalActor(
        IAgent agent,
        string id,
        IStreamProvider streams,
        ILogger logger,
        IActorDeactivationHookDispatcher? deactivationHookDispatcher = null,
        IEventDeduplicator? deduplicator = null)
    {
        Agent = agent;
        Id = id;
        _streams = streams;
        _logger = logger;
        _deactivationHookDispatcher = deactivationHookDispatcher;
        _deduplicator = deduplicator;
    }

    public string Id { get; }
    public IAgent Agent { get; }
    internal string? ParentId => _parentId;
    internal int ChildrenCount => _childrenIds.Count;

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        // Subscribe to self stream (handle Self events and Up events from children).
        var selfStream = _streams.GetStream(Id);
        _selfSubscription = await selfStream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            var route = envelope.Route;
            var direction = route.GetTopologyAudience();
            var publisherActorId = route?.PublisherActorId;

            if (route.IsObserverPublication())
            {
                if (!StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id) ||
                    StreamForwardingRules.IsTransitOnlyForwarding(envelope))
                {
                    return;
                }

                await EnqueueAsync(envelope);
                return;
            }

            if (route.IsDirect())
            {
                if (string.Equals(route.GetTargetActorId(), Id, StringComparison.Ordinal))
                    await EnqueueAsync(envelope);
                return;
            }

            // Handle Self events directly.
            if (direction == TopologyAudience.Self)
            {
                await EnqueueAsync(envelope);
                return;
            }

            // Handle Up events from children (they produce to parent's stream).
            // Child events may use Both (self + parent), so treat direct-child Both as upward.
            if (direction == TopologyAudience.Parent ||
                (direction == TopologyAudience.ParentAndChildren &&
                 !string.IsNullOrWhiteSpace(publisherActorId) &&
                 _childrenIds.Contains(publisherActorId)))
            {
                await EnqueueAsync(envelope);
                return;
            }

            if (direction is TopologyAudience.Children or TopologyAudience.ParentAndChildren &&
                StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id))
            {
                if (StreamForwardingRules.IsTransitOnlyForwarding(envelope))
                    return;

                await EnqueueAsync(envelope);
                return;
            }

            // Down/Both events are not handled on self-stream unless forwarded by stream-layer routing.
        }, ct);

        await Agent.ActivateAsync(ct);
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        if (_selfSubscription != null) { await _selfSubscription.DisposeAsync(); _selfSubscription = null; }
        await Agent.DeactivateAsync(ct);
        TriggerDeactivationHook();
    }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        EnqueueAsync(envelope, propagateFailure: true);

    public Task<string?> GetParentIdAsync() => Task.FromResult(_parentId);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        Task.FromResult<IReadOnlyList<string>>([.. _childrenIds]);

    // Hierarchy operations (called by LocalActorRuntime)

    internal void AddChild(string childId) => _childrenIds.Add(childId);
    internal void RemoveChild(string childId) => _childrenIds.Remove(childId);

    internal Task SubscribeToParentAsync(string parentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _parentId = parentId;
        return Task.CompletedTask;
    }

    internal Task UnsubscribeFromParentAsync()
    {
        _parentId = null;
        return Task.CompletedTask;
    }

    // ─── Mailbox ───

    private async Task EnqueueAsync(EventEnvelope envelope, bool propagateFailure = false)
    {
        EventHandleScope scope = default;
        var scopeCreated = false;
        await _mailbox.WaitAsync();
        try
        {
            if (_deduplicator != null &&
                RuntimeEnvelopeDeduplication.TryBuildDedupKey(Id, envelope, out var dedupKey) &&
                !await _deduplicator.TryRecordAsync(dedupKey))
            {
                _logger.LogDebug(
                    "LocalActor {Id} dropped duplicate envelope {EnvelopeId} with dedup key {DedupKey}",
                    Id,
                    envelope.Id,
                    dedupKey);
                return;
            }

            scope = EventHandleScope.Begin(_logger, Id, envelope);
            scopeCreated = true;
            await Agent.HandleEventAsync(envelope);
        }
        catch (Exception ex)
        {
            if (scopeCreated)
                scope.MarkError(ex);
            _logger.LogError(ex, "LocalActor {Id} failed to handle event", Id);
            if (propagateFailure)
                throw;
        }
        finally
        {
            if (scopeCreated)
                scope.Dispose();
            _mailbox.Release();
        }
    }

    private void TriggerDeactivationHook()
    {
        if (_deactivationHookDispatcher == null)
            return;

        _ = _deactivationHookDispatcher.DispatchAsync(Id, CancellationToken.None);
    }
}
