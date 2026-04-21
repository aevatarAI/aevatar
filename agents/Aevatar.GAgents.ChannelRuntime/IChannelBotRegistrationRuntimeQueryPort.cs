namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Runtime-only registration read path used by callback ingress.
/// Preserves legacy secret material from the read model so pre-migration
/// registrations keep working until credential_ref backfill completes.
/// </summary>
public interface IChannelBotRegistrationRuntimeQueryPort
{
    Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);
}
