using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Projection.Configuration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionMaterializationPort
    : MaterializationProjectionPortBase<WorkflowExecutionMaterializationRuntimeLease>,
      IWorkflowExecutionMaterializationActivationPort
{
    public WorkflowExecutionMaterializationPort(
        WorkflowExecutionProjectionOptions options,
        IProjectionMaterializationActivationService<WorkflowExecutionMaterializationRuntimeLease> activationService,
        IProjectionMaterializationReleaseService<WorkflowExecutionMaterializationRuntimeLease> releaseService)
        : base(
            () => options.Enabled,
            activationService,
            releaseService)
    {
    }

    public Task<WorkflowExecutionMaterializationRuntimeLease?> EnsureActorProjectionAsync(
        string rootActorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionMaterializationStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = WorkflowProjectionKinds.ExecutionMaterialization,
            },
            ct);

    public async Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return false;

        return await EnsureActorProjectionAsync(rootActorId, ct) != null;
    }
}
