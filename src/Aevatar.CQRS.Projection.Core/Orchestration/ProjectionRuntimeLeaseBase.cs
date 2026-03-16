using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionRuntimeLeaseBase : IProjectionRuntimeLease, IProjectionStreamSubscriptionRuntimeLease
{
    protected ProjectionRuntimeLeaseBase(string rootEntityId)
    {
        ArgumentNullException.ThrowIfNull(rootEntityId);
        RootEntityId = rootEntityId;
    }

    public string RootEntityId { get; }

    public IActorStreamSubscriptionLease? ActorStreamSubscriptionLease { get; set; }

    public virtual int GetLiveSinkSubscriptionCount() => 0;
}

public abstract class EventSinkProjectionRuntimeLeaseBase<TEvent>
    : ProjectionRuntimeLeaseBase
{
    private readonly object _liveSinkGate = new();
    private readonly List<LiveSinkSubscription> _liveSinkSubscriptions = [];

    protected EventSinkProjectionRuntimeLeaseBase(string rootEntityId)
        : base(rootEntityId)
    {
    }

    public IAsyncDisposable? AttachOrReplaceLiveSinkSubscription(
        IEventSink<TEvent> sink,
        IAsyncDisposable streamSubscription)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(streamSubscription);

        lock (_liveSinkGate)
        {
            var index = _liveSinkSubscriptions.FindIndex(x => ReferenceEquals(x.Sink, sink));
            if (index < 0)
            {
                _liveSinkSubscriptions.Add(new LiveSinkSubscription(sink, streamSubscription));
                return null;
            }

            var previous = _liveSinkSubscriptions[index].StreamSubscription;
            _liveSinkSubscriptions[index] = new LiveSinkSubscription(sink, streamSubscription);
            return previous;
        }
    }

    public IAsyncDisposable? DetachLiveSinkSubscription(IEventSink<TEvent> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_liveSinkGate)
        {
            var index = _liveSinkSubscriptions.FindIndex(x => ReferenceEquals(x.Sink, sink));
            if (index < 0)
                return null;

            var subscription = _liveSinkSubscriptions[index].StreamSubscription;
            _liveSinkSubscriptions.RemoveAt(index);
            return subscription;
        }
    }

    public override int GetLiveSinkSubscriptionCount()
    {
        lock (_liveSinkGate)
            return _liveSinkSubscriptions.Count;
    }

    private sealed record LiveSinkSubscription(
        IEventSink<TEvent> Sink,
        IAsyncDisposable StreamSubscription);
}
