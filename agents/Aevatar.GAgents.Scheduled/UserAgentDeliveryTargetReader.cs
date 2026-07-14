using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Internal-only delivery target reader. Combines the public projection document
/// (delivery routing) with the credential read model (NyxApiKey). Only registered for
/// outbound delivery components (e.g. Lark notification/card senders);
/// not visible to LLM tools.
///
/// Returns <c>null</c> when:
/// <list type="bullet">
///   <item>the agent doesn't exist or is tombstoned, or</item>
///   <item>the credential document hasn't been materialized yet, or</item>
///   <item>the credential document exists but its <c>NyxApiKey</c> is blank.</item>
/// </list>
///
/// The blank-credential case is a fail-closed signal — never construct a delivery
/// target with an empty <c>NyxApiKey</c>. If the runtime received one,
/// outbound Lark senders would call the NyxID proxy with an empty token,
/// surfacing what is really a "credential not yet projected" condition
/// as an outbound 401/403. Returning null here is the right shape: the caller sees
/// "delivery target unavailable" and surfaces a re-try / propagating message instead.
/// </summary>
public sealed class UserAgentDeliveryTargetReader : IUserAgentDeliveryTargetReader
{
    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;
    private readonly IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string> _credentialReader;
    private readonly ISecretVault _secretVault;

    public UserAgentDeliveryTargetReader(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string> credentialReader,
        ISecretVault secretVault)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    }

    public async Task<UserAgentDeliveryTarget?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        if (document is null || document.Tombstoned) return null;

        var credential = await _credentialReader.GetAsync(agentId, ct);
        if (credential is null)
        {
            // Fail-closed: credential not yet projected (or projected blank). Returning a
            // target with NyxApiKey="" would push the projection-lag failure mode onto the
            // outbound NyxID proxy as a 401/403 surface, which is wrong: the caller needs
            // to know "delivery target unavailable" so retries/propagating messages are
            // honest about what's missing. Issue #466 review.
            return null;
        }

        var nyxApiKey = await ResolveNyxApiKeyAsync(document, credential, ct);
        if (string.IsNullOrWhiteSpace(nyxApiKey))
            return null;

#pragma warning disable CS0612 // deprecated document fields are read only as a channel_address compatibility bridge
        var channelAddress = UserAgentCatalogChannelAddress.ToModel(
            document.ChannelAddress,
            ResolveDeliveryPlatform(document),
            document.NyxProviderSlug,
            document.ConversationId,
            document.LarkReceiveId,
            document.LarkReceiveIdType,
            document.LarkReceiveIdFallback,
            document.LarkReceiveIdTypeFallback);
#pragma warning restore CS0612

        return new UserAgentDeliveryTarget(
            AgentId: document.Id ?? string.Empty,
            Platform: channelAddress.Platform,
            ConversationId: channelAddress.ConversationId,
            NyxProviderSlug: channelAddress.ProviderSlug,
            NyxApiKey: nyxApiKey,
            ChannelAddress: channelAddress,
            OutputFormat: document.OutputFormat,
            TemplateName: document.TemplateName ?? string.Empty,
            AgentType: document.AgentType ?? string.Empty);
    }

    private async Task<string> ResolveNyxApiKeyAsync(
        UserAgentCatalogDocument document,
        UserAgentCatalogNyxCredentialDocument credential,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(credential.NyxApiKeyReference?.Ref))
        {
            var resolved = await _secretVault.ResolveAsync(new ResolveSecretRequest(
                credential.NyxApiKeyReference.Ref,
                ResolveScheduledAgentKeyPurpose(credential.NyxApiKeyReference),
                credential.NyxApiKeyReference.OwnerScopeKey,
                ResolveApiKeyId(document, credential),
                "scheduled-delivery-target"),
                ct);
            return resolved.Secret?.Trim() ?? string.Empty;
        }

        return credential.NyxApiKey?.Trim() ?? string.Empty;
    }

    private static string ResolveApiKeyId(
        UserAgentCatalogDocument document,
        UserAgentCatalogNyxCredentialDocument credential) =>
        string.IsNullOrWhiteSpace(credential.ApiKeyId)
            ? document.ApiKeyId ?? string.Empty
            : credential.ApiKeyId.Trim();

    private static string ResolveScheduledAgentKeyPurpose(SecretReference reference) =>
        string.IsNullOrWhiteSpace(reference.Purpose)
            ? CredentialSecretPurposes.ScheduledNyxApiKey
            : reference.Purpose.Trim();

    private static string ResolveDeliveryPlatform(UserAgentCatalogDocument document)
    {
#pragma warning disable CS0612 // legacy field read for delivery target compatibility
        if (!string.IsNullOrWhiteSpace(document.Platform))
            return document.Platform;
#pragma warning restore CS0612

        return document.TargetPlatform ?? string.Empty;
    }
}
