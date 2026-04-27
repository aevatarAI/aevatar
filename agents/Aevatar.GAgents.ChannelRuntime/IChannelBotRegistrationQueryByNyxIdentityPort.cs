namespace Aevatar.GAgents.ChannelRuntime;

public interface IChannelBotRegistrationQueryByNyxIdentityPort
{
    Task<ChannelBotRegistrationEntry?> GetByNyxAgentApiKeyIdAsync(
        string nyxAgentApiKeyId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every active registration that matches the given Nyx agent API key id.
    /// Callers that route by API key (e.g., scope resolution for relay callbacks) MUST
    /// use this rather than <see cref="GetByNyxAgentApiKeyIdAsync"/> when a wrong-tenant
    /// match would be a security regression — repeated mirror / provision flows can
    /// persist multiple registrations sharing the same API key id but different
    /// scope ids, and the single-result variant returns only the first match.
    /// </summary>
    Task<IReadOnlyList<ChannelBotRegistrationEntry>> ListByNyxAgentApiKeyIdAsync(
        string nyxAgentApiKeyId,
        CancellationToken ct = default);

    Task<ChannelBotRegistrationEntry?> GetByNyxChannelBotIdAsync(
        string nyxChannelBotId,
        CancellationToken ct = default);
}
