namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public interface IVoiceVolatileMediaStreamPort
{
    bool SupportsRemoteAudio { get; }

    Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        CancellationToken ct = default);

    Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
        VoiceToolCredentialTransportBinding? toolCredentialBinding,
        CancellationToken ct = default);

    Task DetachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport? expectedTransport,
        CancellationToken ct = default);

    Task CompleteTransportLifetimeAsync(
        VoicePresenceSessionLeaseHandle handle,
        VoiceTransportLifetimeCompleted? completed,
        string reason,
        CancellationToken ct = default);

    // Deliver a voice tool-call result onto the LIVE realtime session that emitted the function call
    // (the relay keyed by <paramref name="transportLeaseId"/>), so the function_call_output lands on the
    // conversation that requested it and the spoken answer is relayed back to the caller. Returns true
    // when a live relay was found and the result was forwarded; false when no relay is attached for the
    // lease (e.g. a non-relayed transport or cross-host topology), so the caller can fall back instead of
    // silently dropping the result.
    Task<bool> TrySendToolResultAsync(
        string transportLeaseId,
        string callId,
        string resultJson,
        CancellationToken ct = default);
}

public sealed class VoiceVolatileMediaStreamUnavailableException()
    : NotSupportedException(Reason)
{
    public const string Reason = "remote_audio_transport_unavailable";
}
