using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent>
{
    private readonly IProjectionSessionEventHub<WorkflowRunEvent> _runEventStreamHub;

    public WorkflowProjectionSinkSubscriptionManager(IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub)
    {
        _runEventStreamHub = runEventStreamHub;
    }

    public async Task AttachOrReplaceAsync(
        WorkflowExecutionRuntimeLease lease,
        IWorkflowRunEventSink sink,
        Func<WorkflowRunEvent, ValueTask> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        var streamSubscription = await _runEventStreamHub.SubscribeAsync(
            lease.ActorId,
            lease.CommandId,
            handler,
            ct);

        var previous = lease.AttachOrReplaceLiveSinkSubscription(sink, streamSubscription);
        if (previous != null)
            await previous.DisposeAsync();
    }

    public async Task DetachAsync(
        WorkflowExecutionRuntimeLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        var streamSubscription = lease.DetachLiveSinkSubscription(sink);
        if (streamSubscription != null)
            await streamSubscription.DisposeAsync();
    }
}
