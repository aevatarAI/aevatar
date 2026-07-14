using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentApiKeyRevocationProjector
    : ICurrentStateProjectionMaterializer<UserAgentCatalogMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public UserAgentApiKeyRevocationProjector(
        IProjectionWriteDispatcher<UserAgentApiKeyRevocationDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        UserAgentCatalogMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<UserAgentCatalogState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent is null ||
            state is null)
        {
            return;
        }

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var deletedLegacyDocumentIds = new HashSet<string>(StringComparer.Ordinal);
        if (stateEvent.EventData.Is(UserAgentCatalogApiKeyRevocationAttemptRecordedEvent.Descriptor))
        {
            var attempt = stateEvent.EventData.Unpack<UserAgentCatalogApiKeyRevocationAttemptRecordedEvent>();
            var completed = !state.PendingApiKeyRevocations.Any(revocation =>
                MatchesIdentity(
                    revocation,
                    attempt.AgentId,
                    attempt.ApiKeyId,
                    attempt.SecretReferenceRef));
            if (completed &&
                !string.IsNullOrWhiteSpace(attempt.AgentId) &&
                !string.IsNullOrWhiteSpace(attempt.ApiKeyId))
            {
                await DeleteLegacyDocumentAsync(attempt.AgentId, deletedLegacyDocumentIds, ct);
                var documentId = string.IsNullOrWhiteSpace(attempt.SecretReferenceRef)
                    ? ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked(
                        attempt.AgentId.Trim(),
                        attempt.ApiKeyId.Trim())
                    : BuildDocumentId(
                        attempt.AgentId,
                        attempt.ApiKeyId,
                        attempt.SecretReferenceRef);
                await _writeDispatcher.DeleteAsync(
                    documentId,
                    ct);
            }
        }
        else if (stateEvent.EventData.Is(UserAgentCatalogCredentialRevocationRepairedEvent.Descriptor))
        {
            var repaired = stateEvent.EventData.Unpack<UserAgentCatalogCredentialRevocationRepairedEvent>();
            if (!string.IsNullOrWhiteSpace(repaired.AgentId) &&
                !string.IsNullOrWhiteSpace(repaired.ApiKeyId))
            {
                await DeleteLegacyDocumentAsync(repaired.AgentId, deletedLegacyDocumentIds, ct);
                await _writeDispatcher.DeleteAsync(
                    ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked(
                        repaired.AgentId.Trim(),
                        repaired.ApiKeyId.Trim()),
                    ct);
            }
        }

        foreach (var revocation in state.PendingApiKeyRevocations)
        {
            if (string.IsNullOrWhiteSpace(revocation.AgentId) ||
                string.IsNullOrWhiteSpace(revocation.ApiKeyId))
            {
                continue;
            }

            await DeleteLegacyDocumentAsync(revocation.AgentId, deletedLegacyDocumentIds, ct);
            await _writeDispatcher.UpsertAsync(
                Materialize(context, stateEvent, revocation, updatedAt),
                ct);
        }
    }

    public static string BuildDocumentId(string agentId, string apiKeyId, string secretReference) =>
        ScheduledAgentCredentialRevocationDocumentIds.Build(
            agentId.Trim(),
            apiKeyId.Trim(),
            secretReference.Trim());

    private async Task DeleteLegacyDocumentAsync(
        string agentId,
        ISet<string> deletedLegacyDocumentIds,
        CancellationToken ct)
    {
        var legacyDocumentId = agentId.Trim();
        if (!deletedLegacyDocumentIds.Add(legacyDocumentId))
            return;

        await _writeDispatcher.DeleteAsync(legacyDocumentId, ct);
    }

    private static string BuildDocumentId(UserAgentApiKeyRevocation revocation)
    {
        var secretReference = ScheduledAgentCredentialRevocationIdentity.ResolveSecretReferenceRef(revocation);
        return string.IsNullOrEmpty(secretReference)
            ? ScheduledAgentCredentialRevocationDocumentIds.BuildBlocked(
                revocation.AgentId.Trim(),
                revocation.ApiKeyId.Trim())
            : BuildDocumentId(revocation.AgentId, revocation.ApiKeyId, secretReference);
    }

    private static bool MatchesIdentity(
        UserAgentApiKeyRevocation revocation,
        string agentId,
        string apiKeyId,
        string secretReference) =>
        string.Equals(revocation.AgentId, agentId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(revocation.ApiKeyId, apiKeyId?.Trim(), StringComparison.Ordinal) &&
        string.Equals(
            ScheduledAgentCredentialRevocationIdentity.ResolveSecretReferenceRef(revocation),
            secretReference?.Trim() ?? string.Empty,
            StringComparison.Ordinal);

    private static UserAgentApiKeyRevocationDocument Materialize(
        UserAgentCatalogMaterializationContext context,
        StateEvent stateEvent,
        UserAgentApiKeyRevocation revocation,
        DateTimeOffset updatedAt)
    {
        var document = new UserAgentApiKeyRevocationDocument
        {
            Id = BuildDocumentId(revocation),
            AgentId = revocation.AgentId,
            ApiKeyId = revocation.ApiKeyId,
            RequestedAtUtc = revocation.RequestedAt?.Clone(),
            AttemptCount = revocation.AttemptCount,
            LastAttemptAtUtc = revocation.LastAttemptAt?.Clone(),
            LastHttpStatus = revocation.LastHttpStatus,
            LastError = revocation.LastError ?? string.Empty,
            FailureKind = revocation.FailureKind,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = updatedAt,
            ActorId = context.RootActorId,
        };

        if (revocation.NyxApiKeyReference is not null)
            document.NyxApiKeyReference = revocation.NyxApiKeyReference.Clone();
        if (revocation.OwnerScope is not null)
            document.OwnerScope = revocation.OwnerScope.Clone();
        if (revocation.NyxIdTrack is not null)
            document.NyxIdTrack = revocation.NyxIdTrack.Clone();
        if (revocation.VaultTrack is not null)
            document.VaultTrack = revocation.VaultTrack.Clone();
        if (revocation.VaultRevocationDescriptor is not null)
            document.VaultRevocationDescriptor = revocation.VaultRevocationDescriptor.Clone();
        document.SecretSubjectId = revocation.SecretSubjectId ?? string.Empty;
        document.RepairReason = revocation.RepairReason ?? string.Empty;
        document.RequestedBySubjectId = revocation.RequestedBySubjectId ?? string.Empty;
        document.RepairRequestedAtUnixMs = revocation.RepairRequestedAtUnixMs;

        return document;
    }
}
