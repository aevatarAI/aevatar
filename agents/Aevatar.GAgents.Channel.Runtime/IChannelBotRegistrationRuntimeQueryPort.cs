namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Runtime registration read path used by legacy call sites that still expect a
/// callback-facing query port. It now mirrors the public non-secret read model.
/// </summary>
public interface IChannelBotRegistrationRuntimeQueryPort
{
    Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);
}
