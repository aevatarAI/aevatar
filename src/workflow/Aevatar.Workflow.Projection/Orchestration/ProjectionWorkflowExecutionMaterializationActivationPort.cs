using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class ProjectionWorkflowExecutionMaterializationActivationPort
    : IWorkflowExecutionMaterializationActivationPort
{
    private readonly WorkflowExecutionMaterializationPort _materializationPort;

    public ProjectionWorkflowExecutionMaterializationActivationPort(
        WorkflowExecutionMaterializationPort materializationPort)
    {
        _materializationPort = materializationPort ?? throw new ArgumentNullException(nameof(materializationPort));
    }

    public async Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return false;

        return await _materializationPort.EnsureActorProjectionAsync(rootActorId, ct) != null;
    }
}
