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
        IProjectionPortActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionPortReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope> liveSinkForwarder)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            rootActorId,
            workflowName,
            input,
            commandId,
            ct);
}
