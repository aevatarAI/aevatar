using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Refactor (iter97/cluster-526): Old pattern: the OAuth client HMAC-key
/// readmodel had no explicit ES ACL assertion at startup. New principle: this
/// is the only provider allowed to read the HMAC-bearing document, and ES
/// deployments must pass <see cref="AevatarOAuthClientEsAclStartupGuard"/>.
///
/// Reads the cluster-singleton OAuth client state from the projection
/// document. Backs the read seam exposed by <see cref="IAevatarOAuthClientProvider"/>.
/// </summary>
public sealed class AevatarOAuthClientProjectionProvider : IAevatarOAuthClientProvider
{
    private readonly IProjectionDocumentReader<AevatarOAuthClientDocument, string> _reader;
    private readonly ISecretVault _secretVault;

    public AevatarOAuthClientProjectionProvider(
        IProjectionDocumentReader<AevatarOAuthClientDocument, string> reader,
        ISecretVault secretVault)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    }

    public async Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default)
    {
        var document = await _reader.GetAsync(AevatarOAuthClientGAgent.WellKnownId, ct).ConfigureAwait(false);
        // Provisioned when a current HMAC key exists in either shape: a vault
        // reference (new writes) or legacy plaintext bytes (pre-migration).
        var hasCurrentKey = document is not null
            && (HasRef(document.HmacKeyRef) || !document.HmacKey.IsEmpty);
        if (document is null || !document.IsProvisioned || !hasCurrentKey)
            throw new AevatarOAuthClientNotProvisionedException();

        var hmacKey = await ResolveKeyAsync(document.HmacKeyRef, document.HmacKey, ct).ConfigureAwait(false);

        var brokerObservedAt = document.BrokerCapabilityObservedAtUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(document.BrokerCapabilityObservedAtUnix)
            : (DateTimeOffset?)null;

        // Previous-key carrier when one is in flight. Verifiers (the codec)
        // do their own grace-window check against the demoted_at timestamp;
        // we surface the resolved bytes raw here. Dual-read: a ref-backed
        // previous key resolves via the vault, a legacy previous key falls
        // back to the plaintext bytes.
        string? previousKid = null;
        byte[]? previousKey = null;
        DateTimeOffset? previousDemotedAt = null;
        if (HasRef(document.PreviousHmacKeyRef) || !document.PreviousHmacKey.IsEmpty)
        {
            previousKid = string.IsNullOrEmpty(document.PreviousHmacKid) ? null : document.PreviousHmacKid;
            previousKey = await ResolveKeyAsync(document.PreviousHmacKeyRef, document.PreviousHmacKey, ct).ConfigureAwait(false);
            previousDemotedAt = document.PreviousHmacDemotedAtUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(document.PreviousHmacDemotedAtUnix)
                : null;
        }

        var redirectUris = document.RedirectUris.Count > 0
            ? document.RedirectUris.ToArray()
            : string.IsNullOrEmpty(document.RedirectUri) ? [] : [document.RedirectUri];

        return new AevatarOAuthClientSnapshot(
            ClientId: document.ClientId,
            ClientIdIssuedAt: DateTimeOffset.FromUnixTimeSeconds(document.ClientIdIssuedAtUnix),
            HmacKid: string.IsNullOrEmpty(document.HmacKid) ? AevatarOAuthClientGAgent.InitialHmacKid : document.HmacKid,
            HmacKey: hmacKey,
            HmacKeyRotatedAt: DateTimeOffset.FromUnixTimeSeconds(document.HmacKeyRotatedAtUnix),
            NyxIdAuthority: document.NyxidAuthority,
            BrokerCapabilityObserved: document.BrokerCapabilityObserved,
            BrokerCapabilityObservedAt: brokerObservedAt,
            PreviousHmacKid: previousKid,
            PreviousHmacKey: previousKey,
            PreviousHmacDemotedAt: previousDemotedAt,
            RedirectUri: string.IsNullOrEmpty(document.RedirectUri) ? null : document.RedirectUri,
            OauthScope: string.IsNullOrEmpty(document.OauthScope) ? null : document.OauthScope,
            RedirectUris: redirectUris);
    }

    private static bool HasRef(SecretReference? reference) =>
        reference is not null && !string.IsNullOrEmpty(reference.Ref);

    /// <summary>
    /// Resolves HMAC key bytes, preferring the vault reference and falling
    /// back to legacy plaintext bytes when no ref is present. Ref-backed
    /// material is stored base64-encoded (raw 32B key), so decode on resolve.
    /// A ref that fails to resolve is a provisioning fault, not a silent
    /// fall-through to stale legacy bytes.
    /// </summary>
    private async Task<byte[]> ResolveKeyAsync(
        SecretReference? reference,
        ByteString legacy,
        CancellationToken ct)
    {
        if (HasRef(reference))
        {
            var result = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    reference!.Ref,
                    CredentialSecretPurposes.OAuthStateTokenHmacKey,
                    AevatarOAuthClientGAgent.WellKnownId,
                    AevatarOAuthClientGAgent.WellKnownId,
                    "identity.oauth.resolve"),
                ct).ConfigureAwait(false);
            if (!result.Resolved)
                throw new AevatarOAuthClientNotProvisionedException();
            return Convert.FromBase64String(result.Secret!);
        }

        return legacy.ToByteArray();
    }
}
