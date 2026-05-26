namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Internal reader for outbound delivery: returns the agent's delivery target plus the
/// credential needed to send a message on the agent's behalf. By-id (no caller scope)
/// because the caller here is the runtime — workflow human-interaction port, scheduled
/// runner outbound, etc. — that is already executing on behalf of the agent's owner.
///
/// Architectural intent (issue #466 §D): this interface is registered ONLY for outbound
/// delivery components; LLM-facing <c>IAgentTool</c> implementations must NOT take this
/// as a constructor dependency. That keeps the secret boundary a type boundary, not a
/// convention. An architecture guard (full-scan) enforces "no IAgentTool depends on
/// IUserAgentDeliveryTargetReader".
/// </summary>
public interface IUserAgentDeliveryTargetReader
{
    Task<UserAgentDeliveryTarget?> GetAsync(string agentId, CancellationToken ct = default);
}

/// <summary>
/// Credential-bearing record returned by <see cref="IUserAgentDeliveryTargetReader"/>.
/// Distinct type from <c>UserAgentCatalogEntry</c> so the secret surface is a separate
/// type — accidentally serializing a <c>UserAgentDeliveryTarget</c> is a different code
/// path than serializing the public DTO.
/// </summary>
public sealed record UserAgentDeliveryTarget(
    string AgentId,
    string Platform,
    string ConversationId,
    string NyxProviderSlug,
    string NyxApiKey,
    string LarkReceiveId,
    string LarkReceiveIdType,
    string LarkReceiveIdFallback,
    string LarkReceiveIdTypeFallback,
    string TemplateName,
    string AgentType);
