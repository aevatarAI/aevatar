using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class ScopeWorkflowCatalogueRowMaterializer
{
    private const int MaxWriteAttempts = 3;

    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _sourceReader;
    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueRowDocument, string> _rowReader;
    private readonly IProjectionWriteDispatcher<ScopeWorkflowCatalogueRowDocument> _rowWriteDispatcher;

    public ScopeWorkflowCatalogueRowMaterializer(
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> sourceReader,
        IProjectionDocumentReader<ScopeWorkflowCatalogueRowDocument, string> rowReader,
        IProjectionWriteDispatcher<ScopeWorkflowCatalogueRowDocument> rowWriteDispatcher)
    {
        _sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
        _rowReader = rowReader ?? throw new ArgumentNullException(nameof(rowReader));
        _rowWriteDispatcher = rowWriteDispatcher ?? throw new ArgumentNullException(nameof(rowWriteDispatcher));
    }

    public async Task RefreshAsync(
        string scopeId,
        string workflowId,
        string actorId,
        long stateVersion,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(workflowId))
            return;

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var rowId = BuildRowDocumentId(scopeId, workflowId);
            var existingRow = await _rowReader.GetAsync(rowId, ct);
            var draft = await _sourceReader.GetAsync(BuildDraftSourceDocumentId(scopeId, workflowId), ct);
            var service = await _sourceReader.GetAsync(BuildServiceSourceDocumentId(scopeId, workflowId), ct);
            var rowStateVersion = ResolveNextRowStateVersion(existingRow);
            ProjectionWriteResult result;
            if (draft == null && service == null)
            {
                result = await _rowWriteDispatcher.DeleteAsync(
                    new ProjectionDocumentDeleteMarker(
                        rowId,
                        BuildRowActorId(scopeId, workflowId),
                        rowStateVersion,
                        eventId,
                        updatedAt),
                    ct);
            }
            else
            {
                result = await _rowWriteDispatcher.UpsertAsync(
                    ToRowDocument(scopeId, workflowId, rowStateVersion, eventId, updatedAt, draft, service),
                    ct);
            }

            if (!result.IsRejected)
                return;
        }

        throw new InvalidOperationException(
            $"Failed to refresh scope workflow catalogue row '{BuildRowDocumentId(scopeId, workflowId)}' after {MaxWriteAttempts} attempts.");
    }

    public static string BuildDraftSourceDocumentId(string scopeId, string workflowId) =>
        $"{scopeId}:{workflowId}:{ScopeWorkflowCatalogueSourceDocument.DraftSourceKind}";

    public static string BuildServiceSourceDocumentId(string scopeId, string workflowId) =>
        $"{scopeId}:{workflowId}:{ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind}";

    public static string BuildRowDocumentId(string scopeId, string workflowId) =>
        $"{scopeId}:workflow:{workflowId}";

    public static string BuildRowActorId(string scopeId, string workflowId) =>
        $"scope-workflow-catalogue-row:{scopeId}:{workflowId}";

    private static ScopeWorkflowCatalogueRowDocument ToRowDocument(
        string scopeId,
        string workflowId,
        long rowStateVersion,
        string eventId,
        DateTimeOffset projectedAt,
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? service)
    {
        var rowUpdatedAt = ResolveRowUpdatedAt(draft, service);
        return new ScopeWorkflowCatalogueRowDocument
        {
            Id = BuildRowDocumentId(scopeId, workflowId),
            ActorId = BuildRowActorId(scopeId, workflowId),
            StateVersion = rowStateVersion,
            LastEventId = eventId,
            UpdatedAt = Timestamp.FromDateTimeOffset(projectedAt),
            ScopeId = scopeId,
            WorkflowId = workflowId,
            Name = ResolveName(workflowId, draft, service),
            Description = draft?.Description ?? service?.Description ?? string.Empty,
            HasDraftSource = draft != null,
            HasPublishedSource = service != null,
            RowUpdatedAtUtc = rowUpdatedAt,
            UpdatedAtSource = ResolveUpdatedAtSource(rowUpdatedAt, draft, service),
            SourceWatermarkUtc = rowUpdatedAt,
            ServiceKey = service?.ServiceKey ?? string.Empty,
            WorkflowName = service?.WorkflowName ?? string.Empty,
            CommittedActorId = service?.CommittedActorId ?? string.Empty,
            ActiveRevisionId = service?.ActiveRevisionId ?? string.Empty,
            DeploymentId = service?.DeploymentId ?? string.Empty,
            DeploymentStatus = service?.DeploymentStatus ?? string.Empty,
            PublishedServiceId = service?.PublishedServiceId ?? string.Empty,
        };
    }

    private static long ResolveNextRowStateVersion(ScopeWorkflowCatalogueRowDocument? existingRow) =>
        existingRow == null ? 1 : existingRow.StateVersion + 1;

    private static DateTimeOffset ResolveRowUpdatedAt(
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? service) =>
        new[] { draft, service }
            .Where(static source => source != null)
            .Select(static source => source!.SourceUpdatedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

    private static string ResolveUpdatedAtSource(
        DateTimeOffset rowUpdatedAt,
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? service)
    {
        if (service != null && service.SourceUpdatedAtUtc == rowUpdatedAt)
            return ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind;

        return ScopeWorkflowCatalogueSourceDocument.DraftSourceKind;
    }

    private static string ResolveName(
        string workflowId,
        ScopeWorkflowCatalogueSourceDocument? draft,
        ScopeWorkflowCatalogueSourceDocument? service)
    {
        if (!string.IsNullOrWhiteSpace(draft?.Name))
            return draft.Name.Trim();
        if (!string.IsNullOrWhiteSpace(service?.Name))
            return service.Name.Trim();
        if (!string.IsNullOrWhiteSpace(service?.WorkflowName))
            return service.WorkflowName.Trim();

        return workflowId;
    }
}
