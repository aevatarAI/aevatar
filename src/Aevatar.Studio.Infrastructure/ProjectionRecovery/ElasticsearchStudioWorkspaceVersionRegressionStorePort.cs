using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;

namespace Aevatar.Studio.Infrastructure.ProjectionRecovery;

public sealed class ElasticsearchStudioWorkspaceVersionRegressionStorePort
    : IStudioWorkspaceVersionRegressionStorePort
{
    private readonly IEventStore _eventStore;
    private readonly IElasticsearchProjectionDocumentRepairStore<
        StudioWorkspaceCurrentStateDocument,
        string> _repairStore;

    public ElasticsearchStudioWorkspaceVersionRegressionStorePort(
        IEventStore eventStore,
        IElasticsearchProjectionDocumentRepairStore<
            StudioWorkspaceCurrentStateDocument,
            string> repairStore)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _repairStore = repairStore ?? throw new ArgumentNullException(nameof(repairStore));
    }

    public async Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = StudioWorkspaceConventions.NormalizeScopeId(scopeId);
        var actorId = StudioWorkspaceConventions.BuildActorId(normalizedScopeId);
        var sourceVersion = await _eventStore.GetVersionAsync(actorId, ct);
        var lease = await _repairStore.InspectAsync(actorId, ct);
        var document = lease?.Document;
        return new StudioWorkspaceVersionRegressionInspection(
            normalizedScopeId,
            actorId,
            sourceVersion,
            document?.StateVersion,
            document?.LastEventId ?? string.Empty,
            document?.ActorId ?? string.Empty,
            Repairable: false,
            Detail: string.Empty);
    }

    public async Task<StudioWorkspaceReplicaDeleteDisposition> DeleteIfMatchesAsync(
        StudioWorkspaceVersionRegressionRepairRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedSourceStateVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected source state version must be positive.");
        }

        if (request.ExpectedDocumentStateVersion <= request.ExpectedSourceStateVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Expected document state version must exceed the expected source state version.");
        }

        var normalizedScopeId = StudioWorkspaceConventions.NormalizeScopeId(request.ScopeId);
        var actorId = StudioWorkspaceConventions.BuildActorId(normalizedScopeId);
        if (!string.Equals(request.ExpectedActorId, actorId, StringComparison.Ordinal))
            return StudioWorkspaceReplicaDeleteDisposition.SourceChanged;

        var sourceVersion = await _eventStore.GetVersionAsync(actorId, ct);
        if (sourceVersion != request.ExpectedSourceStateVersion)
            return StudioWorkspaceReplicaDeleteDisposition.SourceChanged;

        var lease = await _repairStore.InspectAsync(actorId, ct);
        if (lease is null)
            return StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent;
        if (!FingerprintMatches(lease.Document, actorId, request))
            return StudioWorkspaceReplicaDeleteDisposition.DocumentChanged;

        sourceVersion = await _eventStore.GetVersionAsync(actorId, ct);
        if (sourceVersion != request.ExpectedSourceStateVersion)
            return StudioWorkspaceReplicaDeleteDisposition.SourceChanged;

        var deleteDisposition = await _repairStore.DeleteIfUnchangedAsync(lease, ct);
        return deleteDisposition switch
        {
            ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted =>
                StudioWorkspaceReplicaDeleteDisposition.Deleted,
            ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent =>
                StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent,
            ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict =>
                StudioWorkspaceReplicaDeleteDisposition.RevisionConflict,
            _ => throw new InvalidOperationException(
                $"Unsupported Elasticsearch repair delete disposition '{deleteDisposition}'."),
        };
    }

    private static bool FingerprintMatches(
        StudioWorkspaceCurrentStateDocument document,
        string actorId,
        StudioWorkspaceVersionRegressionRepairRequest request) =>
        string.Equals(document.Id, actorId, StringComparison.Ordinal) &&
        string.Equals(document.ActorId, actorId, StringComparison.Ordinal) &&
        document.StateVersion == request.ExpectedDocumentStateVersion &&
        string.Equals(
            document.LastEventId,
            request.ExpectedDocumentLastEventId,
            StringComparison.Ordinal);
}
