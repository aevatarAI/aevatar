using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Internal-only delivery target reader. Combines the public projection document
/// (delivery routing) with the credential read model (NyxApiKey). Only registered for
/// outbound delivery components (e.g. <see cref="FeishuCardHumanInteractionPort"/>);
/// not visible to LLM tools.
///
/// Returns <c>null</c> when the agent doesn't exist, is tombstoned, or the credential
/// document hasn't been materialized yet.
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
        var nyxApiKey = credential?.NyxApiKey ?? string.Empty;

        return new UserAgentDeliveryTarget(
            AgentId: document.Id ?? string.Empty,
#pragma warning disable CS0612 // legacy field read for delivery target compatibility
            Platform: document.Platform ?? string.Empty,
#pragma warning restore CS0612
            ConversationId: document.ConversationId ?? string.Empty,
            NyxProviderSlug: document.NyxProviderSlug ?? string.Empty,
            NyxApiKey: nyxApiKey,
            LarkReceiveId: document.LarkReceiveId ?? string.Empty,
            LarkReceiveIdType: document.LarkReceiveIdType ?? string.Empty,
            LarkReceiveIdFallback: document.LarkReceiveIdFallback ?? string.Empty,
            LarkReceiveIdTypeFallback: document.LarkReceiveIdTypeFallback ?? string.Empty,
            TemplateName: document.TemplateName ?? string.Empty,
            AgentType: document.AgentType ?? string.Empty);
    }
}
