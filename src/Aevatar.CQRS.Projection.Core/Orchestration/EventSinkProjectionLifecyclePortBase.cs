using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Event-sink specialized lifecycle port base with runtime lease resolution hook.
/// </summary>
public abstract class EventSinkProjectionLifecyclePortBase<TLeaseContract, TRuntimeLease, TEvent>
    : ProjectionLifecyclePortBase<TLeaseContract, TRuntimeLease, IEventSink<TEvent>, TEvent>,
      IEventSinkProjectionLifecyclePort<TLeaseContract, TEvent>
    where TLeaseContract : class
    where TRuntimeLease : class, TLeaseContract
{
    protected EventSinkProjectionLifecyclePortBase(
        Func<bool> projectionEnabledAccessor,
        IProjectionPortActivationService<TRuntimeLease> activationService,
        IProjectionPortReleaseService<TRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<TRuntimeLease, TEvent> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<TRuntimeLease, TEvent> liveSinkForwarder)
        : base(
            projectionEnabledAccessor,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
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
}
