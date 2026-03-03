using Aevatar.Scripting.Abstractions.Evolution;

namespace Aevatar.Scripting.Projection.Orchestration;

public sealed class ScriptEvolutionRuntimeLease : IScriptEvolutionProjectionLease
{
    private readonly object _liveSinkGate = new();
    private readonly List<LiveSinkSubscription> _liveSinkSubscriptions = [];

    public ScriptEvolutionRuntimeLease(ScriptEvolutionSessionProjectionContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ActorId = context.RootActorId;
        ProposalId = context.ProposalId;
    }

    public string ActorId { get; }
    public string ProposalId { get; }
    public ScriptEvolutionSessionProjectionContext Context { get; }

    public IAsyncDisposable? AttachOrReplaceLiveSinkSubscription(
        IScriptEvolutionEventSink sink,
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

    public IAsyncDisposable? DetachLiveSinkSubscription(IScriptEvolutionEventSink sink)
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
        IScriptEvolutionEventSink Sink,
        IAsyncDisposable StreamSubscription);
}
