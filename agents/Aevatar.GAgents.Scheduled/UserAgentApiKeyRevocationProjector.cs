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
        if (stateEvent.EventData.Is(UserAgentCatalogApiKeyRevocationAttemptRecordedEvent.Descriptor))
        {
            var attempt = stateEvent.EventData.Unpack<UserAgentCatalogApiKeyRevocationAttemptRecordedEvent>();
            var completed = !state.PendingApiKeyRevocations.Any(revocation =>
                string.Equals(revocation.AgentId, attempt.AgentId, StringComparison.Ordinal) &&
                string.Equals(revocation.ApiKeyId, attempt.ApiKeyId, StringComparison.Ordinal));
            if (completed &&
                !string.IsNullOrWhiteSpace(attempt.AgentId) &&
                !string.IsNullOrWhiteSpace(attempt.ApiKeyId) &&
                !string.IsNullOrWhiteSpace(attempt.SecretReferenceRef))
            {
                await _writeDispatcher.DeleteAsync(
                    BuildDocumentId(attempt.AgentId, attempt.ApiKeyId, attempt.SecretReferenceRef),
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

    private static UserAgentApiKeyRevocationDocument Materialize(
        UserAgentCatalogMaterializationContext context,
        StateEvent stateEvent,
        UserAgentApiKeyRevocation revocation,
        DateTimeOffset updatedAt)
    {
        var document = new UserAgentApiKeyRevocationDocument
        {
            Id = BuildDocumentId(
                revocation.AgentId,
                revocation.ApiKeyId,
                revocation.NyxApiKeyReference?.Ref ?? string.Empty),
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
        document.SecretSubjectId = revocation.SecretSubjectId ?? string.Empty;
        document.RepairReason = revocation.RepairReason ?? string.Empty;
        document.RequestedBySubjectId = revocation.RequestedBySubjectId ?? string.Empty;
        document.RequestedAtUnixMs = revocation.RequestedAtUnixMs;

        return document;
    }
}
