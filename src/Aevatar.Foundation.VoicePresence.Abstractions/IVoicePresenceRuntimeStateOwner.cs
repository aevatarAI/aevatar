namespace Aevatar.Foundation.VoicePresence.Abstractions;

// Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
//   Old pattern: VoicePresenceModule reflected over local actor State/Persist members to find voice runtime facts.
//   New principle: voice runtime facts are read and written through an explicit actor-owned behavior contract.
public interface IVoicePresenceRuntimeStateOwner
{
    bool TryGetVoicePresenceRuntimeState(string moduleName, out VoicePresenceRuntimeState runtimeState);

    Task PersistVoicePresenceRuntimeStateAsync(
        string moduleName,
        VoicePresenceRuntimeState runtimeState,
        CancellationToken ct = default);
}
