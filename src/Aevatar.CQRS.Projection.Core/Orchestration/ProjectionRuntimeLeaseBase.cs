namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionRuntimeLeaseBase<TSink>
    : IProjectionRuntimeLease
    where TSink : class
{
    private readonly object _liveSinkGate = new();
    private readonly List<LiveSinkSubscription> _liveSinkSubscriptions = [];

    protected ProjectionRuntimeLeaseBase(string rootEntityId)
    {
        ArgumentNullException.ThrowIfNull(rootEntityId);
        RootEntityId = rootEntityId;
    }

    public string RootEntityId { get; }

    public IAsyncDisposable? AttachOrReplaceLiveSinkSubscription(
        TSink sink,
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

    public IAsyncDisposable? DetachLiveSinkSubscription(TSink sink)
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

    public int GetLiveSinkSubscriptionCount()
    {
        lock (_liveSinkGate)
            return _liveSinkSubscriptions.Count;
    }

    private sealed record LiveSinkSubscription(
        TSink Sink,
        IAsyncDisposable StreamSubscription);
}
