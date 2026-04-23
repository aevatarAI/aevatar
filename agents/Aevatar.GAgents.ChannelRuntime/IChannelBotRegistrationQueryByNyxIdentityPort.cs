namespace Aevatar.GAgents.ChannelRuntime;

public interface IChannelBotRegistrationQueryByNyxIdentityPort
{
    Task<ChannelBotRegistrationEntry?> GetByNyxAgentApiKeyIdAsync(
        string nyxAgentApiKeyId,
        CancellationToken ct = default);

    Task<ChannelBotRegistrationEntry?> GetByNyxChannelBotIdAsync(
        string nyxChannelBotId,
        CancellationToken ct = default);
}
