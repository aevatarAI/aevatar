using System.Collections.Concurrent;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Event-sink specialized lifecycle port base with runtime lease resolution hook.
/// Sink subscriptions are process-local transient I/O handles (not business fact state).
/// </summary>
public abstract class EventSinkProjectionLifecyclePortBase<TLeaseContract, TRuntimeLease, TEvent>
    : IEventSinkProjectionLifecyclePort<TLeaseContract, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, IProjectionRuntimeLease, TLeaseContract
    where TEvent : class
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionScopeActivationService<TRuntimeLease> _activationService;
    private readonly IProjectionScopeReleaseService<TRuntimeLease> _releaseService;
    private readonly IProjectionSessionEventHub<TEvent> _sessionEventHub;

    // Keyed by object identity (ReferenceEqualityComparer) to avoid RuntimeHelpers.GetHashCode collisions.
    private readonly ConcurrentDictionary<object, IAsyncDisposable> _sinkSubscriptions =
        new(ReferenceEqualityComparer.Instance);

    protected EventSinkProjectionLifecyclePortBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionScopeActivationService<TRuntimeLease> activationService,
        IProjectionScopeReleaseService<TRuntimeLease> releaseService,
        IProjectionSessionEventHub<TEvent> sessionEventHub)
    {
        _projectionEnabledAccessor = projectionEnabledAccessor ?? throw new ArgumentNullException(nameof(projectionEnabledAccessor));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _sessionEventHub = sessionEventHub ?? throw new ArgumentNullException(nameof(sessionEventHub));
    }

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    protected async Task<TLeaseContract?> EnsureProjectionAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || request == null || string.IsNullOrWhiteSpace(request.RootActorId))
            return null;

        return await _activationService.EnsureAsync(request, ct);
    }

    public async Task AttachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return;

        var runtimeLease = ResolveRuntimeLease(lease);
        if (runtimeLease is not IProjectionPortSessionLease portLease)
        {
            throw new InvalidOperationException(
                $"Runtime lease `{runtimeLease.GetType().FullName}` must implement `{typeof(IProjectionPortSessionLease).FullName}`.");
        }

        var subscription = await _sessionEventHub.SubscribeAsync(
            portLease.ScopeId,
            portLease.SessionId,
            evt => sink.PushAsync(evt, CancellationToken.None),
            ct).ConfigureAwait(false);

        var previous = _sinkSubscriptions.TryGetValue(sink, out var existing) ? existing : null;
        _sinkSubscriptions[sink] = subscription;
        if (previous != null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }

    public async Task DetachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return;

        if (_sinkSubscriptions.TryRemove(sink, out var subscription))
            await subscription.DisposeAsync().ConfigureAwait(false);
    }

    public Task ReleaseActorProjectionAsync(
        TLeaseContract lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return Task.CompletedTask;

        return _releaseService.ReleaseIfIdleAsync(ResolveRuntimeLease(lease), ct);
    }

    protected virtual TRuntimeLease ResolveRuntimeLease(TLeaseContract lease) =>
        lease as TRuntimeLease
        ?? throw new InvalidOperationException(
            $"Unsupported projection lease type `{lease.GetType().FullName}`; expected `{typeof(TRuntimeLease).FullName}`.");
}
