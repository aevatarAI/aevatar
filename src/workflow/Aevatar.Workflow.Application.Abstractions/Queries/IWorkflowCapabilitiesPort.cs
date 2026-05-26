namespace Aevatar.Workflow.Application.Abstractions.Queries;

public interface IWorkflowCapabilitiesPort
{
    Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default);
}
