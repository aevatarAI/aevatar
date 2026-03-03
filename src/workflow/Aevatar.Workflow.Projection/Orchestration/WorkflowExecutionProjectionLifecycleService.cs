using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionProjectionLifecycleService
    : WorkflowProjectionLifecyclePortServiceBase,
      IWorkflowExecutionProjectionLifecyclePort
{
    public WorkflowExecutionProjectionLifecycleService(
        WorkflowExecutionProjectionOptions options,
        IProjectionPortActivationService<WorkflowExecutionRuntimeLease> activationService,
        IProjectionPortReleaseService<WorkflowExecutionRuntimeLease> releaseService,
        IWorkflowProjectionSinkSubscriptionManager sinkSubscriptionManager,
        IWorkflowProjectionLiveSinkForwarder liveSinkForwarder)
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
        IEventSink<WorkflowRunEvent> sink,
        CancellationToken ct = default) =>
        AttachSinkAsync(lease, sink, ct);

    public Task DetachLiveSinkAsync(
        IWorkflowExecutionProjectionLease lease,
        IEventSink<WorkflowRunEvent> sink,
        CancellationToken ct = default) =>
        DetachSinkAsync(lease, sink, ct);

    public Task ReleaseActorProjectionAsync(
        IWorkflowExecutionProjectionLease lease,
        CancellationToken ct = default) =>
        ReleaseProjectionAsync(lease, ct);
}
