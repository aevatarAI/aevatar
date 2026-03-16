namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionMaterializationActivationPort
{
    Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default);
}
