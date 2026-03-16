namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionReadModelActivationPort
{
    Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default);
}
