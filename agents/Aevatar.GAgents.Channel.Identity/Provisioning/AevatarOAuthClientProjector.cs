using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Projects the cluster-singleton <see cref="AevatarOAuthClientState"/> into
/// one <see cref="AevatarOAuthClientDocument"/>. Backs the read seam exposed
/// by <see cref="IAevatarOAuthClientProvider"/>.
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
            HmacKey = state.HmacKey ?? Google.Protobuf.ByteString.Empty,
            HmacKid = state.HmacKid ?? string.Empty,
            HmacKeyRotatedAtUnix = state.HmacKeyRotatedAtUnix,
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

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
