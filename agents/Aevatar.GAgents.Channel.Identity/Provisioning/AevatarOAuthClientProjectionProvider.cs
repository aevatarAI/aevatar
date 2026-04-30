using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Reads the cluster-singleton OAuth client state from the projection
/// document. Backs the read seam exposed by
/// <see cref="IAevatarOAuthClientProvider"/> for callers that need
/// <c>client_id</c> + HMAC keys (state-token codec, broker, OAuth callback,
/// status endpoint).
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

        var brokerObservedAt = document.BrokerCapabilityObservedAtUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(document.BrokerCapabilityObservedAtUnix)
            : (DateTimeOffset?)null;

        // Previous-key carrier when one is in flight. Verifiers (the codec)
        // do their own grace-window check against the demoted_at timestamp;
        // we surface the bytes raw here.
        string? previousKid = null;
        byte[]? previousKey = null;
        DateTimeOffset? previousDemotedAt = null;
        if (!document.PreviousHmacKey.IsEmpty)
        {
            previousKid = string.IsNullOrEmpty(document.PreviousHmacKid) ? null : document.PreviousHmacKid;
            previousKey = document.PreviousHmacKey.ToByteArray();
            previousDemotedAt = document.PreviousHmacDemotedAtUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(document.PreviousHmacDemotedAtUnix)
                : null;
        }

        return new AevatarOAuthClientSnapshot(
            ClientId: document.ClientId,
            ClientIdIssuedAt: DateTimeOffset.FromUnixTimeSeconds(document.ClientIdIssuedAtUnix),
            HmacKid: string.IsNullOrEmpty(document.HmacKid) ? AevatarOAuthClientGAgent.InitialHmacKid : document.HmacKid,
            HmacKey: document.HmacKey.ToByteArray(),
            HmacKeyRotatedAt: DateTimeOffset.FromUnixTimeSeconds(document.HmacKeyRotatedAtUnix),
            NyxIdAuthority: document.NyxidAuthority,
            BrokerCapabilityObserved: document.BrokerCapabilityObserved,
            BrokerCapabilityObservedAt: brokerObservedAt,
            PreviousHmacKid: previousKid,
            PreviousHmacKey: previousKey,
            PreviousHmacDemotedAt: previousDemotedAt,
            RedirectUri: string.IsNullOrEmpty(document.RedirectUri) ? null : document.RedirectUri);
    }
}
