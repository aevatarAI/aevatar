namespace Aevatar.GAgents.ChannelRuntime;

public interface IChannelBotRegistrationQueryPort
{
    Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelBotRegistrationEntry>> ListAsync(CancellationToken ct = default);
}
