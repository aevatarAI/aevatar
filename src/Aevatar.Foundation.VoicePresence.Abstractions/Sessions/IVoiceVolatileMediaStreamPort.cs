namespace Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

public interface IVoiceVolatileMediaStreamPort
{
    bool SupportsRemoteAudio { get; }

    Task<VoiceTransportLifetimeCompleted?> AttachAsync(
        VoicePresenceSessionLeaseHandle handle,
        IVoiceTransport transport,
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
}

public sealed class VoiceVolatileMediaStreamUnavailableException()
    : NotSupportedException(Reason)
{
    public const string Reason = "remote_audio_transport_unavailable";
}
