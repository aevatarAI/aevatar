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
        IProjectionSessionActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionSessionReleaseService<WorkflowExecutionRuntimeLease> releaseService,
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
            new ProjectionSessionStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = WorkflowProjectionKinds.ExecutionSession,
                SessionId = commandId,
            },
            ct);
}
