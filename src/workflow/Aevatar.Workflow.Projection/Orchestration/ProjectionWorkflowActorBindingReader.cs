using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class ProjectionWorkflowActorBindingReader : IWorkflowActorBindingReader, IWorkflowRunBindingReader
{
    private readonly Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>> _getDocumentAsync;
    private readonly Func<ProjectionDocumentQuery, CancellationToken, Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>>> _queryDocumentsAsync;
    private readonly Func<string, Task<bool>> _existsAsync;
    private readonly Func<string, Type, CancellationToken, Task<bool>> _isExpectedAsync;

    public ProjectionWorkflowActorBindingReader(
        IProjectionDocumentReader<WorkflowActorBindingDocument, string> documentStore,
        IActorRuntime runtime,
        IAgentTypeVerifier agentTypeVerifier)
    {
        ArgumentNullException.ThrowIfNull(documentStore);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(agentTypeVerifier);

        _getDocumentAsync = (actorId, ct) => documentStore.GetAsync(actorId, ct);
        _queryDocumentsAsync = documentStore.QueryAsync;
        _existsAsync = runtime.ExistsAsync;
        _isExpectedAsync = agentTypeVerifier.IsExpectedAsync;
    }

    internal ProjectionWorkflowActorBindingReader(
        Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>> getDocumentAsync,
        Func<ProjectionDocumentQuery, CancellationToken, Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>>> queryDocumentsAsync,
        Func<string, Task<bool>> existsAsync,
        Func<string, Type, CancellationToken, Task<bool>> isExpectedAsync)
    {
        _getDocumentAsync = getDocumentAsync ?? throw new ArgumentNullException(nameof(getDocumentAsync));
        _queryDocumentsAsync = queryDocumentsAsync ?? throw new ArgumentNullException(nameof(queryDocumentsAsync));
        _existsAsync = existsAsync ?? throw new ArgumentNullException(nameof(existsAsync));
        _isExpectedAsync = isExpectedAsync ?? throw new ArgumentNullException(nameof(isExpectedAsync));
    }

    public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        if (!await _existsAsync(actorId))
            return null;

        var actorKind = await ResolveActorKindAsync(actorId, ct);
        if (actorKind == WorkflowActorKind.Unsupported)
            return WorkflowActorBinding.Unsupported(actorId);

        var document = await _getDocumentAsync(actorId, ct);
        return document == null
            ? CreateUnboundBinding(actorId, actorKind)
            : MapDocument(document, actorId, actorKind);
    }

    public async Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
        string runId,
        int take = 20,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ct.ThrowIfCancellationRequested();

        var normalizedRunId = WorkflowRunIdNormalizer.Normalize(runId);
        var boundedTake = Math.Clamp(take, 1, 100);
        var result = await _queryDocumentsAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(WorkflowActorBindingDocument.RunId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(normalizedRunId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(WorkflowActorBindingDocument.ActorKindValue),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromInt64((int)WorkflowActorKind.Run),
                    },
                ],
            },
            ct);

        if (result.Items.Count == 0)
            return [];

        var bindings = new List<WorkflowActorBinding>(result.Items.Count);
        foreach (var document in result.Items)
        {
            var actorId = document.ActorId?.Trim();
            if (string.IsNullOrWhiteSpace(actorId) ||
                !await _existsAsync(actorId) ||
                !await _isExpectedAsync(actorId, typeof(WorkflowRunGAgent), ct))
            {
                continue;
            }

            bindings.Add(MapDocument(document, actorId, WorkflowActorKind.Run));
        }

        return bindings;
    }

    public async Task<IReadOnlyList<WorkflowActorBinding>> QueryAsync(
        WorkflowRunBindingQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();

        var normalizedScopeId = (query.ScopeId ?? string.Empty).Trim();
        var definitionActorIds = (query.DefinitionActorIds ?? [])
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (definitionActorIds.Length == 0)
            return [];

        var boundedTake = Math.Clamp(query.Take, 1, 200);
        var filters = new List<ProjectionDocumentFilter>
        {
            new()
            {
                FieldPath = nameof(WorkflowActorBindingDocument.ActorKindValue),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromInt64((int)WorkflowActorKind.Run),
            },
        };
        if (!string.IsNullOrWhiteSpace(normalizedScopeId))
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowActorBindingDocument.ScopeId),
                Operator = ProjectionDocumentFilterOperator.Eq,
                Value = ProjectionDocumentValue.FromString(normalizedScopeId),
            });
        }

        filters.Add(new ProjectionDocumentFilter
        {
            FieldPath = nameof(WorkflowActorBindingDocument.DefinitionActorId),
            Operator = definitionActorIds.Length == 1
                ? ProjectionDocumentFilterOperator.Eq
                : ProjectionDocumentFilterOperator.In,
            Value = definitionActorIds.Length == 1
                ? ProjectionDocumentValue.FromString(definitionActorIds[0])
                : ProjectionDocumentValue.FromStrings(definitionActorIds),
        });

        var result = await _queryDocumentsAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
                Filters = filters,
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(WorkflowActorBindingDocument.UpdatedAt),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(WorkflowActorBindingDocument.ActorId),
                        Direction = ProjectionDocumentSortDirection.Asc,
                    },
                ],
            },
            ct);

        if (result.Items.Count == 0)
            return [];

        var bindings = new List<WorkflowActorBinding>(result.Items.Count);
        foreach (var document in result.Items)
        {
            var actorId = document.ActorId?.Trim();
            if (string.IsNullOrWhiteSpace(actorId) ||
                !await _existsAsync(actorId) ||
                !await _isExpectedAsync(actorId, typeof(WorkflowRunGAgent), ct))
            {
                continue;
            }

            bindings.Add(MapDocument(document, actorId, WorkflowActorKind.Run));
        }

        return bindings;
    }

    private async Task<WorkflowActorKind> ResolveActorKindAsync(string actorId, CancellationToken ct)
    {
        if (await _isExpectedAsync(actorId, typeof(WorkflowGAgent), ct))
            return WorkflowActorKind.Definition;
        if (await _isExpectedAsync(actorId, typeof(WorkflowRunGAgent), ct))
            return WorkflowActorKind.Run;

        return WorkflowActorKind.Unsupported;
    }

    private static WorkflowActorBinding CreateUnboundBinding(string actorId, WorkflowActorKind actorKind) =>
        new(
            actorKind,
            actorId,
            actorKind == WorkflowActorKind.Definition ? actorId : string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            string.Empty);

    private static WorkflowActorBinding MapDocument(
        WorkflowActorBindingDocument document,
        string fallbackActorId,
        WorkflowActorKind fallbackActorKind)
    {
        ArgumentNullException.ThrowIfNull(document);

        var actorId = string.IsNullOrWhiteSpace(document.ActorId)
            ? fallbackActorId
            : document.ActorId;
        var actorKind = document.ActorKind == WorkflowActorKind.Unsupported
            ? fallbackActorKind
            : document.ActorKind;
        var definitionActorId = string.IsNullOrWhiteSpace(document.DefinitionActorId) && actorKind == WorkflowActorKind.Definition
            ? actorId
            : document.DefinitionActorId ?? string.Empty;

        return new WorkflowActorBinding(
            actorKind,
            actorId,
            definitionActorId,
            document.RunId ?? string.Empty,
            document.WorkflowName ?? string.Empty,
            document.WorkflowYaml ?? string.Empty,
            new Dictionary<string, string>(document.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase),
            document.ScopeId ?? string.Empty,
            document.StateVersion,
            document.LastEventId ?? string.Empty,
            document.CreatedAt,
            document.UpdatedAt);
    }
}
