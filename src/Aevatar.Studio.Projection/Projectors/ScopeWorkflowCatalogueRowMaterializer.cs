using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.ScopeWorkflows;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Projection.Projectors;

public sealed class ScopeWorkflowCatalogueRowMaterializer
{
    private readonly IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> _sourceReader;
    private readonly IScopeWorkflowCatalogueRowCommandPort _rowCommandPort;

    public ScopeWorkflowCatalogueRowMaterializer(
        IProjectionDocumentReader<ScopeWorkflowCatalogueSourceDocument, string> sourceReader,
        IScopeWorkflowCatalogueRowCommandPort rowCommandPort)
    {
        _sourceReader = sourceReader ?? throw new ArgumentNullException(nameof(sourceReader));
        _rowCommandPort = rowCommandPort ?? throw new ArgumentNullException(nameof(rowCommandPort));
    }

    public async Task RefreshAsync(
        string scopeId,
        string workflowId,
        string eventId,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(workflowId))
            return;

        var draft = await _sourceReader.GetAsync(BuildDraftSourceDocumentId(scopeId, workflowId), ct);
        var service = await _sourceReader.GetAsync(BuildServiceSourceDocumentId(scopeId, workflowId), ct);
        await _rowCommandPort.ObserveSourcesAsync(
            scopeId,
            workflowId,
            ToSnapshot(draft),
            ToSnapshot(service),
            draft?.SourceUpdatedAtUtc ?? updatedAt,
            service?.SourceUpdatedAtUtc ?? updatedAt,
            eventId,
            updatedAt,
            ct);
    }

    public static string BuildDraftSourceDocumentId(string scopeId, string workflowId) =>
        ScopeWorkflowCatalogueActorIds.SourceDocument(scopeId, workflowId, ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);

    public static string BuildServiceSourceDocumentId(string scopeId, string workflowId) =>
        ScopeWorkflowCatalogueActorIds.SourceDocument(scopeId, workflowId, ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);

    public static string BuildDraftSourceActorId(string scopeId, string workflowId) =>
        ScopeWorkflowCatalogueActorIds.SourceActor(scopeId, workflowId, ScopeWorkflowCatalogueSourceDocument.DraftSourceKind);

    public static string BuildServiceSourceActorId(string scopeId, string workflowId) =>
        ScopeWorkflowCatalogueActorIds.SourceActor(scopeId, workflowId, ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind);

    public static long BuildSourceStateVersion(DateTimeOffset updatedAt) =>
        ToWatermarkStateVersion(updatedAt);

    public static long BuildSourceDeleteStateVersion(DateTimeOffset updatedAt) =>
        ToWatermarkStateVersion(updatedAt) + 1;

    public static string BuildRowDocumentId(string scopeId, string workflowId) =>
        ScopeWorkflowCatalogueActorIds.RowDocument(scopeId, workflowId);

    public static string BuildRowActorId(string scopeId, string workflowId) =>
        ScopeWorkflowCatalogueActorIds.Row(scopeId, workflowId);

    internal static ScopeWorkflowCatalogueRowDocument ToRowDocument(
        string actorId,
        long stateVersion,
        ScopeWorkflowCatalogueRowState state)
    {
        var rowUpdatedAt = ResolveRowUpdatedAt(state.DraftSource, state.ServiceSource);
        return new ScopeWorkflowCatalogueRowDocument
        {
            Id = BuildRowDocumentId(state.ScopeId, state.WorkflowId),
            ActorId = actorId,
            StateVersion = stateVersion,
            LastEventId = state.LastEventId ?? string.Empty,
            UpdatedAt = state.ObservedAt?.Clone(),
            ScopeId = state.ScopeId,
            WorkflowId = state.WorkflowId,
            Name = ResolveName(state.WorkflowId, state.DraftSource, state.ServiceSource),
            Description = state.DraftSource?.Description ?? state.ServiceSource?.Description ?? string.Empty,
            HasDraftSource = state.DraftSource != null,
            HasPublishedSource = state.ServiceSource != null,
            RowUpdatedAtUtc = rowUpdatedAt,
            UpdatedAtSource = ResolveUpdatedAtSource(rowUpdatedAt, state.DraftSource, state.ServiceSource),
            SourceWatermarkUtc = rowUpdatedAt,
            ServiceKey = state.ServiceSource?.ServiceKey ?? string.Empty,
            WorkflowName = state.ServiceSource?.WorkflowName ?? string.Empty,
            CommittedActorId = state.ServiceSource?.CommittedActorId ?? string.Empty,
            ActiveRevisionId = state.ServiceSource?.ActiveRevisionId ?? string.Empty,
            DeploymentId = state.ServiceSource?.DeploymentId ?? string.Empty,
            DeploymentStatus = state.ServiceSource?.DeploymentStatus ?? string.Empty,
            ServiceAppId = state.ServiceSource?.ServiceAppId ?? string.Empty,
            ServiceNamespace = state.ServiceSource?.ServiceNamespace ?? string.Empty,
            PublishedServiceId = state.ServiceSource?.PublishedServiceId ?? string.Empty,
        };
    }

    internal static ProjectionDocumentDeleteMarker ToDeleteMarker(
        string actorId,
        long stateVersion,
        ScopeWorkflowCatalogueRowState state) =>
        new(
            BuildRowDocumentId(state.ScopeId, state.WorkflowId),
            actorId,
            stateVersion,
            state.LastEventId ?? string.Empty,
            state.ObservedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue);

    private static ScopeWorkflowCatalogueSourceSnapshot? ToSnapshot(ScopeWorkflowCatalogueSourceDocument? source)
    {
        if (source == null)
            return null;

        return new ScopeWorkflowCatalogueSourceSnapshot
        {
            SourceKind = source.SourceKind ?? string.Empty,
            Name = source.Name ?? string.Empty,
            Description = source.Description ?? string.Empty,
            SourceUpdatedAtUtc = Timestamp.FromDateTimeOffset(source.SourceUpdatedAtUtc),
            LastEventId = source.LastEventId ?? string.Empty,
            ObservedAt = source.UpdatedAt?.Clone(),
            ServiceKey = source.ServiceKey ?? string.Empty,
            WorkflowName = source.WorkflowName ?? string.Empty,
            CommittedActorId = source.CommittedActorId ?? string.Empty,
            ActiveRevisionId = source.ActiveRevisionId ?? string.Empty,
            DeploymentId = source.DeploymentId ?? string.Empty,
            DeploymentStatus = source.DeploymentStatus ?? string.Empty,
            PublishedServiceId = source.PublishedServiceId ?? string.Empty,
            ServiceAppId = source.ServiceAppId ?? string.Empty,
            ServiceNamespace = source.ServiceNamespace ?? string.Empty,
        };
    }

    private static long ToWatermarkStateVersion(DateTimeOffset updatedAt) =>
        Math.Max(1L, updatedAt.UtcDateTime.Ticks);

    private static DateTimeOffset ResolveRowUpdatedAt(
        ScopeWorkflowCatalogueSourceSnapshot? draft,
        ScopeWorkflowCatalogueSourceSnapshot? service) =>
        new[] { draft, service }
            .Where(static source => source != null)
            .Select(static source => source!.SourceUpdatedAtUtc?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

    private static string ResolveUpdatedAtSource(
        DateTimeOffset rowUpdatedAt,
        ScopeWorkflowCatalogueSourceSnapshot? draft,
        ScopeWorkflowCatalogueSourceSnapshot? service)
    {
        if (service != null && service.SourceUpdatedAtUtc?.ToDateTimeOffset() == rowUpdatedAt)
            return ScopeWorkflowCatalogueSourceDocument.ServiceSourceKind;

        return ScopeWorkflowCatalogueSourceDocument.DraftSourceKind;
    }

    private static string ResolveName(
        string workflowId,
        ScopeWorkflowCatalogueSourceSnapshot? draft,
        ScopeWorkflowCatalogueSourceSnapshot? service)
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
