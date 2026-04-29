using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Reads the cluster-singleton OAuth client state from the projection
/// document. Backs <see cref="IAevatarOAuthClientProvider"/> for callers
/// that need <c>client_id</c> + HMAC key (state-token codec, broker, OAuth
/// callback, status endpoint).
/// </summary>
public sealed class AevatarOAuthClientProjectionProvider : IAevatarOAuthClientProvider
{
    private readonly IProjectionDocumentReader<AevatarOAuthClientDocument, string> _reader;

    public AevatarOAuthClientProjectionProvider(
        IProjectionDocumentReader<AevatarOAuthClientDocument, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default)
    {
        var document = await _reader.GetAsync(AevatarOAuthClientGAgent.WellKnownId, ct).ConfigureAwait(false);
        if (document is null || !document.IsProvisioned || document.HmacKey.IsEmpty)
            throw new AevatarOAuthClientNotProvisionedException();

        var hmac = document.HmacKey.ToByteArray();
        var brokerObservedAt = document.BrokerCapabilityObservedAtUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(document.BrokerCapabilityObservedAtUnix)
            : (DateTimeOffset?)null;

        return new AevatarOAuthClientSnapshot(
            ClientId: document.ClientId,
            ClientIdIssuedAt: DateTimeOffset.FromUnixTimeSeconds(document.ClientIdIssuedAtUnix),
            HmacKey: hmac,
            HmacKeyRotatedAt: DateTimeOffset.FromUnixTimeSeconds(document.HmacKeyRotatedAtUnix),
            NyxIdAuthority: document.NyxidAuthority,
            BrokerCapabilityObserved: document.BrokerCapabilityObserved,
            BrokerCapabilityObservedAt: brokerObservedAt);
    }
}
