using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class ProjectionWorkflowActorBindingReader : IWorkflowActorBindingReader, IWorkflowRunBindingReader
{
    private readonly Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>> _getDocumentAsync;
    private readonly Func<ProjectionDocumentQuery, CancellationToken, Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>>> _queryDocumentsAsync;

    public ProjectionWorkflowActorBindingReader(
        IProjectionDocumentReader<WorkflowActorBindingDocument, string> documentStore)
    {
        ArgumentNullException.ThrowIfNull(documentStore);

        _getDocumentAsync = (actorId, ct) => documentStore.GetAsync(actorId, ct);
        _queryDocumentsAsync = documentStore.QueryAsync;
    }

    internal ProjectionWorkflowActorBindingReader(
        Func<string, CancellationToken, Task<WorkflowActorBindingDocument?>> getDocumentAsync,
        Func<ProjectionDocumentQuery, CancellationToken, Task<ProjectionDocumentQueryResult<WorkflowActorBindingDocument>>> queryDocumentsAsync)
    {
        _getDocumentAsync = getDocumentAsync ?? throw new ArgumentNullException(nameof(getDocumentAsync));
        _queryDocumentsAsync = queryDocumentsAsync ?? throw new ArgumentNullException(nameof(queryDocumentsAsync));
    }

    public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();

        // Refactor (iter56/cluster-925-binding-query-readmodel-only): old=runtime existence/type fallback, new=readmodel-only
        var document = await _getDocumentAsync(actorId, ct);
        return document == null
            ? null
            : MapDocument(document, actorId);
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
            if (string.IsNullOrWhiteSpace(actorId))
            {
                continue;
            }

            bindings.Add(MapDocument(document, actorId));
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
        var runIds = (query.RunIds ?? [])
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (definitionActorIds.Length == 0 && runIds.Length == 0)
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

        if (definitionActorIds.Length > 0)
        {
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
        }

        if (runIds.Length > 0)
        {
            filters.Add(new ProjectionDocumentFilter
            {
                FieldPath = nameof(WorkflowActorBindingDocument.RunId),
                Operator = ProjectionDocumentFilterOperator.In,
                Value = ProjectionDocumentValue.FromStrings(runIds),
            });
        }

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
            if (string.IsNullOrWhiteSpace(actorId))
            {
                continue;
            }

            bindings.Add(MapDocument(document, actorId));
        }

        return bindings;
    }

    private static WorkflowActorBinding MapDocument(
        WorkflowActorBindingDocument document,
        string fallbackActorId)
    {
        ArgumentNullException.ThrowIfNull(document);

        var actorId = string.IsNullOrWhiteSpace(document.ActorId)
            ? fallbackActorId
            : document.ActorId;
        var actorKind = document.ActorKind;
        var definitionActorId = string.IsNullOrWhiteSpace(document.DefinitionActorId) && actorKind == WorkflowActorKind.Definition
            ? actorId
            : document.DefinitionActorId ?? string.Empty;
        var expectedExecutionMode = ResolveExpectedExecutionMode(document);

        return new WorkflowActorBinding(
            actorKind,
            actorId,
            definitionActorId,
            document.RunId ?? string.Empty,
            document.WorkflowName ?? string.Empty,
            document.WorkflowYaml ?? string.Empty,
            new Dictionary<string, string>(document.InlineWorkflowYamls, StringComparer.OrdinalIgnoreCase),
            expectedExecutionMode,
            document.ScopeId ?? string.Empty,
            document.StateVersion,
            document.LastEventId ?? string.Empty,
            document.CreatedAt,
            document.UpdatedAt,
            document.SourceKind ?? string.Empty,
            document.CapabilityAdmissionPlan?.Clone(),
            document.WorkflowId ?? string.Empty,
            document.RevisionId ?? string.Empty,
            document.ToolCatalogPolicyVersion ?? string.Empty,
            document.CatalogPublicationContractVersion ?? string.Empty);
    }

    private static ExternalCapabilityExecutionMode ResolveExpectedExecutionMode(
        WorkflowActorBindingDocument document)
    {
        if (document.ExpectedExecutionMode != ExternalCapabilityExecutionMode.Unspecified)
            return document.ExpectedExecutionMode;

        return document.CapabilityAdmissionPlan?.ExecutionMode ?? ExternalCapabilityExecutionMode.Unspecified;
    }
}
