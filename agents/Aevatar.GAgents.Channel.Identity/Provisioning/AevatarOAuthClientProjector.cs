using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Refactor (iter97/cluster-526): Old pattern: HMAC key projection shared the
/// generic document-store registration without an ES access-boundary guard.
/// New principle: projector remains the committed-state materializer, while
/// startup/CI guards fail closed if broader query layers can reach this document.
///
/// Projects the cluster-singleton <see cref="AevatarOAuthClientState"/> into
/// one <see cref="AevatarOAuthClientDocument"/>.
/// </summary>
public sealed class AevatarOAuthClientProjector
    : ICurrentStateProjectionMaterializer<AevatarOAuthClientMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<AevatarOAuthClientDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public AevatarOAuthClientProjector(
        IProjectionWriteDispatcher<AevatarOAuthClientDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        AevatarOAuthClientMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<AevatarOAuthClientState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent is null ||
            state is null)
        {
            return;
        }

        var document = new AevatarOAuthClientDocument
        {
            Id = context.RootActorId,
            ClientId = state.ClientId ?? string.Empty,
            ClientIdIssuedAtUnix = state.ClientIdIssuedAtUnix,
            // Mirror both the vault ref (new writes) and legacy bytes (empty
            // for new, populated for legacy state) so the provider can dual-
            // read. No decryption here — the ref stays opaque in the readmodel.
            HmacKeyRef = state.HmacKeyRef,
            HmacKey = state.HmacKey ?? Google.Protobuf.ByteString.Empty,
            HmacKid = state.HmacKid ?? string.Empty,
            HmacKeyRotatedAtUnix = state.HmacKeyRotatedAtUnix,
            PreviousHmacKeyRef = state.PreviousHmacKeyRef,
            PreviousHmacKey = state.PreviousHmacKey ?? Google.Protobuf.ByteString.Empty,
            PreviousHmacKid = state.PreviousHmacKid ?? string.Empty,
            PreviousHmacDemotedAtUnix = state.PreviousHmacDemotedAtUnix,
            NyxidAuthority = state.NyxidAuthority ?? string.Empty,
            BrokerCapabilityObserved = state.BrokerCapabilityObserved,
            BrokerCapabilityObservedAtUnix = state.BrokerCapabilityObservedAtUnix,
            RedirectUri = state.RedirectUri ?? string.Empty,
            OauthScope = state.OauthScope ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
        };
        document.RedirectUris.AddRange(state.RedirectUris);

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
