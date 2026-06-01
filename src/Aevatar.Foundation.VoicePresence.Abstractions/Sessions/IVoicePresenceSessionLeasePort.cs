namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: host voice attachment reused a resolved local module instance as if runtime shape were capability state.
//   New principle: host attachment asks the actor for a typed lease and treats the synchronous result as dispatch acceptance only.
public interface IVoicePresenceSessionLeasePort
{
    Task<VoicePresenceSessionLeaseHandle> AcquireAsync(
        VoicePresenceSessionLeaseRequest request,
        CancellationToken ct = default);

    Task ReleaseAsync(
        VoicePresenceSessionLeaseHandle handle,
        string reason,
        CancellationToken ct = default);

    Task CompleteTransportLifetimeAsync(
        VoicePresenceSessionLeaseHandle handle,
        string transportLeaseId,
        string reason,
        CancellationToken ct = default);
}
