using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionPort
    : EventSinkProjectionLifecyclePortBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>,
      IWorkflowExecutionProjectionPort
{
    public WorkflowExecutionProjectionPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionScopeActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionScopeReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IProjectionSessionEventHub<WorkflowRunEventEnvelope> sessionEventHub)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sessionEventHub)
    {
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
    protected override WorkflowExecutionRuntimeLease ResolveRuntimeLease(IWorkflowExecutionProjectionLease lease)
    {
        if (lease is WorkflowExecutionRuntimeLease runtimeLease)
            return runtimeLease;

        return new WorkflowExecutionRuntimeLease(new WorkflowExecutionProjectionContext
        {
            RootActorId = lease.ActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
            SessionId = lease.CommandId,
        });
    }
}
