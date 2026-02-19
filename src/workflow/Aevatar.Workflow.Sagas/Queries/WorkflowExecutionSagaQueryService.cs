using Aevatar.CQRS.Sagas.Abstractions.Runtime;
using Aevatar.Workflow.Sagas.States;

namespace Aevatar.Workflow.Sagas.Queries;

public sealed class WorkflowExecutionSagaQueryService : IWorkflowExecutionSagaQueryService
{
    private readonly ISagaRepository _repository;

    public WorkflowExecutionSagaQueryService(ISagaRepository repository)
    {
        _repository = repository;
    }

    public Task<WorkflowExecutionSagaState?> GetAsync(string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return Task.FromResult<WorkflowExecutionSagaState?>(null);

        return _repository.LoadAsync<WorkflowExecutionSagaState>(WorkflowExecutionSagaNames.Execution, correlationId, ct);
    }

    public Task<IReadOnlyList<WorkflowExecutionSagaState>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 500);
        return _repository.ListAsync<WorkflowExecutionSagaState>(WorkflowExecutionSagaNames.Execution, boundedTake, ct);
    }
}
