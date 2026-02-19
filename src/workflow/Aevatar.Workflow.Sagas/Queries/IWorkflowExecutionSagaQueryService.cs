using Aevatar.Workflow.Sagas.States;

namespace Aevatar.Workflow.Sagas.Queries;

public interface IWorkflowExecutionSagaQueryService
{
    Task<WorkflowExecutionSagaState?> GetAsync(string correlationId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowExecutionSagaState>> ListAsync(int take = 50, CancellationToken ct = default);
}
