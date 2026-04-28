namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelBotRegistrationQueryPort
{
    Task<ChannelBotRegistrationEntry?> GetAsync(string registrationId, CancellationToken ct = default);

    /// <summary>
    /// Returns the projection state version for a registration document, or null
    /// if the document does not exist. Used to confirm that a command was actually
    /// committed by the actor (version advances on each persisted event).
    /// </summary>
    Task<long?> GetStateVersionAsync(string registrationId, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelBotRegistrationEntry>> QueryAllAsync(CancellationToken ct = default);
}
