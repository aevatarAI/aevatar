using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;

namespace Aevatar.Foundation.VoicePresence;

/// <summary>
/// Registration entry used by <see cref="VoicePresenceModuleFactory"/> to create
/// voice-presence modules by configured names.
/// </summary>
public sealed class VoicePresenceModuleRegistration
{
    public VoicePresenceModuleRegistration(
        IEnumerable<string> names,
        Func<IServiceProvider, VoicePresenceModule> create,
        int? pcmSampleRateHz = null)
        : this(
            names,
            create,
            ThrowProviderSessionUnavailableAsync,
            pcmSampleRateHz)
    {
    }

    public VoicePresenceModuleRegistration(
        IEnumerable<string> names,
        Func<IServiceProvider, VoicePresenceModule> create,
        Func<IServiceProvider, VoicePresenceSessionLeaseHandle, Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task>, Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task>, CancellationToken, Task<RealtimeVoiceProviderSession>> connectProviderSession,
        int? pcmSampleRateHz = null)
        : this(
            names,
            (services, _) => create(services),
            connectProviderSession,
            pcmSampleRateHz)
    {
    }

    public VoicePresenceModuleRegistration(
        IEnumerable<string> names,
        Func<IServiceProvider, string, VoicePresenceModule> create,
        int? pcmSampleRateHz = null)
        : this(
            names,
            create,
            ThrowProviderSessionUnavailableAsync,
            pcmSampleRateHz)
    {
    }

    public VoicePresenceModuleRegistration(
        IEnumerable<string> names,
        Func<IServiceProvider, string, VoicePresenceModule> create,
        Func<IServiceProvider, VoicePresenceSessionLeaseHandle, Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task>, Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task>, CancellationToken, Task<RealtimeVoiceProviderSession>> connectProviderSession,
        int? pcmSampleRateHz = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        Create = create ?? throw new ArgumentNullException(nameof(create));
        ConnectProviderSession = connectProviderSession ?? throw new ArgumentNullException(nameof(connectProviderSession));

        Names = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (Names.Count == 0)
            throw new ArgumentException("At least one module name is required.", nameof(names));

        PcmSampleRateHz = pcmSampleRateHz ?? Transport.WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz;
    }

    public IReadOnlyList<string> Names { get; }

    public int PcmSampleRateHz { get; }

    public Func<IServiceProvider, string, VoicePresenceModule> Create { get; }

    public Func<IServiceProvider, VoicePresenceSessionLeaseHandle, Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task>, Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task>, CancellationToken, Task<RealtimeVoiceProviderSession>> ConnectProviderSession { get; }

    public Task<RealtimeVoiceProviderSession> ConnectProviderSessionAsync(
        IServiceProvider serviceProvider,
        VoicePresenceSessionLeaseHandle handle,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct = default) =>
        ConnectProviderSession(serviceProvider, handle, eventSink, audioSink, ct);

    private static Task<RealtimeVoiceProviderSession> ThrowProviderSessionUnavailableAsync(
        IServiceProvider serviceProvider,
        VoicePresenceSessionLeaseHandle handle,
        Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
        Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
        CancellationToken ct)
    {
        _ = serviceProvider;
        _ = handle;
        _ = eventSink;
        _ = audioSink;
        _ = ct;
        throw new VoiceVolatileMediaStreamUnavailableException();
    }
}
