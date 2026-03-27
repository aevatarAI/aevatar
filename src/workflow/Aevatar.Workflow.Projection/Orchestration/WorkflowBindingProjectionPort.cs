using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingProjectionPort
    : MaterializationProjectionPortBase<WorkflowBindingRuntimeLease>,
      IWorkflowBindingProjectionActivationPort
{
    public WorkflowBindingProjectionPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionScopeActivationService<WorkflowBindingRuntimeLease> activationService,
        IProjectionScopeReleaseService<WorkflowBindingRuntimeLease> releaseService)
        : base(
            () => options.Enabled,
            activationService,
            releaseService)
    {
    }

    public Task<WorkflowBindingRuntimeLease?> EnsureActorProjectionAsync(
        string rootActorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = WorkflowProjectionKinds.Binding,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);

    public async Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return false;

        return await EnsureActorProjectionAsync(rootActorId, ct) != null;
    }
}
