using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Host-side voice session contract used by WebSocket and WebRTC transports.
/// </summary>
// Refactor (iter114/cluster-114-voice-presence-session-runtime-state):
//   Old pattern: VoicePresenceModule stores transport pump, provider session, provider key, dispatcher delegate, and mutable lease epoch in process-local module fields.
//   New principle: Delete direct VoicePresenceModule attach bridge and route attach through existing actor-owned attachment port; default DI returns honest unsupported.
public sealed class VoicePresenceSession
{
    private const string DetachedReason = "host_transport_detached";
    private const string CompletedReason = "host_transport_completed";
    private const bool ActorOwnedLeaseInitializedForAttach = true;
    private const bool ActorOwnedLeaseTransportAttached = false;
    private readonly Func<bool> _isInitialized;
    private readonly Func<bool> _isTransportAttached;
    private readonly Func<IVoiceTransport, CancellationToken, Task<VoiceTransportLifetimeCompleted?>> _attachTransportAsync;
    private readonly Func<IVoiceTransport?, CancellationToken, Task> _detachTransportAsync;
    private readonly Func<VoiceTransportLifetimeCompleted?, CancellationToken, Task> _completeTransportLifetimeAsync;
    private readonly VoicePresenceSessionLeaseHandle? _leaseHandle;

    // Refactor (iter51/issue-888-voice-presence-lease-ack-snapshot):
    //   Old pattern: lease ACK returned VoicePresenceSession bound to pre-lease capability snapshot; endpoint accept/reject closed over stale transport facts.
    //   New principle: lease ACK only signals inbox receipt; attach readiness is a separate signal; resolver preflights capability and returns typed sentinel (Unsupported/PreflightFailed/PendingAttach/Attached); endpoint maps typed sentinel, not boolean closure.
    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    public VoicePresenceSession(
        VoicePresenceCapabilitySnapshot capability,
        VoicePresenceSessionLeaseHandle leaseHandle,
        IVoicePresenceSessionLeasePort leasePort,
        IVoicePresenceTransportAttachmentPort transportAttachmentPort)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(leaseHandle);
        ArgumentNullException.ThrowIfNull(leasePort);
        ArgumentNullException.ThrowIfNull(transportAttachmentPort);

        PcmSampleRateHz = capability.PcmSampleRateHz;
        _leaseHandle = leaseHandle;
        _isInitialized = static () => ActorOwnedLeaseInitializedForAttach;
        _isTransportAttached = static () => ActorOwnedLeaseTransportAttached;
        _attachTransportAsync = async (transport, ct) =>
        {
            await transportAttachmentPort.AttachAsync(leaseHandle, transport, ct);
            return null;
        };
        _detachTransportAsync = async (expectedTransport, ct) =>
        {
            await transportAttachmentPort.DetachAsync(leaseHandle, expectedTransport, ct);
            await leasePort.ReleaseAsync(leaseHandle, DetachedReason, ct);
        };
        _completeTransportLifetimeAsync = (completed, ct) =>
        {
            var transportLeaseId = string.IsNullOrWhiteSpace(completed?.TransportLeaseId)
                ? leaseHandle.ActiveTransportLeaseId
                : completed.TransportLeaseId;
            return string.IsNullOrWhiteSpace(transportLeaseId)
                ? Task.CompletedTask
                : leasePort.CompleteTransportLifetimeAsync(
                    leaseHandle,
                    transportLeaseId,
                    CompletedReason,
                    ct);
        };
    }

    internal static VoicePresenceSession CreateAttachedForDetach(
        VoicePresenceCapabilitySnapshot capability,
        VoicePresenceSessionLeaseHandle leaseHandle,
        IVoicePresenceSessionLeasePort leasePort,
        IVoicePresenceTransportAttachmentPort transportAttachmentPort)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(leaseHandle);
        ArgumentNullException.ThrowIfNull(leasePort);
        ArgumentNullException.ThrowIfNull(transportAttachmentPort);

        return new VoicePresenceSession(
            isInitialized: () => capability.Initialized,
            isTransportAttached: () => true,
            attachTransportAsync: static (_, _) => throw new InvalidOperationException("Voice transport already attached."),
            detachTransportAsync: async (expectedTransport, ct) =>
            {
                await transportAttachmentPort.DetachAsync(leaseHandle, expectedTransport, ct);
                await leasePort.ReleaseAsync(leaseHandle, DetachedReason, ct);
            },
            capability.PcmSampleRateHz,
            completeTransportLifetimeAsync: (completed, ct) =>
            {
                var transportLeaseId = string.IsNullOrWhiteSpace(completed?.TransportLeaseId)
                    ? leaseHandle.ActiveTransportLeaseId
                    : completed.TransportLeaseId;
                return string.IsNullOrWhiteSpace(transportLeaseId)
                    ? Task.CompletedTask
                    : leasePort.CompleteTransportLifetimeAsync(
                        leaseHandle,
                        transportLeaseId,
                        CompletedReason,
                        ct);
            });
    }

    public VoicePresenceSession(
        Func<bool> isInitialized,
        Func<bool> isTransportAttached,
        Func<IVoiceTransport, CancellationToken, Task> attachTransportAsync,
        Func<IVoiceTransport?, CancellationToken, Task> detachTransportAsync,
        int pcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz,
        Func<VoiceTransportLifetimeCompleted?, CancellationToken, Task>? completeTransportLifetimeAsync = null,
        Func<IVoiceTransport, CancellationToken, Task<VoiceTransportLifetimeCompleted?>>? attachTransportAndBuildLifetimeAsync = null)
    {
        _isInitialized = isInitialized ?? throw new ArgumentNullException(nameof(isInitialized));
        _isTransportAttached = isTransportAttached ?? throw new ArgumentNullException(nameof(isTransportAttached));
        ArgumentNullException.ThrowIfNull(attachTransportAsync);
        _attachTransportAsync = attachTransportAndBuildLifetimeAsync ?? (async (transport, ct) =>
        {
            await attachTransportAsync(transport, ct);
            return null;
        });
        _detachTransportAsync = detachTransportAsync ?? throw new ArgumentNullException(nameof(detachTransportAsync));
        _completeTransportLifetimeAsync = completeTransportLifetimeAsync ?? ((_, _) => Task.CompletedTask);
        PcmSampleRateHz = pcmSampleRateHz;
    }

    public VoicePresenceSessionLeaseHandle? LeaseHandle => _leaseHandle;

    public int PcmSampleRateHz { get; }

    public bool IsInitialized => _isInitialized();

    public bool IsTransportAttached => _isTransportAttached();

    public Task<VoiceTransportLifetimeCompleted?> AttachTransportAsync(IVoiceTransport transport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return _attachTransportAsync(transport, ct);
    }

    public Task DetachTransportAsync(IVoiceTransport? expectedTransport = null, CancellationToken ct = default) =>
        _detachTransportAsync(expectedTransport, ct);

    internal Task CompleteTransportLifetimeAsync(VoiceTransportLifetimeCompleted? completed, CancellationToken ct = default)
    {
        return _completeTransportLifetimeAsync(completed, ct);
    }

    internal Task CompleteTransportLifetimeAsync(IVoiceTransport expectedTransport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedTransport);
        return _completeTransportLifetimeAsync(null, ct);
    }
}
