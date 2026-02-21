using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionRuntimeLease : IWorkflowExecutionProjectionLease
{
    private readonly object _liveSinkGate = new();
    private readonly List<LiveSinkSubscription> _liveSinkSubscriptions = [];

    public WorkflowExecutionRuntimeLease(WorkflowExecutionProjectionContext context)
    {
        Context = context;
        ActorId = context.RootActorId;
        CommandId = context.CommandId;
    }

    public string ActorId { get; }
    public string CommandId { get; }
    public WorkflowExecutionProjectionContext Context { get; }

    public IAsyncDisposable? AttachOrReplaceLiveSinkSubscription(
        IWorkflowRunEventSink sink,
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

    public IAsyncDisposable? DetachLiveSinkSubscription(IWorkflowRunEventSink sink)
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
        IWorkflowRunEventSink Sink,
        IAsyncDisposable StreamSubscription);
}
