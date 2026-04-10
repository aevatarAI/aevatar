namespace Aevatar.GAgents.ChannelRuntime;

public interface IChannelBotRegistrationQueryPort
{
    Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelBotRegistrationEntry>> QueryAllAsync(CancellationToken ct = default);
}
