using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTarget
    : IActorCommandDispatchTarget,
      ICommandDispatchCleanupAware
{
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;

    public WorkflowRunCommandTarget(
        IActor actor,
        string workflowName,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowExecutionProjectionLifecyclePort projectionPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? throw new ArgumentException("Workflow name is required.", nameof(workflowName))
            : workflowName;
        CreatedActorIds = createdActorIds ?? [];
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public IActor Actor { get; }
    public string WorkflowName { get; }
    public IReadOnlyList<string> CreatedActorIds { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public IWorkflowExecutionProjectionLease? ProjectionLease { get; private set; }
    public IEventSink<WorkflowRunEvent>? LiveSink { get; private set; }

    public void BindLiveObservation(
        IWorkflowExecutionProjectionLease lease,
        IEventSink<WorkflowRunEvent> sink)
    {
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<WorkflowRunEvent> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("Workflow run live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(null, ct);

    public async Task ReleaseAsync(
        Func<Task>? onDetachedAsync = null,
        CancellationToken ct = default)
    {
        if (ProjectionLease != null && LiveSink != null)
        {
            await _projectionPort.DetachReleaseAndDisposeAsync(
                ProjectionLease,
                LiveSink,
                onDetachedAsync,
                ct);
            ProjectionLease = null;
            LiveSink = null;
            return;
        }

        if (LiveSink != null)
        {
            await LiveSink.DisposeAsync();
            LiveSink = null;
        }

        if (ProjectionLease != null)
        {
            await _projectionPort.ReleaseActorProjectionAsync(ProjectionLease, ct);
            ProjectionLease = null;
        }
    }
}
