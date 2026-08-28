using System.Threading.Channels;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

public sealed class LocalActor : IActor
{
    private readonly Channel<MailboxWorkItem> _mailbox = Channel.CreateUnbounded<MailboxWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly HashSet<string> _childrenIds = [];
    private readonly IStreamProvider _streams;
    private readonly ILogger _logger;
    private readonly IActorDeactivationHookDispatcher? _deactivationHookDispatcher;
    private readonly RuntimeActorIdentity? _identity;
    private readonly IRuntimeActorStateSchemaContextBinder? _stateSchemaContextBinder;
    private readonly IRuntimeFleetReconcileDeliveryVerifier? _fleetReconcileVerifier;
    private readonly IRuntimeFleetReconcileDeliveryAttestationBinder? _fleetReconcileAttestationBinder;
    private readonly IActorRuntimeCallbackScheduler? _callbackScheduler;
    private Task? _mailboxPump;
    private IAsyncDisposable? _selfSubscription;
    private string? _parentId;

    public LocalActor(
        IAgent agent,
        string id,
        IStreamProvider streams,
        ILogger logger,
        IActorDeactivationHookDispatcher? deactivationHookDispatcher = null,
        RuntimeActorIdentity? identity = null,
        IRuntimeActorStateSchemaContextBinder? stateSchemaContextBinder = null,
        IRuntimeFleetReconcileDeliveryVerifier? fleetReconcileVerifier = null,
        IRuntimeFleetReconcileDeliveryAttestationBinder? fleetReconcileAttestationBinder = null,
        IActorRuntimeCallbackScheduler? callbackScheduler = null)
    {
        Agent = agent;
        Id = id;
        _streams = streams;
        _logger = logger;
        _deactivationHookDispatcher = deactivationHookDispatcher;
        _identity = identity?.Clone();
        _stateSchemaContextBinder = stateSchemaContextBinder;
        _fleetReconcileVerifier = fleetReconcileVerifier;
        _fleetReconcileAttestationBinder = fleetReconcileAttestationBinder;
        _callbackScheduler = callbackScheduler;
    }

    public string Id { get; }
    public IAgent Agent { get; }
    internal string? ParentId => _parentId;
    internal int ChildrenCount => _childrenIds.Count;

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        _mailboxPump = ProcessMailboxAsync();
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
            // When publisher is self (orphan fallback to self-stream), skip to avoid self-handling.
            if ((direction == TopologyAudience.Parent &&
                 !string.Equals(publisherActorId, Id, StringComparison.Ordinal)) ||
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

        using var schemaContext = BindStateSchemaContext();
        await Agent.ActivateAsync(ct);
    }

    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        if (_selfSubscription != null) { await _selfSubscription.DisposeAsync(); _selfSubscription = null; }
        _mailbox.Writer.TryComplete();
        if (_mailboxPump != null)
            await _mailboxPump.WaitAsync(ct);
        using var schemaContext = BindStateSchemaContext();
        await Agent.DeactivateAsync(ct);
        TriggerDeactivationHook();
    }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
        EnqueueAsync(envelope, propagateFailure: true);

    internal void AcceptDispatchedEnvelope(EventEnvelope envelope) =>
        ObserveDispatchedEnvelopeAsync(EnqueueAsync(envelope));

    private void ObserveDispatchedEnvelopeAsync(Task dispatchTask)
    {
        if (dispatchTask.IsCompletedSuccessfully)
            return;

        _ = dispatchTask.ContinueWith(
            static (task, state) =>
            {
                var actor = (LocalActor)state!;
                actor._logger.LogError(
                    task.Exception,
                    "LocalActor {Id} failed to accept dispatched envelope",
                    actor.Id);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

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

    private Task EnqueueAsync(EventEnvelope envelope, bool propagateFailure = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_mailbox.Writer.TryWrite(new MailboxWorkItem(envelope, propagateFailure, completion)))
            throw new InvalidOperationException($"LocalActor {Id} mailbox is closed.");

        return completion.Task;
    }

    private async Task ProcessMailboxAsync()
    {
        await foreach (var item in _mailbox.Reader.ReadAllAsync())
            await ProcessMailboxItemAsync(item);
    }

    private async Task ProcessMailboxItemAsync(MailboxWorkItem item)
    {
        EventHandleScope scope = default;
        var scopeCreated = false;
        try
        {
            scope = EventHandleScope.Begin(_logger, Id, item.Envelope, Agent.GetType().FullName ?? Agent.GetType().Name);
            scopeCreated = true;
            // Verify asynchronously, but bind the attestation synchronously in this frame:
            // an AsyncLocal assigned inside an awaited helper does not flow back to the
            // caller, so the actor handler would observe no attestation and fail closed.
            var reconcileAttestation = await VerifyFleetReconcileAttestationAsync(
                item.Envelope);
            using var reconcileAttestationBinding = reconcileAttestation == null
                ? null
                : _fleetReconcileAttestationBinder!.Bind(reconcileAttestation);
            using var schemaContext = BindStateSchemaContext();
            await Agent.HandleEventAsync(item.Envelope);
            await CompleteHandledRetryCoalescingCursorAsync(item.Envelope);
            item.Completion.SetResult();
        }
        catch (Exception ex)
        {
            if (scopeCreated)
                scope.MarkError(ex);
            _logger.LogError(ex, "LocalActor {Id} failed to handle event", Id);
            if (item.PropagateFailure)
            {
                item.Completion.SetException(ex);
            }
            else
            {
                item.Completion.SetResult();
            }
        }
        finally
        {
            if (scopeCreated)
                scope.Dispose();
        }
    }

    private sealed record MailboxWorkItem(
        EventEnvelope Envelope,
        bool PropagateFailure,
        TaskCompletionSource Completion);

    private void TriggerDeactivationHook()
    {
        if (_deactivationHookDispatcher == null)
            return;

        _ = _deactivationHookDispatcher.DispatchAsync(Id, CancellationToken.None);
    }

    private IDisposable? BindStateSchemaContext() =>
        _identity == null ? null : _stateSchemaContextBinder?.Bind(_identity);

    private async Task CompleteHandledRetryCoalescingCursorAsync(EventEnvelope envelope)
    {
        if (Agent is not IRuntimeEnvelopeRetryCoalescingCompletionSource completionSource)
            return;

        var cursor = completionSource.ResolveHandledRetryCoalescingCursor(envelope);
        if (cursor == null)
            return;
        if (_callbackScheduler == null)
        {
            // A bare LocalActorRuntime has no runtime-owned retry scheduler, so it cannot have
            // a coalesced retry slot to complete. Standard local hosting always registers the
            // in-memory scheduler; keeping this path optional preserves lightweight embeddings.
            return;
        }
        if (_callbackScheduler is not IRuntimeEnvelopeRetryCoalescingCallbackScheduler completionScheduler)
        {
            throw new InvalidOperationException(
                "The local runtime callback scheduler does not support coalesced retry completion.");
        }

        await completionScheduler.CompleteRuntimeEnvelopeRetryAsync(
            Id,
            cursor,
            CancellationToken.None);
    }

    private async Task<RuntimeFleetReconcileDeliveryAttestation?> VerifyFleetReconcileAttestationAsync(
        EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) != true)
            return null;
        if (!string.Equals(
                Id,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            _fleetReconcileVerifier == null ||
            _fleetReconcileAttestationBinder == null)
        {
            throw new InvalidOperationException(
                "Runtime fleet reconcile callback reached an invalid runtime ingress.");
        }

        var attestation = await _fleetReconcileVerifier.VerifyAsync(
            envelope,
            CancellationToken.None);
        if (attestation == null)
        {
            throw new InvalidOperationException(
                "Runtime fleet reconcile callback delivery is not current in scheduler-owned state.");
        }

        return attestation;
    }
}
