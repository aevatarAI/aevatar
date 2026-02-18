using Aevatar.CQRS.Core.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Adapters;

public sealed class WorkflowExecutionQueryServiceAdapter
    : IAgentQueryService<WorkflowAgentSummary>,
      IExecutionTemplateQueryService,
      IExecutionQueryService<WorkflowRunSummary, WorkflowRunReport>
{
    private readonly IWorkflowExecutionQueryApplicationService _inner;

    public WorkflowExecutionQueryServiceAdapter(IWorkflowExecutionQueryApplicationService inner)
    {
        _inner = inner;
    }

    public bool ExecutionQueryEnabled => _inner.RunQueryEnabled;

    public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
        _inner.ListAgentsAsync(ct);

    public IReadOnlyList<string> ListTemplates() => _inner.ListWorkflows();

    public Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(int take = 50, CancellationToken ct = default) =>
        _inner.ListRunsAsync(take, ct);

    public Task<WorkflowRunReport?> GetAsync(string executionId, CancellationToken ct = default) =>
        _inner.GetRunAsync(executionId, ct);
}
