namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Runtime-only registration read path used by callback ingress.
/// Composes any runtime-only direct callback binding material that should stay
/// off the public registration read model.
/// </summary>
public interface IChannelBotRegistrationRuntimeQueryPort
{
    Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);
}
