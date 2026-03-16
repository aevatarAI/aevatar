using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Event-sink specialized lifecycle port base with runtime lease resolution hook.
/// </summary>
public abstract class EventSinkProjectionLifecyclePortBase<TLeaseContract, TRuntimeLease, TEvent>
    : IEventSinkProjectionLifecyclePort<TLeaseContract, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, IProjectionRuntimeLease, TLeaseContract
    where TEvent : class
{
    private readonly Func<bool> _projectionEnabledAccessor;
    private readonly IProjectionSessionActivationService<TRuntimeLease> _activationService;
    private readonly IProjectionSessionReleaseService<TRuntimeLease> _releaseService;
    private readonly IEventSinkProjectionSubscriptionManager<TRuntimeLease, TEvent> _sinkSubscriptionManager;
    private readonly IEventSinkProjectionLiveForwarder<TRuntimeLease, TEvent> _liveSinkForwarder;

    protected EventSinkProjectionLifecyclePortBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionSessionActivationService<TRuntimeLease> activationService,
        IProjectionSessionReleaseService<TRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<TRuntimeLease, TEvent> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<TRuntimeLease, TEvent> liveSinkForwarder)
    {
        _projectionEnabledAccessor = projectionEnabledAccessor ?? throw new ArgumentNullException(nameof(projectionEnabledAccessor));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _sinkSubscriptionManager = sinkSubscriptionManager ?? throw new ArgumentNullException(nameof(sinkSubscriptionManager));
        _liveSinkForwarder = liveSinkForwarder ?? throw new ArgumentNullException(nameof(liveSinkForwarder));
    }

    public bool ProjectionEnabled => _projectionEnabledAccessor();

    protected async Task<TLeaseContract?> EnsureProjectionAsync(
        ProjectionSessionStartRequest request,
        CancellationToken ct = default)
    {
        if (!ProjectionEnabled || request == null || string.IsNullOrWhiteSpace(request.RootActorId))
            return null;

        return await _activationService.EnsureAsync(request, ct);
    }

    public Task AttachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return Task.CompletedTask;

        var runtimeLease = ResolveRuntimeLease(lease);
        return _sinkSubscriptionManager.AttachOrReplaceAsync(
            runtimeLease,
            sink,
            evt => _liveSinkForwarder.ForwardAsync(runtimeLease, sink, evt, CancellationToken.None),
            ct);
    }

    public Task DetachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled)
            return Task.CompletedTask;

        return _sinkSubscriptionManager.DetachAsync(ResolveRuntimeLease(lease), sink, ct);
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
