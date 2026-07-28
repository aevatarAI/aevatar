using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Infrastructure.Schedules.Authorization;

public sealed class ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort
    : INyxIdAuthorizationCatalogVersionRegressionStorePort
{
    private readonly IEventStore _eventStore;
    private readonly IElasticsearchProjectionDocumentRepairStore<
        NyxIdAuthorizationCatalogDocument,
        string> _repairStore;

    public ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort(
        IEventStore eventStore,
        IElasticsearchProjectionDocumentRepairStore<
            NyxIdAuthorizationCatalogDocument,
            string> repairStore)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _repairStore = repairStore ?? throw new ArgumentNullException(nameof(repairStore));
    }

    public async Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
        string verifiedOwnerSubject,
        CancellationToken ct = default)
    {
        var normalizedSubject = NormalizeSubject(verifiedOwnerSubject);
        var actorId = BuildActorId(normalizedSubject);
        var sourceVersion = await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false);
        var lease = await _repairStore.InspectAsync(actorId, ct).ConfigureAwait(false);
        var document = lease?.Document;
        return new NyxIdAuthorizationCatalogVersionRegressionInspection(
            normalizedSubject,
            actorId,
            sourceVersion,
            document?.StateVersion,
            document?.LastEventId ?? string.Empty,
            document?.ActorId ?? string.Empty,
            Repairable: false,
            Detail: string.Empty);
    }

    public async Task<NyxIdAuthorizationCatalogReplicaDeleteDisposition> DeleteIfMatchesAsync(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
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

        var normalizedSubject = NormalizeSubject(request.VerifiedOwnerSubject);
        var actorId = BuildActorId(normalizedSubject);
        if (!string.Equals(request.ExpectedActorId, actorId, StringComparison.Ordinal))
            return NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged;

        var sourceVersion = await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false);
        if (sourceVersion != request.ExpectedSourceStateVersion)
            return NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged;

        var lease = await _repairStore.InspectAsync(actorId, ct).ConfigureAwait(false);
        if (lease is null)
            return NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent;
        if (!FingerprintMatches(lease.Document, actorId, request))
            return NyxIdAuthorizationCatalogReplicaDeleteDisposition.DocumentChanged;

        sourceVersion = await _eventStore.GetVersionAsync(actorId, ct).ConfigureAwait(false);
        if (sourceVersion != request.ExpectedSourceStateVersion)
            return NyxIdAuthorizationCatalogReplicaDeleteDisposition.SourceChanged;

        var deleteDisposition = await _repairStore
            .DeleteIfUnchangedAsync(lease, ct)
            .ConfigureAwait(false);
        return deleteDisposition switch
        {
            ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted =>
                NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted,
            ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent =>
                NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent,
            ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict =>
                NyxIdAuthorizationCatalogReplicaDeleteDisposition.RevisionConflict,
            _ => throw new InvalidOperationException(
                $"Unsupported Elasticsearch repair delete disposition '{deleteDisposition}'."),
        };
    }

    private static string BuildActorId(string normalizedSubject)
    {
        var owner = new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Personal,
            OwnerSubject = normalizedSubject,
        };
        return NyxIdAuthorizationCatalogActorIds.Build(owner);
    }

    private static string NormalizeSubject(string verifiedOwnerSubject)
    {
        var normalized = verifiedOwnerSubject?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Verified owner subject is required.",
                nameof(verifiedOwnerSubject));
        }

        return normalized;
    }

    private static bool FingerprintMatches(
        NyxIdAuthorizationCatalogDocument document,
        string actorId,
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request) =>
        string.Equals(document.Id, actorId, StringComparison.Ordinal) &&
        string.Equals(document.ActorId, actorId, StringComparison.Ordinal) &&
        document.StateVersion == request.ExpectedDocumentStateVersion &&
        string.Equals(
            document.LastEventId,
            request.ExpectedDocumentLastEventId,
            StringComparison.Ordinal);
}
