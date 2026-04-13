using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Events;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Modules;

/// <summary>
/// EventModule skeleton for phase-1 voice presence state management.
/// </summary>
public sealed class VoicePresenceModule : ILifecycleAwareEventModule, IAudioFastPath
{
    private readonly IRealtimeVoiceProvider _provider;
    private readonly VoiceProviderConfig _providerConfig;
    private readonly VoiceSessionConfig? _sessionConfig;
    private readonly VoicePresenceModuleOptions _options;

    public VoicePresenceModule(
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig? sessionConfig = null,
        VoicePresenceModuleOptions? options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig?.Clone() ?? throw new ArgumentNullException(nameof(providerConfig));
        _sessionConfig = sessionConfig?.Clone();
        _options = options ?? new VoicePresenceModuleOptions();
        StateMachine = new VoicePresenceStateMachine();
        EventPolicy = new VoicePresenceEventPolicy
        {
            StaleAfter = _options.StaleAfter,
            DedupeWindow = _options.DedupeWindow,
        };
    }

    public string Name => _options.Name;

    public int Priority => _options.Priority;

    public VoicePresenceStateMachine StateMachine { get; }

    public VoicePresenceEventPolicy EventPolicy { get; }

    public bool IsInitialized { get; private set; }

    public bool CanHandle(EventEnvelope envelope) =>
        envelope.Payload?.Is(VoiceProviderEvent.Descriptor) == true ||
        envelope.Payload?.Is(VoiceControlFrame.Descriptor) == true;

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        _ = ctx;
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(VoiceProviderEvent.Descriptor))
        {
            await HandleProviderEventAsync(envelope.Payload.Unpack<VoiceProviderEvent>(), ct);
            return;
        }

        if (envelope.Payload.Is(VoiceControlFrame.Descriptor))
            HandleControlFrame(envelope.Payload.Unpack<VoiceControlFrame>());
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsInitialized)
            return;

        await _provider.ConnectAsync(_providerConfig, ct);
        if (_sessionConfig != null)
            await _provider.UpdateSessionAsync(_sessionConfig, ct);

        IsInitialized = true;
    }

    public async ValueTask DisposeAsync()
    {
        IsInitialized = false;
        await _provider.DisposeAsync();
    }

    public bool CanHandleAudio(VoiceAudioFastPathFrame frame) =>
        string.IsNullOrWhiteSpace(_options.LinkId) || string.Equals(_options.LinkId, frame.LinkId, StringComparison.Ordinal);

    public Task HandleAudioAsync(VoiceAudioFastPathFrame frame, CancellationToken ct)
    {
        if (!CanHandleAudio(frame))
        {
            throw new InvalidOperationException(
                $"VoicePresenceModule cannot handle audio for link '{frame.LinkId}'.");
        }

        return _provider.SendAudioAsync(frame.Pcm16, ct);
    }

    private async Task HandleProviderEventAsync(VoiceProviderEvent providerEvent, CancellationToken ct)
    {
        switch (providerEvent.EventCase)
        {
            case VoiceProviderEvent.EventOneofCase.ResponseStarted:
                StateMachine.OnResponseStarted(providerEvent.ResponseStarted.ResponseId);
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseDone:
                StateMachine.OnResponseDone(providerEvent.ResponseDone.ResponseId);
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                StateMachine.OnResponseCancelled(providerEvent.ResponseCancelled.ResponseId);
                break;
            case VoiceProviderEvent.EventOneofCase.SpeechStarted:
            {
                var wasInProgress = StateMachine.State == VoicePresenceState.ResponseInProgress;
                StateMachine.OnSpeechStarted();
                if (wasInProgress)
                    await _provider.CancelResponseAsync(ct);
                break;
            }
            case VoiceProviderEvent.EventOneofCase.SpeechStopped:
                StateMachine.OnSpeechStopped();
                break;
            case VoiceProviderEvent.EventOneofCase.Disconnected:
                StateMachine.OnProviderDisconnected();
                break;
            case VoiceProviderEvent.EventOneofCase.AudioReceived:
            case VoiceProviderEvent.EventOneofCase.FunctionCall:
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                break;
        }
    }

    private void HandleControlFrame(VoiceControlFrame frame)
    {
        switch (frame.FrameCase)
        {
            case VoiceControlFrame.FrameOneofCase.DrainAcknowledged:
                StateMachine.OnDrainAcknowledged(
                    frame.DrainAcknowledged.ResponseId,
                    frame.DrainAcknowledged.PlayoutSequence);
                break;
            case VoiceControlFrame.FrameOneofCase.None:
            default:
                break;
        }
    }
}
