using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Abstractions;
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
        CancellationToken ct = default) =>
        await ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = take,
            },
            ct);

    public async Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default)
    {
        var page = await PageWorkflowActorCurrentStatesAsync(query, ct);
        return page.Items;
    }

    public async Task<WorkflowActorCurrentStatePage> PageWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default)
    {
        if (!_workflowRunCurrentStateQueryEnabled)
            return new WorkflowActorCurrentStatePage([], null, null);

        ArgumentNullException.ThrowIfNull(query);
        var boundedTake = Math.Clamp(query.Take, 1, 1000);
        var currentStates = await _currentStateReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
                Filters = BuildFilters(query),
                AnyOfFilters = BuildActivitySearchFilters(query),
                Sorts = RecencyDescendingSort,
                Cursor = query.Cursor,
                IncludeTotalCount = query.IncludeTotalCount,
            },
            ct);
        var snapshots = new List<WorkflowActorSnapshot>(currentStates.Items.Count);
        foreach (var currentState in currentStates.Items)
        {
            snapshots.Add(_mapper.ToActorSnapshot(currentState));
        }

        return new WorkflowActorCurrentStatePage(
            snapshots,
            currentStates.NextCursor,
            currentStates.TotalCount);
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

    // Current-state lists (observatory own-scope + cross-scope overview, query service, tools) order by
    // most-recent activity. Without an explicit sort the Elasticsearch store falls back to a non-existent
    // default sort field ("CreatedAt") and degrades to the actor-id tiebreak order, so a bounded Take
    // returns an arbitrary subset and recent runs are dropped from cross-scope pages (06-23 observatory bug).
    private static readonly IReadOnlyList<ProjectionDocumentSort> RecencyDescendingSort =
    [
        new ProjectionDocumentSort
        {
            FieldPath = nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue),
            Direction = ProjectionDocumentSortDirection.Desc,
        },
        new ProjectionDocumentSort
        {
            FieldPath = nameof(WorkflowExecutionCurrentStateDocument.RootActorId),
            Direction = ProjectionDocumentSortDirection.Asc,
        },
    ];

    private static readonly IReadOnlyList<string> ActivitySearchFieldPaths =
    [
        nameof(WorkflowExecutionCurrentStateDocument.WorkflowName),
        nameof(WorkflowExecutionCurrentStateDocument.RunId),
        nameof(WorkflowExecutionCurrentStateDocument.Status),
        nameof(WorkflowExecutionCurrentStateDocument.InputSummary),
        nameof(WorkflowExecutionCurrentStateDocument.ActivityInitiator) + "." + nameof(WorkflowRunActivityInitiatorSnapshot.DisplayValue),
    ];

    private static IReadOnlyList<ProjectionDocumentFilter> BuildActivitySearchFilters(WorkflowActorCurrentStateListQuery query)
    {
        var searchText = query.SearchText.Trim();
        if (searchText.Length == 0)
            return [];

        return ActivitySearchFieldPaths
            .Select(fieldPath => new ProjectionDocumentFilter
            {
                FieldPath = fieldPath,
                Operator = ProjectionDocumentFilterOperator.ContainsText,
                Value = ProjectionDocumentValue.FromString(searchText),
            })
            .ToArray();
    }

    private static IReadOnlyList<ProjectionDocumentFilter> BuildFilters(WorkflowActorCurrentStateListQuery query)
    {
        var filters = new List<ProjectionDocumentFilter>();
        if (query.SagaStatus is { } sagaStatus && sagaStatus != WorkflowSagaStatus.Unspecified)
        {
            // saga_status is persisted in its protobuf-JSON form (WORKFLOW_SAGA_STATUS_*) and mapped
            // as an explicit keyword; the filter value must carry that same form, not the C# name.
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.SagaStatus),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromProtoEnum(sagaStatus),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.ScopeId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.ScopeId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.ScopeId.Trim()),
            });
        }

        var definitionActorIds = query.DefinitionActorIds
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (definitionActorIds.Length == 1)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.DefinitionActorId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(definitionActorIds[0]),
            });
        }
        else if (definitionActorIds.Length > 1)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.DefinitionActorId),
                Operator = ProjectionDocumentFilterOperator.In,
                Value = ProjectionDocumentValue.FromStrings(definitionActorIds),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.Status),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.Status.Trim()),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.RunId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.RunId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.RunId.Trim()),
            });
        }

        if (!string.IsNullOrWhiteSpace(query.WorkflowId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.WorkflowId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(query.WorkflowId.Trim()),
            });
        }

        var runOrigins = query.RunOrigins
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (runOrigins.Length == 1)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.RunOrigin),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(runOrigins[0]),
            });
        }
        else if (runOrigins.Length > 1)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.RunOrigin),
                Operator = ProjectionDocumentFilterOperator.In,
                Value = ProjectionDocumentValue.FromStrings(runOrigins),
            });
        }

        var scheduleIds = query.ScheduleIds
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (scheduleIds.Length == 1)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.ScheduleId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(scheduleIds[0]),
            });
        }
        else if (scheduleIds.Length > 1)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.ScheduleId),
                Operator = ProjectionDocumentFilterOperator.In,
                Value = ProjectionDocumentValue.FromStrings(scheduleIds),
            });
        }

        if (query.UpdatedFromUtc is { } updatedFrom)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue),
                Operator = ProjectionDocumentFilterOperator.Gte,
                Value = ProjectionDocumentValue.FromString(updatedFrom.UtcDateTime.ToString("O")),
            });
        }

        if (query.UpdatedToUtc is { } updatedTo)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowExecutionCurrentStateDocument.UpdatedAtUtcValue),
                Operator = ProjectionDocumentFilterOperator.Lte,
                Value = ProjectionDocumentValue.FromString(updatedTo.UtcDateTime.ToString("O")),
            });
        }

        return filters;
    }
}
