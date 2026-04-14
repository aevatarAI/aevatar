using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Events;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Modules;

/// <summary>
/// EventModule for voice presence. Bridges user-side <see cref="IVoiceTransport"/>
/// with <see cref="IRealtimeVoiceProvider"/>. Audio flows directly between the two
/// transports without entering the grain inbox or event pipeline. Only control events
/// (state transitions, tool calls, drain ack) are dispatched as actor events.
/// </summary>
public sealed class VoicePresenceModule : ILifecycleAwareEventModule, IAudioFastPath
{
    private readonly IRealtimeVoiceProvider _provider;
    private readonly VoiceProviderConfig _providerConfig;
    private readonly VoiceSessionConfig? _sessionConfig;
    private readonly VoicePresenceModuleOptions _options;
    private readonly ILogger _logger;

    private IVoiceTransport? _userTransport;
    private Func<VoiceProviderEvent, CancellationToken, Task>? _controlEventDispatcher;
    private CancellationTokenSource? _relayCts;
    private Task? _userToProviderRelay;
    private Task? _providerToUserRelay;

    public VoicePresenceModule(
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig? sessionConfig = null,
        VoicePresenceModuleOptions? options = null,
        ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig?.Clone() ?? throw new ArgumentNullException(nameof(providerConfig));
        _sessionConfig = sessionConfig?.Clone();
        _options = options ?? new VoicePresenceModuleOptions();
        _logger = logger ?? NullLogger.Instance;
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

    public bool IsTransportAttached => _userTransport != null;

    // ── IEventModule ──────────────────────────────────────────

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

    // ── ILifecycleAwareEventModule ────────────────────────────

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
        await StopRelayAsync();

        if (_userTransport != null)
        {
            await _userTransport.DisposeAsync();
            _userTransport = null;
        }

        await _provider.DisposeAsync();
    }

    // ── IAudioFastPath (Phase 1 legacy, still usable for non-transport callers) ──

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

    // ── Phase 3: Transport attachment + bidirectional relay ──

    /// <summary>
    /// Attaches a user-side voice transport and starts bidirectional audio relay.
    /// Audio flows directly between transport and provider (no grain inbox).
    /// Control events are dispatched to the grain inbox via <paramref name="controlEventDispatcher"/>.
    /// </summary>
    public void AttachTransport(
        IVoiceTransport userTransport,
        Func<VoiceProviderEvent, CancellationToken, Task> controlEventDispatcher)
    {
        ArgumentNullException.ThrowIfNull(userTransport);
        ArgumentNullException.ThrowIfNull(controlEventDispatcher);

        if (_userTransport != null)
            throw new InvalidOperationException("A voice transport is already attached.");

        _userTransport = userTransport;
        _controlEventDispatcher = controlEventDispatcher;
        _relayCts = new CancellationTokenSource();

        _provider.OnEvent = OnProviderEventAsync;
        _userToProviderRelay = RunUserToProviderRelayAsync(_relayCts.Token);
        _providerToUserRelay = Task.CompletedTask;
    }

    /// <summary>
    /// Detaches the current transport and stops the relay loops.
    /// </summary>
    public async Task DetachTransportAsync()
    {
        await StopRelayAsync();

        if (_userTransport != null)
        {
            await _userTransport.DisposeAsync();
            _userTransport = null;
        }

        _controlEventDispatcher = null;
    }

    private async Task RunUserToProviderRelayAsync(CancellationToken ct)
    {
        var transport = _userTransport;
        if (transport == null) return;

        try
        {
            await foreach (var frame in transport.ReceiveFramesAsync(ct))
            {
                if (frame.IsAudio)
                {
                    if (!frame.AudioPcm16.IsEmpty)
                        await _provider.SendAudioAsync(frame.AudioPcm16, ct);
                }
                else if (frame.Control != null)
                {
                    HandleControlFrame(frame.Control);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User-to-provider relay terminated unexpectedly.");
        }
    }

    private async Task OnProviderEventAsync(VoiceProviderEvent evt, CancellationToken ct)
    {
        if (evt.EventCase == VoiceProviderEvent.EventOneofCase.AudioReceived &&
            _userTransport != null)
        {
            try
            {
                await _userTransport.SendAudioAsync(evt.AudioReceived.Pcm16.Memory, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send audio to user transport.");
            }

            return;
        }

        var dispatcher = _controlEventDispatcher;
        if (dispatcher != null)
        {
            try
            {
                await dispatcher(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch provider control event {EventCase}.", evt.EventCase);
            }
        }
    }

    private async Task StopRelayAsync()
    {
        var cts = _relayCts;
        _relayCts = null;
        cts?.Cancel();

        if (_userToProviderRelay != null)
        {
            try { await _userToProviderRelay; }
            catch (OperationCanceledException) { }
        }

        if (_providerToUserRelay != null)
        {
            try { await _providerToUserRelay; }
            catch (OperationCanceledException) { }
        }

        _userToProviderRelay = null;
        _providerToUserRelay = null;
        cts?.Dispose();
    }

    // ── State machine dispatch (used by both event pipeline and relay) ──

    internal async Task HandleProviderEventAsync(VoiceProviderEvent providerEvent, CancellationToken ct)
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
