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
    private readonly ScopeWorkflowCatalogueRowMaterializer _catalogueRowMaterializer;
    private readonly IWorkflowYamlDocumentService _yamlDocumentService;
    private readonly IProjectionClock _clock;

    public StudioWorkspaceCurrentStateProjector(
        IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument> writeDispatcher,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueSourceDocument> catalogueWriteDispatcher,
        ScopeWorkflowCatalogueRowMaterializer catalogueRowMaterializer,
        IWorkflowYamlDocumentService yamlDocumentService,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _catalogueWriteDispatcher = catalogueWriteDispatcher ?? throw new ArgumentNullException(nameof(catalogueWriteDispatcher));
        _catalogueRowMaterializer = catalogueRowMaterializer ?? throw new ArgumentNullException(nameof(catalogueRowMaterializer));
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
        await MaterializeCatalogueDraftSourceAsync(state, stateEvent.EventId ?? string.Empty, updatedAt, stateEvent.EventData, ct);
    }

    private async Task MaterializeCatalogueDraftSourceAsync(
        StudioWorkspaceState state,
        string eventId,
        DateTimeOffset updatedAt,
        Any eventData,
        CancellationToken ct)
    {
        if (eventData.Is(StudioWorkflowDraftSaved.Descriptor))
        {
            var saved = eventData.Unpack<StudioWorkflowDraftSaved>();
            if (saved.Draft != null && !string.IsNullOrWhiteSpace(saved.Draft.WorkflowId))
            {
                await _catalogueWriteDispatcher.UpsertAsync(
                    ToCatalogueDraftSource(state.ScopeId, saved.Draft, eventId, updatedAt),
                    ct);
                await _catalogueRowMaterializer.RefreshAsync(
                    state.ScopeId,
                    saved.Draft.WorkflowId,
                    eventId,
                    updatedAt,
                    ct);
            }

            return;
        }

        if (eventData.Is(StudioWorkflowDraftDeleted.Descriptor))
        {
            var deleted = eventData.Unpack<StudioWorkflowDraftDeleted>();
            if (!string.IsNullOrWhiteSpace(deleted.WorkflowId))
            {
                await _catalogueWriteDispatcher.DeleteAsync(
                    new ProjectionDocumentDeleteMarker(
                        ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceDocumentId(state.ScopeId, deleted.WorkflowId),
                        ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceActorId(state.ScopeId, deleted.WorkflowId),
                        ScopeWorkflowCatalogueRowMaterializer.BuildSourceDeleteStateVersion(updatedAt),
                        eventId,
                        updatedAt),
                    ct);
                await _catalogueRowMaterializer.RefreshAsync(
                    state.ScopeId,
                    deleted.WorkflowId,
                    eventId,
                    updatedAt,
                    ct);
            }
        }
    }

    private ScopeWorkflowCatalogueSourceDocument ToCatalogueDraftSource(
        string scopeId,
        StudioWorkflowDraft draft,
        string eventId,
        DateTimeOffset projectedAt)
    {
        var parse = _yamlDocumentService.Parse(draft.Yaml);
        var name = parse.Document?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(draft.Name) ? draft.WorkflowId : draft.Name.Trim();

        var sourceUpdatedAt = draft.UpdatedAtUtc?.ToDateTimeOffset() ?? projectedAt;

        return new ScopeWorkflowCatalogueSourceDocument
        {
            Id = ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceDocumentId(scopeId, draft.WorkflowId),
            ActorId = ScopeWorkflowCatalogueRowMaterializer.BuildDraftSourceActorId(scopeId, draft.WorkflowId),
            StateVersion = ScopeWorkflowCatalogueRowMaterializer.BuildSourceStateVersion(sourceUpdatedAt),
            LastEventId = eventId,
            UpdatedAt = Timestamp.FromDateTimeOffset(projectedAt),
            ScopeId = scopeId,
            WorkflowId = draft.WorkflowId,
            SourceKind = ScopeWorkflowCatalogueSourceDocument.DraftSourceKind,
            Name = name,
            Description = parse.Document?.Description ?? string.Empty,
            SourceUpdatedAtUtc = sourceUpdatedAt,
        };
    }

    private static StudioWorkspaceDraftSummary ToDraftSummary(StudioWorkflowDraft draft) => new()
    {
        WorkflowId = draft.WorkflowId,
        Name = draft.Name,
        FileName = draft.FileName,
        DirectoryId = draft.DirectoryId,
        Version = draft.Version,
    };
}
