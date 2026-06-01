using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
{
    private readonly IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> _currentStateReader;
    private readonly WorkflowExecutionReadModelMapper _mapper;
    private readonly bool _workflowRunCurrentStateQueryEnabled;

    // Refactor (iter29/cluster-029-workflow-history-artifact):
    //   Old pattern: workflow history / report / graph are treated as current-state readmodels (current-state query path enriches actor snapshots by reading report artifacts; duplicate WorkflowRunTimelineDocument and WorkflowRunGraphArtifactDocument shells copy WorkflowRunInsightReportDocument; public application/query/tool/HTTP surfaces expose them as actor current-state queries instead of workflow-run artifacts)
    //   New principle: Workflow history / report / graph are workflow-run artifacts (or aggregate-owned views), NOT actor current-state readmodels: keep existing WorkflowRunInsightReportDocument adapter/name workflow-local as the single report artifact source; delete duplicate WorkflowRunTimelineDocument / WorkflowRunGraphArtifactDocument shells (timeline derived from report artifact, graph materialization derived from report artifact); stop current-state query paths from reading report/history artifacts to enrich actor snapshots; rename public application/query/tool/HTTP surfaces so report/timeline/graph are explicit workflow-run artifact / export, not current-state readmodel surfaces; WorkflowExecutionCurrentStateDocument remains the only workflow actor-scoped current-state readmodel; NO CLAUDE.md change, NO new core abstraction, NO generic CQRS Projection artifact storage seam, NO new actor type
    //   New pattern: workflow history/report/graph are artifacts or aggregate-owned views, not current-state readmodels.
    public WorkflowExecutionCurrentStateQueryPort(
        IProjectionDocumentReader<WorkflowExecutionCurrentStateDocument, string> currentStateReader,
        WorkflowExecutionReadModelMapper mapper,
        WorkflowExecutionProjectionOptions? options = null)
    {
        _currentStateReader = currentStateReader ?? throw new ArgumentNullException(nameof(currentStateReader));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _workflowRunCurrentStateQueryEnabled = options == null || (options.Enabled && options.WorkflowActorCurrentStateQueryEnabled);
    }

    public bool WorkflowActorCurrentStateQueryEnabled => _workflowRunCurrentStateQueryEnabled;

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: projection port exposed actor snapshot lookup by actorId.
    //   New principle: projection port exposes workflow actor current-state lookup while still reading the actor-scoped document key.
    public async Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!_workflowRunCurrentStateQueryEnabled || string.IsNullOrWhiteSpace(actorId))
            return null;

        var currentState = await _currentStateReader.GetAsync(actorId, ct);
        if (currentState == null)
            return null;

        return _mapper.ToActorSnapshot(currentState);
    }

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        if (!_workflowRunCurrentStateQueryEnabled)
            return [];

        var boundedTake = Math.Clamp(take, 1, 1000);
        var currentStates = await _currentStateReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
            },
            ct);
        var snapshots = new List<WorkflowActorSnapshot>(currentStates.Items.Count);
        foreach (var currentState in currentStates.Items)
        {
            snapshots.Add(_mapper.ToActorSnapshot(currentState));
        }

        return snapshots;
    }

    public async Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        if (!_workflowRunCurrentStateQueryEnabled || string.IsNullOrWhiteSpace(actorId))
            return null;

        var currentState = await _currentStateReader.GetAsync(actorId, ct);
        return currentState == null ? null : _mapper.ToActorProjectionState(currentState);
    }
}
