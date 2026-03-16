using Aevatar.Workflow.Application.Abstractions.Projections;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class ProjectionWorkflowExecutionReadModelActivationPort
    : IWorkflowExecutionReadModelActivationPort
{
    private readonly WorkflowExecutionReadModelPort _projectionPort;

    public ProjectionWorkflowExecutionReadModelActivationPort(
        WorkflowExecutionReadModelPort projectionPort)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return false;

        return await _projectionPort.EnsureActorProjectionAsync(rootActorId, ct) != null;
    }
}
