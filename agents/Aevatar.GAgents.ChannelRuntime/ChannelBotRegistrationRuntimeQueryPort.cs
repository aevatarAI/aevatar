namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotRegistrationRuntimeQueryPort : IChannelBotRegistrationRuntimeQueryPort
{
    private readonly IChannelBotRegistrationQueryPort _queryPort;

    public ChannelBotRegistrationRuntimeQueryPort(
        IChannelBotRegistrationQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default) =>
        _queryPort.GetAsync(registrationId, ct);
}
