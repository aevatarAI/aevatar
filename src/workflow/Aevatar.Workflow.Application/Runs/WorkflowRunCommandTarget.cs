using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using System.Runtime.ExceptionServices;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTarget
    : IActorCommandDispatchTarget,
      ICommandDispatchCleanupAware
{
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;
    private readonly IWorkflowRunActorPort _actorPort;
    private bool _createdActorsDestroyed;

    public WorkflowRunCommandTarget(
        IActor actor,
        string workflowName,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowExecutionProjectionLifecyclePort projectionPort,
        IWorkflowRunActorPort actorPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? throw new ArgumentException("Workflow name is required.", nameof(workflowName))
            : workflowName;
        CreatedActorIds = createdActorIds ?? [];
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
    }

    public IActor Actor { get; }
    public string WorkflowName { get; }
    public IReadOnlyList<string> CreatedActorIds { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public IWorkflowExecutionProjectionLease? ProjectionLease { get; private set; }
    public IEventSink<WorkflowRunEventEnvelope>? LiveSink { get; private set; }

    public void BindLiveObservation(
        IWorkflowExecutionProjectionLease lease,
        IEventSink<WorkflowRunEventEnvelope> sink)
    {
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<WorkflowRunEventEnvelope> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("Workflow run live sink is not bound.");

    public Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default) =>
        ReleaseAsync(destroyCreatedActors: true, ct: ct);

    public Task RollbackCreatedActorsAsync(CancellationToken ct = default) =>
        DestroyCreatedActorsAsync(ct);

    public async Task ReleaseAsync(
        Func<Task>? onDetachedAsync = null,
        bool destroyCreatedActors = false,
        CancellationToken ct = default)
    {
        Exception? firstException = null;
        if (ProjectionLease != null && LiveSink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    ProjectionLease,
                    LiveSink,
                    onDetachedAsync,
                    ct);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }

            ProjectionLease = null;
            LiveSink = null;
        }
        else
        {
            if (LiveSink != null)
            {
                try
                {
                    await LiveSink.DisposeAsync();
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }

                LiveSink = null;
            }

            if (ProjectionLease != null)
            {
                try
                {
                    await _projectionPort.ReleaseActorProjectionAsync(ProjectionLease, ct);
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }

                ProjectionLease = null;
            }
        }

        if (destroyCreatedActors)
        {
            try
            {
                await DestroyCreatedActorsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }

    private async Task DestroyCreatedActorsAsync(CancellationToken ct)
    {
        if (_createdActorsDestroyed || CreatedActorIds.Count == 0)
            return;

        List<Exception>? failures = null;
        foreach (var actorId in CreatedActorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
        {
            try
            {
                await _actorPort.DestroyAsync(actorId, ct);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new InvalidOperationException(
                    $"Failed to destroy workflow actor '{actorId}'.",
                    ex));
            }
        }

        if (failures == null)
        {
            _createdActorsDestroyed = true;
            return;
        }

        ExceptionDispatchInfo.Capture(
            failures.Count == 1
                ? failures[0]
                : new AggregateException("Workflow actor cleanup failed.", failures)).Throw();
    }
}
