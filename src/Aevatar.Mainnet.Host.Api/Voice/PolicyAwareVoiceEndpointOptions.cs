namespace Aevatar.Mainnet.Host.Api.Voice;

public sealed class PolicyAwareVoiceEndpointOptions
{
    public TimeSpan WebSocketCloseWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan AttachTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
