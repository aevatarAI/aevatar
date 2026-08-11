using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Workspace;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

// Refactor (iter16/cluster-meta-studio-actor-substrate):
//   Old: workspace current state was assembled from a local file-backed store outside the unified projection path.
//   New principle: this projector consumes committed workspace actor state and materializes the single query replica.
public sealed class StudioWorkspaceCurrentStateProjector
    : ICurrentStateProjectionMaterializer<StudioMaterializationContext>
{
    private static readonly JsonFormatter StateRootFormatter = new(
        JsonFormatter.Settings.Default
            .WithPreserveProtoFieldNames(true)
            .WithFormatDefaultValues(true));

    private readonly IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> _catalogueWriteDispatcher;
    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _catalogueDocumentReader;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly IProjectionClock _clock;

    public StudioWorkspaceCurrentStateProjector(
        IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument> writeDispatcher,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> catalogueDocumentReader,
        IWorkflowYamlDocumentService yamlDocumentService,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _catalogueWriteDispatcher = catalogueWriteDispatcher ?? throw new ArgumentNullException(nameof(catalogueWriteDispatcher));
        _catalogueDocumentReader = catalogueDocumentReader ?? throw new ArgumentNullException(nameof(catalogueDocumentReader));
        _yamlDocumentService = yamlDocumentService ?? throw new ArgumentNullException(nameof(yamlDocumentService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        StudioMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<StudioWorkspaceState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent?.EventData == null ||
            state == null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var document = new StudioWorkspaceCurrentStateDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTimeOffset(updatedAt),
            StateRootJson = StateRootFormatter.Format(state),
        };
        document.DraftSummaries.AddRange(state.Drafts.Values.Select(ToDraftSummary));

        await _writeDispatcher.UpsertAsync(document, ct);
        await MaterializeCatalogueDraftSourcesAsync(context, state, stateEvent.Version, stateEvent.EventId ?? string.Empty, updatedAt, ct);
    }

    private async Task MaterializeCatalogueDraftSourcesAsync(
        StudioMaterializationContext context,
        StudioWorkspaceState state,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        var currentWorkflowIds = state.Drafts.Values
            .Select(static draft => draft.WorkflowId)
            .Where(static workflowId => !string.IsNullOrWhiteSpace(workflowId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var draft in state.Drafts.Values)
        {
            if (string.IsNullOrWhiteSpace(draft.WorkflowId))
                continue;

            await _catalogueWriteDispatcher.UpsertAsync(
                ToCatalogueDraftSource(context.RootActorId, state.ScopeId, draft, stateVersion, eventId, updatedAt),
                ct);
        }

        var existingDraftSources = await _catalogueDocumentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 10_000,
                Filters =
                [
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ScopeWorkflowCatalogueSourceDocument.ScopeId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(state.ScopeId),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ScopeWorkflowCatalogueSourceDocument.SourceKind),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(ScopeWorkflowCatalogueSourceDocument.DraftSourceKind),
                    },
                    new ProjectionDocumentFilter
                    {
                        FieldPath = nameof(ScopeWorkflowCatalogueSourceDocument.ActorId),
                        Operator = ProjectionDocumentFilterOperator.Eq,
                        Value = ProjectionDocumentValue.FromString(context.RootActorId),
                    },
                ],
            },
            ct);

        foreach (var existing in existingDraftSources.Items)
        {
            if (currentWorkflowIds.Contains(existing.WorkflowId))
                continue;

            await _catalogueWriteDispatcher.DeleteAsync(
                new ProjectionDocumentDeleteMarker(
                    existing.Id,
                    context.RootActorId,
                    stateVersion,
                    eventId,
                    updatedAt),
                ct);
        }
    }

    private ScopeWorkflowCatalogueSourceDocument ToCatalogueDraftSource(
        string actorId,
        string scopeId,
        StudioWorkflowDraft draft,
        long stateVersion,
        string eventId,
        DateTimeOffset projectedAt)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var name = parse.Document?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(draft.Name) ? draft.WorkflowId : draft.Name.Trim();

        return new ScopeWorkflowCatalogueSourceDocument
        {
            Id = BuildCatalogueSourceDocumentId(scopeId, draft.WorkflowId, ScopeWorkflowCatalogueSourceDocument.DraftSourceKind),
            ActorId = actorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            UpdatedAt = Timestamp.FromDateTimeOffset(projectedAt),
            ScopeId = scopeId,
            WorkflowId = draft.WorkflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            Name = name,
            Description = parse.Document?.Description ?? string.Empty,
            SourceUpdatedAtUtc = draft.UpdatedAtUtc?.ToDateTimeOffset() ?? projectedAt,
        };
    }

    private static string BuildCatalogueSourceDocumentId(string scopeId, string workflowId, string sourceKind) =>
        $"{scopeId}:{workflowId}:{sourceKind}";

    private static StudioWorkspaceDraftSummary ToDraftSummary(StudioWorkflowDraft draft) => new()
    {
        WorkflowId = draft.WorkflowId,
        Name = draft.Name,
        FileName = draft.FileName,
        DirectoryId = draft.DirectoryId,
        Version = draft.Version,
    };
}
