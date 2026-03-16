using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using System.Runtime.ExceptionServices;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTarget
    : IActorCommandDispatchTarget,
      ICommandEventTarget<WorkflowRunEventEnvelope>,
      ICommandInteractionCleanupTarget<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowExecutionReadModelActivationPort _readModelActivationPort;
    private readonly IWorkflowRunActorPort _actorPort;
    private bool _createdActorsDestroyed;

    public WorkflowRunCommandTarget(
        IActor actor,
        string workflowName,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowExecutionReadModelActivationPort readModelActivationPort,
        IWorkflowRunActorPort actorPort)
    {
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? throw new ArgumentException("Workflow name is required.", nameof(workflowName))
            : workflowName;
        CreatedActorIds = createdActorIds ?? [];
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _readModelActivationPort = readModelActivationPort ?? throw new ArgumentNullException(nameof(readModelActivationPort));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
    }

    public IActor Actor { get; }
    public string WorkflowName { get; }
    public IReadOnlyList<string> CreatedActorIds { get; }
    public string TargetId => Actor.Id;
    public string ActorId => Actor.Id;
    public IWorkflowExecutionProjectionLease? ProjectionLease { get; private set; }
    public IEventSink<WorkflowRunEventEnvelope>? LiveSink { get; private set; }
    public bool DispatchFailureCleanupCompleted { get; private set; }

    public void BindLiveObservation(
        IWorkflowExecutionProjectionLease lease,
        IEventSink<WorkflowRunEventEnvelope> sink)
    {
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<WorkflowRunEventEnvelope> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("Workflow run live sink is not bound.");

    public Task<bool> ActivateReadModelAsync(CancellationToken ct = default) =>
        _readModelActivationPort.ActivateAsync(ActorId, ct);

    public async Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default)
    {
        DispatchFailureCleanupCompleted = false;
        await ReleaseAsync(destroyCreatedActors: true, ct: ct);
        DispatchFailureCleanupCompleted = true;
    }

    public Task RollbackCreatedActorsAsync(CancellationToken ct = default) =>
        DestroyCreatedActorsAsync(ct);

    public async Task DetachLiveObservationAsync(CancellationToken ct = default)
    {
        var sink = LiveSink;
        if (sink == null)
            return;

        Exception? firstException = null;
        if (ProjectionLease != null)
        {
            try
            {
                await _projectionPort.DetachLiveSinkAsync(ProjectionLease, sink, ct);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        try
        {
            await CompleteAndDisposeLiveSinkAsync(sink, ct);
            LiveSink = null;
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }

    public Task ReleaseAfterInteractionAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus> cleanup,
        CancellationToken ct = default) =>
        ReleaseAfterInteractionCoreAsync(receipt, cleanup, ct);

    public async Task ReleaseAsync(
        Func<Task>? onDetachedAsync = null,
        bool destroyCreatedActors = false,
        CancellationToken ct = default)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var liveSink = LiveSink;

        if (projectionLease != null && liveSink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    liveSink,
                    onDetachedAsync,
                    ct);
                ProjectionLease = null;
                LiveSink = null;
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }
        else
        {
            if (liveSink != null)
            {
                try
                {
                    await CompleteAndDisposeLiveSinkAsync(liveSink, ct);
                    LiveSink = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
            }

            if (projectionLease != null)
            {
                try
                {
                    await _projectionPort.ReleaseActorProjectionAsync(projectionLease, ct);
                    ProjectionLease = null;
                }
                catch (Exception ex)
                {
                    firstException ??= ex;
                }
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

    private static async Task CompleteAndDisposeLiveSinkAsync(
        IEventSink<WorkflowRunEventEnvelope> sink,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        Exception? firstException = null;
        try
        {
            sink.Complete();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            await sink.DisposeAsync();
        }
        catch (Exception ex)
        {
            firstException ??= ex;
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

    private async Task ReleaseAfterInteractionCoreAsync(
        WorkflowChatRunAcceptedReceipt receipt,
        CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus> cleanup,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(cleanup);

        var destroyCreatedActors = cleanup.ObservedCompleted || cleanup.DurableCompletion.HasTerminalCompletion;
        if (destroyCreatedActors)
        {
            await ReleaseAsync(destroyCreatedActors: true, ct: ct);
            return;
        }

        await ReleaseAsync(destroyCreatedActors: false, ct: ct);
    }
}
