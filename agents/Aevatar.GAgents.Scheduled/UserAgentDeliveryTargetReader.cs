using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Internal-only delivery target reader. Combines the public projection document
/// (delivery routing) with the credential read model (NyxApiKey). Only registered for
/// outbound delivery components (e.g. <see cref="FeishuCardHumanInteractionPort"/>);
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
/// <see cref="FeishuCardHumanInteractionPort"/> would call the NyxID proxy with an
/// empty token, surfacing what is really a "credential not yet projected" condition
/// as an outbound 401/403. Returning null here is the right shape: the caller sees
/// "delivery target unavailable" and surfaces a re-try / propagating message instead.
/// </summary>
public sealed class UserAgentDeliveryTargetReader : IUserAgentDeliveryTargetReader
{
    private readonly IProjectionDocumentReader<UserAgentCatalogDocument, string> _documentReader;
    private readonly IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string> _credentialReader;

    public UserAgentDeliveryTargetReader(
        IProjectionDocumentReader<UserAgentCatalogDocument, string> documentReader,
        IProjectionDocumentReader<UserAgentCatalogNyxCredentialDocument, string> credentialReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _credentialReader = credentialReader ?? throw new ArgumentNullException(nameof(credentialReader));
    }

    public async Task<UserAgentDeliveryTarget?> GetAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;

        var document = await _documentReader.GetAsync(agentId, ct);
        if (document is null || document.Tombstoned) return null;

        var credential = await _credentialReader.GetAsync(agentId, ct);
        if (credential is null || string.IsNullOrWhiteSpace(credential.NyxApiKey))
        {
            // Fail-closed: credential not yet projected (or projected blank). Returning a
            // target with NyxApiKey="" would push the projection-lag failure mode onto the
            // outbound NyxID proxy as a 401/403 surface, which is wrong: the caller needs
            // to know "delivery target unavailable" so retries/propagating messages are
            // honest about what's missing. Issue #466 review.
            return null;
        }

        return new UserAgentDeliveryTarget(
            AgentId: document.Id ?? string.Empty,
#pragma warning disable CS0612 // legacy field read for delivery target compatibility
            Platform: document.Platform ?? string.Empty,
#pragma warning restore CS0612
            ConversationId: document.ConversationId ?? string.Empty,
            NyxProviderSlug: document.NyxProviderSlug ?? string.Empty,
            NyxApiKey: credential.NyxApiKey,
            LarkReceiveId: document.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType: document.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback: document.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback: document.LarkReceiveIdTypeFallback ?? string.Empty,
            TemplateName: document.TemplateName ?? string.Empty,
            AgentType: document.AgentType ?? string.Empty);
    }
}
