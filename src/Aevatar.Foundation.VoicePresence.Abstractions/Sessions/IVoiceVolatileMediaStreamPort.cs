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

    // Deliver upstream provider actions onto the live realtime session keyed by transportLeaseId.
    // A false result means this host has no live relay for the lease. For attached leases, callers
    // must report the delivery gap instead of opening a replacement provider connection.
    Task<bool> TryCancelResponseAsync(
        string transportLeaseId,
        CancellationToken ct = default);

    Task<bool> TrySendInputImageAsync(
        string transportLeaseId,
        VoiceInputImage inputImage,
        CancellationToken ct = default);

    Task<bool> TrySendToolResultAsync(
        string transportLeaseId,
        string callId,
        string resultJson,
        CancellationToken ct = default);

    Task<bool> TryInjectEventAsync(
        string transportLeaseId,
        VoiceConversationEventInjection injection,
        CancellationToken ct = default);
}

public sealed class VoiceVolatileMediaStreamUnavailableException()
    : NotSupportedException(Reason)
{
    public const string Reason = "remote_audio_transport_unavailable";
}
