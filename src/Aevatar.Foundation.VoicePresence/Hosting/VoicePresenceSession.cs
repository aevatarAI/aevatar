using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.VoicePresence.Transport;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Host-side voice session contract used by WebSocket and WebRTC transports.
/// </summary>
public sealed class VoicePresenceSession
{
    private const string DetachedReason = "host_transport_detached";
    private const bool ActorOwnedLeaseInitializedForAttach = true;
    private const bool ActorOwnedLeaseTransportAttached = false;
    private readonly Func<bool> _isInitialized;
    private readonly Func<bool> _isTransportAttached;
    private readonly Func<IVoiceTransport, CancellationToken, Task> _attachTransportAsync;
    private readonly Func<IVoiceTransport?, CancellationToken, Task> _detachTransportAsync;
    private readonly VoicePresenceSessionLeaseHandle? _leaseHandle;

    public VoicePresenceSession(
        VoicePresenceModule module,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        int pcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz)
        : this(module, selfEventDispatcher, null, pcmSampleRateHz)
    {
    }

    public VoicePresenceSession(
        VoicePresenceModule module,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        VoicePresenceSessionLeaseHandle? leaseHandle,
        int pcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(selfEventDispatcher);

        Module = module;
        SelfEventDispatcher = selfEventDispatcher;
        PcmSampleRateHz = pcmSampleRateHz;
        _leaseHandle = leaseHandle;
        _isInitialized = () => module.IsInitialized;
        _isTransportAttached = () => module.HasVolatileTransportLease;
        _attachTransportAsync = (transport, _) =>
            module.AttachTransportAsync(
                transport,
                selfEventDispatcher,
                leaseHandle?.SessionId,
                leaseHandle?.OwnerId,
                leaseHandle == null ? null : Timestamp.FromDateTimeOffset(leaseHandle.ExpiresAtUtc),
                _);
        _detachTransportAsync = (expectedTransport, _) => module.DetachTransportAsync(expectedTransport);
    }

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
        _attachTransportAsync = (transport, ct) =>
            transportAttachmentPort.AttachAsync(leaseHandle, transport, ct);
        _detachTransportAsync = async (expectedTransport, ct) =>
        {
            await transportAttachmentPort.DetachAsync(leaseHandle, expectedTransport, ct);
            await leasePort.ReleaseAsync(leaseHandle, DetachedReason, ct);
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
            capability.PcmSampleRateHz);
    }

    public VoicePresenceSession(
        Func<bool> isInitialized,
        Func<bool> isTransportAttached,
        Func<IVoiceTransport, CancellationToken, Task> attachTransportAsync,
        Func<IVoiceTransport?, CancellationToken, Task> detachTransportAsync,
        int pcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz,
        VoicePresenceModule? module = null,
        Func<IMessage, CancellationToken, Task>? selfEventDispatcher = null)
    {
        _isInitialized = isInitialized ?? throw new ArgumentNullException(nameof(isInitialized));
        _isTransportAttached = isTransportAttached ?? throw new ArgumentNullException(nameof(isTransportAttached));
        _attachTransportAsync = attachTransportAsync ?? throw new ArgumentNullException(nameof(attachTransportAsync));
        _detachTransportAsync = detachTransportAsync ?? throw new ArgumentNullException(nameof(detachTransportAsync));
        PcmSampleRateHz = pcmSampleRateHz;
        Module = module;
        SelfEventDispatcher = selfEventDispatcher;
    }

    public VoicePresenceModule? Module { get; }

    public Func<IMessage, CancellationToken, Task>? SelfEventDispatcher { get; }

    public VoicePresenceSessionLeaseHandle? LeaseHandle => _leaseHandle;

    public int PcmSampleRateHz { get; }

    public bool IsInitialized => _isInitialized();

    public bool IsTransportAttached => _isTransportAttached();

    public Task AttachTransportAsync(IVoiceTransport transport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return _attachTransportAsync(transport, ct);
    }

    public Task DetachTransportAsync(IVoiceTransport? expectedTransport = null, CancellationToken ct = default) =>
        _detachTransportAsync(expectedTransport, ct);
}
