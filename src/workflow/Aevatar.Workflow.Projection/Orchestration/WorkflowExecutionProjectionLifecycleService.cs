using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionLifecycleService
    : ProjectionLifecyclePortServiceBase<IWorkflowExecutionProjectionLease, WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent>,
      IWorkflowExecutionProjectionLifecyclePort
{
    public WorkflowExecutionProjectionLifecycleService(
        WorkflowExecutionProjectionOptions options,
        IProjectionPortActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionPortReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent> liveSinkForwarder)
        : base(
            () => options.Enabled,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public bool ProjectionEnabled => ProjectionEnabledCore;

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

    public Task AttachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default) =>
        AttachSinkAsync(lease, sink, ct);

    public Task DetachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default) =>
        DetachSinkAsync(lease, sink, ct);

    public Task ReleaseActorProjectionAsync(
        IWorkflowExecutionProjectionLease lease,
        CancellationToken ct = default) =>
        ReleaseProjectionAsync(lease, ct);

    protected override WorkflowExecutionRuntimeLease ResolveRuntimeLease(IWorkflowExecutionProjectionLease lease) =>
        lease as WorkflowExecutionRuntimeLease
        ?? throw new InvalidOperationException("Unsupported workflow projection lease implementation.");
}
