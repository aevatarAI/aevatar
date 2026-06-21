using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using System.Runtime.ExceptionServices;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTarget
    : ICommandDispatchTarget,
      ICommandEventTarget<WorkflowRunEventEnvelope>,
      ICommandInteractionCleanupTarget<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>,
      ICommandDetachedContinuationTarget<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>,
      ICommandDispatchCleanupAware
{
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly WorkflowRunDurableCompletionResolver _durableCompletionResolver;
    private readonly WorkflowRunMaterializationReclaimGate? _reclaimGate;
    private readonly Func<Func<Task>, Task> _detachedReclaimLauncher;
    private readonly bool _destroyCreatedActorsOnDispatchFailure;
    private bool _createdActorsDestroyed;
    private Task _pendingReclaim = Task.CompletedTask;

    public WorkflowRunCommandTarget(
        string actorId,
        string workflowName,
        IReadOnlyList<string>? createdActorIds,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        WorkflowRunDurableCompletionResolver durableCompletionResolver,
        bool destroyCreatedActorsOnDispatchFailure = true,
        WorkflowRunMaterializationReclaimGate? reclaimGate = null,
        Func<Func<Task>, Task>? detachedReclaimLauncher = null)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: accepted-only dispatch reused interaction targets that owned live sinks
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ActorId = string.IsNullOrWhiteSpace(actorId)
            ? throw new ArgumentException("Actor id is required.", nameof(actorId))
            : actorId;
        WorkflowName = string.IsNullOrWhiteSpace(workflowName)
            ? throw new ArgumentException("Workflow name is required.", nameof(workflowName))
            : workflowName;
        CreatedActorIds = createdActorIds ?? [];
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
        _destroyCreatedActorsOnDispatchFailure = destroyCreatedActorsOnDispatchFailure;
        // 06-20-observatory-run-state-feed (R2): reclaim throwaway ad-hoc run actors only after their
        // current-state doc is confirmed materialized. The gate is absent for seeded/non-ephemeral targets.
        _reclaimGate = reclaimGate;
        // The reclaim wait runs detached so the in-request interaction (and thus SSE latency) is unaffected:
        // the launcher returns the reclaim task, but the caller never awaits it. Production runs it on the
        // thread pool; tests can await PendingReclaimTask (or inject an inline launcher) for determinism.
        _detachedReclaimLauncher = detachedReclaimLauncher
            ?? (reclaim => Task.Run(reclaim));
    }

    public string ActorId { get; }
    public string WorkflowName { get; }
    public IReadOnlyList<string> CreatedActorIds { get; }
    public string TargetId => ActorId;
    public IWorkflowExecutionProjectionLease? ProjectionLease { get; private set; }
    public IAsyncDisposable? LiveSinkLease { get; private set; }
    public IEventSink<WorkflowRunEventEnvelope>? LiveSink { get; private set; }
    public bool DispatchFailureCleanupCompleted { get; private set; }

    public void BindLiveObservation(
        IWorkflowExecutionProjectionLease lease,
        IAsyncDisposable? liveSinkLease,
        IEventSink<WorkflowRunEventEnvelope> sink)
    {
        // Refactor (iter35/cluster-039-observation-binder-attach-only):
        //   Old pattern: Command observation binders synchronously ensure and attach projection leases before dispatch,让 request/command preparation 拥有 projection lifecycle。
        //   New principle: Command observation binders 仅 attach 到 pre-existing lease/session;cold session 返回 ProjectionPending / ProjectionUnavailable;projection activation 移到 projection-owned startup / background lifecycle。
        //   删除 pre-dispatch projection activation from command binders。不新增 top-level CLAUDE.md exception。
        ProjectionLease = lease ?? throw new ArgumentNullException(nameof(lease));
        LiveSinkLease = liveSinkLease;
        LiveSink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public IEventSink<WorkflowRunEventEnvelope> RequireLiveSink() =>
        LiveSink ?? throw new InvalidOperationException("Workflow run live sink is not bound.");

    public async Task CleanupAfterDispatchFailureAsync(CancellationToken ct = default)
    {
        DispatchFailureCleanupCompleted = false;
        await ReleaseAsync(destroyCreatedActors: _destroyCreatedActorsOnDispatchFailure, ct: ct);
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
                await _projectionPort.DetachLiveSinkAsync(LiveSinkLease, ct);
                LiveSinkLease = null;
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

    public Task PublishDetachedCommandSignalAsync(
        DetachedCommandSignal<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> signal,
        CancellationToken ct = default) =>
        // Refactor (iter17/cluster-036):
        // Old pattern: the generic detached worker resolved durable workflow state and destroyed created actors.
        // New principle: workflow target consumes detached signals and owns durable fallback plus cleanup decisions.
        PublishDetachedCommandSignalCoreAsync(signal, ct);

    public async Task ReleaseAsync(
        Func<Task>? onDetachedAsync = null,
        bool destroyCreatedActors = false,
        CancellationToken ct = default)
    {
        Exception? firstException = null;
        var projectionLease = ProjectionLease;
        var liveSinkLease = LiveSinkLease;
        var liveSink = LiveSink;

        if (projectionLease != null && liveSink != null)
        {
            try
            {
                await _projectionPort.DetachReleaseAndDisposeAsync(
                    projectionLease,
                    liveSinkLease,
                    liveSink,
                    onDetachedAsync,
                    ct);
                ProjectionLease = null;
                LiveSinkLease = null;
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
                    LiveSinkLease = null;
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
                    LiveSinkLease = null;
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
                await _runProvisioningPort.DestroyAsync(actorId, ct);
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

        // 06-20-observatory-run-state-feed (R2): the in-request cleanup releases only the session
        // projection lease + live sink (never destroys), so SSE latency is unaffected. The throwaway
        // ad-hoc run/definition actors are reclaimed separately, gated on confirmed materialization, so
        // their current-state doc is not dropped before the durable projection scope materializes it.
        await ReleaseAsync(destroyCreatedActors: false, ct: ct);

        var runReachedTerminal = cleanup.ObservedCompleted || cleanup.DurableCompletion.HasTerminalCompletion;
        if (runReachedTerminal)
            ScheduleMaterializationGatedReclaim();
    }

    private void ScheduleMaterializationGatedReclaim()
    {
        // No gate (seeded/non-ephemeral target) → fall back to the original behavior: destroy the created
        // actors directly. Such targets are not the throwaway-per-call ad-hoc runs this gate protects.
        if (_reclaimGate == null)
        {
            if (CreatedActorIds.Count > 0)
                _pendingReclaim = _detachedReclaimLauncher(() => DestroyCreatedActorsAsync(CancellationToken.None));
            return;
        }

        if (_createdActorsDestroyed || CreatedActorIds.Count == 0)
            return;

        _pendingReclaim = _detachedReclaimLauncher(ReclaimCreatedActorsWhenMaterializedAsync);
    }

    // 06-20-observatory-run-state-feed (R2): the reclaim wait uses CancellationToken.None (a teardown
    // lifetime, NOT the request/interaction token) so request cancellation does not abort it before the
    // materialization gate confirms. Only after confirmation are the throwaway actors destroyed; on a
    // deferral (timeout / scope absent / unknown head version) the actors are intentionally left persisted.
    private async Task ReclaimCreatedActorsWhenMaterializedAsync()
    {
        var materialized = await _reclaimGate!.TryConfirmMaterializedAsync(ActorId, CancellationToken.None);
        if (materialized)
            await DestroyCreatedActorsAsync(CancellationToken.None);
    }

    // Test seam: production schedules the reclaim fire-and-forget; tests await its completion deterministically.
    internal Task PendingReclaimTask => _pendingReclaim;

    private async Task PublishDetachedCommandSignalCoreAsync(
        DetachedCommandSignal<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> signal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var observedCompleted = signal is DetachedCommandCompleted<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>;
        var observedCompletion = signal switch
        {
            DetachedCommandCompleted<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> completed => completed.Completion,
            DetachedCommandTimeout<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus> timeout => timeout.Completion,
            _ => WorkflowProjectionCompletionStatus.Unknown,
        };

        var durableCompletion = observedCompleted
            ? CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete
            : await _durableCompletionResolver.ResolveAsync(signal.Receipt, ct);

        if (!observedCompleted && durableCompletion.HasTerminalCompletion)
        {
            observedCompleted = true;
            observedCompletion = durableCompletion.Completion;
        }

        await ReleaseAfterInteractionCoreAsync(
            signal.Receipt,
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                observedCompleted,
                observedCompletion,
                durableCompletion),
            ct);
    }
}
