using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Aevatar.Foundation.VoicePresence.Transport;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Host-side voice session contract used by WebSocket and WebRTC transports.
/// </summary>
public sealed class VoicePresenceSession
{
    private readonly Func<bool> _isInitialized;
    private readonly Func<bool> _isTransportAttached;
    private readonly Func<IVoiceTransport, CancellationToken, Task> _attachTransportAsync;
    private readonly Func<IVoiceTransport?, CancellationToken, Task> _detachTransportAsync;

    public VoicePresenceSession(
        VoicePresenceModule module,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        int pcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(selfEventDispatcher);

        Module = module;
        SelfEventDispatcher = selfEventDispatcher;
        PcmSampleRateHz = pcmSampleRateHz;
        _isInitialized = () => module.IsInitialized;
        _isTransportAttached = () => module.IsTransportAttached;
        _attachTransportAsync = (transport, _) =>
        {
            module.AttachTransport(transport, selfEventDispatcher);
            return Task.CompletedTask;
        };
        _detachTransportAsync = (expectedTransport, _) => module.DetachTransportAsync(expectedTransport);
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
