namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowBindingProjectionActivationPort
{
    Task<bool> ActivateAsync(string rootActorId, CancellationToken ct = default);
}
