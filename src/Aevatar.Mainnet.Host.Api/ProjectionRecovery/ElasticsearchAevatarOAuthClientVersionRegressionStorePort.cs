using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.ProjectionRecovery;

namespace Aevatar.Mainnet.Host.Api.ProjectionRecovery;

internal sealed class ElasticsearchAevatarOAuthClientVersionRegressionStorePort
    : IAevatarOAuthClientVersionRegressionStorePort
{
    private readonly IEventStore _eventStore;
    private readonly IElasticsearchProjectionDocumentRepairStore<
        AevatarOAuthClientDocument,
        string> _repairStore;

    public ElasticsearchAevatarOAuthClientVersionRegressionStorePort(
        IEventStore eventStore,
        IElasticsearchProjectionDocumentRepairStore<
            AevatarOAuthClientDocument,
            string> repairStore)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _repairStore = repairStore ?? throw new ArgumentNullException(nameof(repairStore));
    }

    public async Task<AevatarOAuthClientVersionRegressionInspection> InspectAsync(
        CancellationToken ct = default)
    {
        var actorId = AevatarOAuthClientGAgent.WellKnownId;
        var sourceVersion = await _eventStore
            .GetVersionAsync(actorId, ct)
            .ConfigureAwait(false);
        var lease = await _repairStore
            .InspectAsync(actorId, ct)
            .ConfigureAwait(false);
        var document = lease?.Document;
        return new AevatarOAuthClientVersionRegressionInspection(
            actorId,
            sourceVersion,
            document?.StateVersion,
            document?.LastEventId ?? string.Empty,
            document?.ActorId ?? string.Empty,
            Repairable: false,
            Detail: string.Empty);
    }

    public async Task<AevatarOAuthClientReplicaDeleteDisposition> DeleteIfMatchesAsync(
        AevatarOAuthClientVersionRegressionRepairRequest request,
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

        var actorId = AevatarOAuthClientGAgent.WellKnownId;
        if (!string.Equals(request.ExpectedActorId, actorId, StringComparison.Ordinal))
            return AevatarOAuthClientReplicaDeleteDisposition.SourceChanged;

        var sourceVersion = await _eventStore
            .GetVersionAsync(actorId, ct)
            .ConfigureAwait(false);
        if (sourceVersion != request.ExpectedSourceStateVersion)
            return AevatarOAuthClientReplicaDeleteDisposition.SourceChanged;

        var lease = await _repairStore
            .InspectAsync(actorId, ct)
            .ConfigureAwait(false);
        if (lease is null)
        {
            // Do not turn an absent document at invocation start into an
            // idempotency credential. AlreadyAbsent is reserved for the
            // repair store reconciling an ambiguous delete after this call
            // acquired and verified the exact lease.
            return AevatarOAuthClientReplicaDeleteDisposition.DocumentChanged;
        }
        if (!FingerprintMatches(lease.Document, actorId, request))
            return AevatarOAuthClientReplicaDeleteDisposition.DocumentChanged;

        sourceVersion = await _eventStore
            .GetVersionAsync(actorId, ct)
            .ConfigureAwait(false);
        if (sourceVersion != request.ExpectedSourceStateVersion)
            return AevatarOAuthClientReplicaDeleteDisposition.SourceChanged;

        var deleteDisposition = await _repairStore
            .DeleteIfUnchangedAsync(lease, ct)
            .ConfigureAwait(false);
        return deleteDisposition switch
        {
            ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted =>
                AevatarOAuthClientReplicaDeleteDisposition.Deleted,
            ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent =>
                AevatarOAuthClientReplicaDeleteDisposition.AlreadyAbsent,
            ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict =>
                AevatarOAuthClientReplicaDeleteDisposition.RevisionConflict,
            _ => throw new InvalidOperationException(
                $"Unsupported Elasticsearch repair delete disposition '{deleteDisposition}'."),
        };
    }

    private static bool FingerprintMatches(
        AevatarOAuthClientDocument document,
        string actorId,
        AevatarOAuthClientVersionRegressionRepairRequest request) =>
        string.Equals(document.Id, actorId, StringComparison.Ordinal) &&
        string.Equals(document.ActorId, actorId, StringComparison.Ordinal) &&
        document.StateVersion == request.ExpectedDocumentStateVersion &&
        string.Equals(
            document.LastEventId,
            request.ExpectedDocumentLastEventId,
            StringComparison.Ordinal);
}
