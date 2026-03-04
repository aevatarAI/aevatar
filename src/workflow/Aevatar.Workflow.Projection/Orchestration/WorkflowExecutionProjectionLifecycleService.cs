using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionLifecycleService
    : EventSinkProjectionLifecyclePortServiceBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, WorkflowRunEvent>,
      IWorkflowExecutionProjectionLifecyclePort
{
    public WorkflowExecutionProjectionLifecycleService(
        WorkflowExecutionProjectionOptions options,
        IProjectionPortActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionPortReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<WorkflowExecutionRuntimeLease, IEventSink<WorkflowRunEvent>, WorkflowRunEvent> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<WorkflowExecutionRuntimeLease, IEventSink<WorkflowRunEvent>, WorkflowRunEvent> liveSinkForwarder)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder,
            ResolveRuntimeLeaseOrThrow)
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

    private static WorkflowExecutionRuntimeLease ResolveRuntimeLeaseOrThrow(IWorkflowExecutionProjectionLease lease) =>
        lease as WorkflowExecutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported workflow projection lease implementation.");
}
