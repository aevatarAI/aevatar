using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Event-sink specialized lifecycle port base with runtime lease resolution hook.
/// </summary>
public abstract class EventSinkProjectionLifecyclePortServiceBase<TLeaseContract, TRuntimeLease, TEvent>
    : ProjectionLifecyclePortServiceBase<TLeaseContract, TRuntimeLease, IEventSink<TEvent>, TEvent>,
      IEventSinkProjectionLifecyclePort<TLeaseContract, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, TLeaseContract
{
    private readonly Func<TLeaseContract, TRuntimeLease> _runtimeLeaseResolver;

    protected EventSinkProjectionLifecyclePortServiceBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionPortActivationService<TRuntimeLease> activationService,
        IProjectionPortReleaseService<TRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<TRuntimeLease, IEventSink<TEvent>, TEvent> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<TRuntimeLease, IEventSink<TEvent>, TEvent> liveSinkForwarder,
        Func<TLeaseContract, TRuntimeLease> runtimeLeaseResolver)
        : base(
            projectionEnabledAccessor,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
        _runtimeLeaseResolver = runtimeLeaseResolver ?? throw new ArgumentNullException(nameof(runtimeLeaseResolver));
    }

    public bool ProjectionEnabled => ProjectionEnabledCore;

    public Task AttachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default) =>
        AttachSinkAsync(lease, sink, ct);

    public Task DetachLiveSinkAsync(
        TLeaseContract lease,
        IEventSink<TEvent> sink,
        CancellationToken ct = default) =>
        DetachSinkAsync(lease, sink, ct);

    public Task ReleaseActorProjectionAsync(
        TLeaseContract lease,
        CancellationToken ct = default) =>
        ReleaseProjectionAsync(lease, ct);

    protected sealed override TRuntimeLease ResolveRuntimeLease(TLeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return _runtimeLeaseResolver(lease);
    }
}
