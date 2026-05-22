using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionPort
{
    private readonly IActorRuntime _runtime;

    public WorkflowExecutionProjectionPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionScopeActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> sessionEventHub,
        IActorRuntime runtime)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string commandId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = commandId,
            },
            ct);

    // Refactor (iter35/cluster-039-observation-binder-attach-only):
    //   Old pattern: Command observation binders synchronously ensure and attach projection leases before dispatch,让 request/command preparation 拥有 projection lifecycle。
    //   New principle: Command observation binders 仅 attach 到 pre-existing lease/session;cold session 返回 ProjectionPending / ProjectionUnavailable;projection activation 移到 projection-owned startup / background lifecycle。
    //   删除 pre-dispatch projection activation from command binders。不新增 top-level CLAUDE.md exception。
    public async Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
        string rootActorId,
        string commandId,
        IEventSink<WorkflowRunEventEnvelope> sink,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(rootActorId) ||
            string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            rootActorId,
            WorkflowProjectionKinds.ExecutionSession,
            ProjectionRuntimeMode.SessionObservation,
            commandId);
        if (!await _runtime.ExistsAsync(ProjectionScopeActorId.Build(scopeKey)).ConfigureAwait(false))
            return null;

        var lease = new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = rootActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
            SessionId = commandId,
        });
        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>(lease, liveSinkLease);
    }
}
