// LocalActor - IActor implementation.
// Focuses on two responsibilities: mailbox serialization and stream subscription management.

using System.Diagnostics;
using Aevatar.Foundation.Runtime.Routing;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Actors;

public sealed class LocalActor : IActor
{
    private readonly SemaphoreSlim _mailbox = new(1, 1);
    private readonly EventRouter _router;
    private readonly IStreamProvider _streams;
    private readonly ILogger _logger;
    private readonly IActorDeactivationHookDispatcher? _deactivationHookDispatcher;
    private IAsyncDisposable? _selfSubscription;

    public LocalActor(
        IAgent agent,
        string id,
        EventRouter router,
        IStreamProvider streams,
        ILogger logger,
        IActorDeactivationHookDispatcher? deactivationHookDispatcher = null)
    {
        Agent = agent;
        Id = id;
        _router = router;
        _streams = streams;
        _logger = logger;
        _deactivationHookDispatcher = deactivationHookDispatcher;
    }

    public string Id { get; }
    public IAgent Agent { get; }
    internal EventRouter Router => _router;

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        // Subscribe to self stream (handle Self events and Up events from children).
        var selfStream = _streams.GetStream(Id);
        _selfSubscription = await selfStream.SubscribeAsync<EventEnvelope>(async envelope =>
        {
            // Handle Self events directly.
            if (envelope.Direction == EventDirection.Self)
            {
                await EnqueueAsync(envelope);
                return;
            }

            // Handle Up events from children (they produce to parent's stream).
            if (envelope.Direction == EventDirection.Up)
            {
                await EnqueueAsync(envelope);
                return;
            }

            if (envelope.Direction is EventDirection.Down or EventDirection.Both &&
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

    public Task<string?> GetParentIdAsync() => Task.FromResult(_router.ParentId);
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_router.ChildrenIds.ToList());

    // Hierarchy operations (called by LocalActorRuntime)

    internal void AddChild(string childId) => _router.AddChild(childId);
    internal void RemoveChild(string childId) => _router.RemoveChild(childId);

    internal Task SubscribeToParentAsync(string parentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _router.SetParent(parentId);
        return Task.CompletedTask;
    }

    internal Task UnsubscribeFromParentAsync()
    {
        _router.ClearParent();
        return Task.CompletedTask;
    }

    // ─── Mailbox ───

    private async Task EnqueueAsync(EventEnvelope envelope, bool propagateFailure = false)
    {
        using var activity = AevatarActivitySource.StartHandleEvent(Id, envelope.Id);
        activity?.SetTag("aevatar.event.direction", envelope.Direction.ToString());
        activity?.SetTag("aevatar.event.type", envelope.Payload?.TypeUrl);
        activity?.SetTag("aevatar.event.publisher", envelope.PublisherId);

        var sw = Stopwatch.StartNew();
        var status = "ok";
        await _mailbox.WaitAsync();
        try
        {
            await Agent.HandleEventAsync(envelope);
        }
        catch (Exception ex)
        {
            status = "error";
            activity?.SetTag("aevatar.error", true);
            activity?.SetTag("aevatar.error.message", ex.Message);
            _logger.LogError(ex, "LocalActor {Id} failed to handle event", Id);
            if (propagateFailure)
                throw;
        }
        finally
        {
            _mailbox.Release();
            sw.Stop();
            AgentMetrics.EventsHandled.Add(1,
            [
                new("agent.id", Id),
                new("event.direction", envelope.Direction.ToString()),
                new("event.type", envelope.Payload?.TypeUrl ?? "unknown"),
                new("status", status),
            ]);
            AgentMetrics.EventHandleDuration.Record(sw.Elapsed.TotalMilliseconds,
            [
                new("agent.id", Id),
                new("event.direction", envelope.Direction.ToString()),
                new("status", status),
            ]);
        }
    }

    private void TriggerDeactivationHook()
    {
        if (_deactivationHookDispatcher == null)
            return;

        _ = _deactivationHookDispatcher.DispatchAsync(Id, CancellationToken.None);
    }
}
