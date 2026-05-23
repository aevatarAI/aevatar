using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Events;
using Aevatar.Foundation.VoicePresence.Transport;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.Json;

namespace Aevatar.Foundation.VoicePresence.Modules;

/// <summary>
/// EventModule for voice presence. Bridges user-side <see cref="IVoiceTransport"/>
/// with <see cref="IRealtimeVoiceProvider"/>. Audio flows directly between the two
/// transports without entering the grain inbox or event pipeline. Only control events
/// (state transitions, tool calls, drain ack) are dispatched as actor events.
/// </summary>
// Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
//   Old pattern: VoicePresenceModule 在 module 内持有 process-local background state(unbounded channels / TaskCompletionSource waiters / 静态字段持 lifecycle),还保留 disabled remote voice fallback shell;违反 Actor 单线程事实源 + 中间层状态约束。
//   New principle: Reuse existing RoleGAgent state for voice runtime facts(typed protobuf sub-state in RoleGAgent state);transport handles 仅作 volatile process-local lease(non-fact source);provider callbacks 走 typed self-signals(self-message 到 actor inbox);**删除** disabled remote voice fallback shell。无新 actor type / 新 envelope kind。
public sealed class VoicePresenceModule : ILifecycleAwareEventModule, IAudioFastPath, IRouteBypassModule
{
    private static readonly JsonFormatter PayloadJsonFormatter = new(JsonFormatter.Settings.Default);
    private const int DefaultLastDrainAckResponseId = -1;
    private const long DefaultLastDrainAckPlayoutSequence = -1;

    private readonly IRealtimeVoiceProvider _provider;
    private readonly VoiceProviderConfig _providerConfig;
    private readonly VoiceSessionConfig? _sessionConfig;
    private readonly VoicePresenceModuleOptions _options;
    private readonly IVoiceToolInvoker? _toolInvoker;
    private readonly IVoiceToolCatalog? _toolCatalog;
    private readonly ILogger _logger;

    private VoicePresenceRuntimeState _runtimeState = CreateInitialRuntimeState();
    private IVoiceTransport? _userTransport;
    private Func<IMessage, CancellationToken, Task>? _selfEventDispatcher;
    private CancellationTokenSource? _relayCts;
    private Task? _userToProviderRelay;
    private Task? _providerToUserRelay;

    public VoicePresenceModule(
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig? sessionConfig = null,
        VoicePresenceModuleOptions? options = null,
        IVoiceToolInvoker? toolInvoker = null,
        IVoiceToolCatalog? toolCatalog = null,
        ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig?.Clone() ?? throw new ArgumentNullException(nameof(providerConfig));
        _sessionConfig = sessionConfig?.Clone();
        _options = options ?? new VoicePresenceModuleOptions();
        _toolInvoker = toolInvoker;
        _toolCatalog = toolCatalog;
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

    public int PcmSampleRateHz =>
        _sessionConfig is { SampleRateHz: > 0 }
            ? _sessionConfig.SampleRateHz
            : WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz;

    // ── IEventModule ──────────────────────────────────────────

    public bool CanHandle(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return false;

        return envelope.Payload.Is(VoiceModuleSignal.Descriptor) ||
               envelope.Payload.Is(VoiceProviderEvent.Descriptor) ||
               envelope.Payload.Is(VoiceControlFrame.Descriptor) ||
               envelope.Route?.IsPublication() == true;
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(VoiceModuleSignal.Descriptor))
        {
            await HandleModuleSignalAsync(envelope.Payload.Unpack<VoiceModuleSignal>(), ctx, ct);
            return;
        }

        if (envelope.Payload.Is(VoiceProviderEvent.Descriptor))
        {
            await HandleProviderEventAsync(envelope.Payload.Unpack<VoiceProviderEvent>(), ctx, ct);
            return;
        }

        if (envelope.Payload.Is(VoiceControlFrame.Descriptor))
        {
            await HandleControlFrameAsync(envelope.Payload.Unpack<VoiceControlFrame>(), ctx, ct);
            return;
        }

        await HandleExternalEventAsync(envelope, ctx, ct);
    }

    private async Task HandleModuleSignalAsync(
        VoiceModuleSignal signal,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (!MatchesModuleName(signal.ModuleName))
            return;

        switch (signal.SignalCase)
        {
            case VoiceModuleSignal.SignalOneofCase.ProviderEvent:
                await HandleProviderEventAsync(signal.ProviderEvent, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.ControlFrame:
                await HandleControlFrameAsync(signal.ControlFrame, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.RemoteSessionOpenRequested:
                await HandleRemoteSessionOpenRequestedAsync(signal.RemoteSessionOpenRequested, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.RemoteSessionCloseRequested:
                await HandleRemoteSessionCloseRequestedAsync(signal.RemoteSessionCloseRequested, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.RemoteControlInputReceived:
                await HandleRemoteControlInputReceivedAsync(signal.RemoteControlInputReceived, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.SessionLeaseRequested:
                await HandleSessionLeaseRequestedAsync(signal.SessionLeaseRequested, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.SessionLeaseReleased:
                await HandleSessionLeaseReleasedAsync(signal.SessionLeaseReleased, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.None:
            default:
                break;
        }
    }

    // ── ILifecycleAwareEventModule ────────────────────────────

    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsInitialized)
            return;

        await _provider.ConnectAsync(_providerConfig, ct);
        var effectiveSessionConfig = await BuildEffectiveSessionConfigAsync(ct);
        if (effectiveSessionConfig != null)
            await _provider.UpdateSessionAsync(effectiveSessionConfig, ct);

        IsInitialized = true;
        _runtimeState.Initialized = true;
        _runtimeState.PcmSampleRateHz = PcmSampleRateHz;
        _runtimeState.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;
        await FlushPendingEventInjectionsAsync(ct);
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
        _runtimeState = CreateInitialRuntimeState();
        _runtimeState.Initialized = false;
        _runtimeState.TransportAttached = false;
        _runtimeState.ActiveSessionId = string.Empty;
        RestoreStateMachineFromRuntimeState();
        _selfEventDispatcher = null;
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
    /// Control events are dispatched to the grain inbox via <paramref name="selfEventDispatcher"/>.
    /// </summary>
    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    public void AttachTransport(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher)
    {
        ArgumentNullException.ThrowIfNull(userTransport);
        ArgumentNullException.ThrowIfNull(selfEventDispatcher);

        if (_userTransport != null || !string.IsNullOrWhiteSpace(_runtimeState.RemoteSessionId))
            throw new InvalidOperationException("A voice transport is already attached.");

        _userTransport = userTransport;
        _selfEventDispatcher = selfEventDispatcher;
        _relayCts = new CancellationTokenSource();
        _runtimeState.TransportAttached = true;

        _provider.OnEvent = OnProviderEventAsync;
        _userToProviderRelay = RunUserToProviderRelayAsync(_relayCts.Token);
        _providerToUserRelay = Task.CompletedTask;
    }

    /// <summary>
    /// Detaches the current transport and stops the relay loops.
    /// </summary>
    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    public async Task DetachTransportAsync(IVoiceTransport? expectedTransport = null)
    {
        if (expectedTransport != null && !ReferenceEquals(expectedTransport, _userTransport))
            return;

        await StopRelayAsync();

        if (_userTransport != null)
        {
            await _userTransport.DisposeAsync();
            _userTransport = null;
        }

        _runtimeState.TransportAttached = false;
        _selfEventDispatcher = null;
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
                    await DispatchSelfEventAsync(frame.Control, ct);
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

        await DispatchSelfEventAsync(evt, ct);
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
        _provider.OnEvent = null;
        cts?.Dispose();
    }

    // ── State machine dispatch (used by both event pipeline and relay) ──

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: provider callbacks normalized ids against process-local dictionaries and volatile module fields.
    //   New principle: provider turns hydrate and persist the typed RoleGAgent voice runtime sub-state before mutating response/session facts.
    internal async Task HandleProviderEventAsync(
        VoiceProviderEvent providerEvent,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);
        EnsureSelfEventDispatcher(ctx);
        if (!TryNormalizeProviderEvent(providerEvent, out var normalizedEvent))
            return;

        var stateChanged = false;
        switch (normalizedEvent.EventCase)
        {
            case VoiceProviderEvent.EventOneofCase.ResponseStarted:
                _runtimeState.AwaitingInjectedResponseStart = false;
                StateMachine.OnResponseStarted(normalizedEvent.ResponseStarted.ResponseId);
                stateChanged = true;
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseDone:
                StateMachine.OnResponseDone(normalizedEvent.ResponseDone.ResponseId);
                RetireProviderResponse(normalizedEvent.ResponseDone.ProviderResponseId);
                stateChanged = true;
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                _runtimeState.AwaitingInjectedResponseStart = false;
                StateMachine.OnResponseCancelled(normalizedEvent.ResponseCancelled.ResponseId);
                RetireProviderResponse(normalizedEvent.ResponseCancelled.ProviderResponseId);
                stateChanged = true;
                await FlushPendingEventInjectionsAsync(ct);
                break;
            case VoiceProviderEvent.EventOneofCase.SpeechStarted:
            {
                var wasInProgress = StateMachine.State == VoicePresenceState.ResponseInProgress;
                if (wasInProgress)
                {
                    var responseId = StateMachine.CurrentResponseId;
                    var providerResponseId = _runtimeState.ActiveProviderResponseId;
                    await _provider.CancelResponseAsync(ct);
                    if (!string.IsNullOrWhiteSpace(providerResponseId))
                    {
                        if (!_runtimeState.CancelledProviderResponseIds.Contains(providerResponseId))
                            _runtimeState.CancelledProviderResponseIds.Add(providerResponseId);
                        RetireProviderResponse(providerResponseId);
                    }

                    StateMachine.OnResponseCancelled(responseId);
                }

                StateMachine.OnSpeechStarted();
                stateChanged = true;
                break;
            }
            case VoiceProviderEvent.EventOneofCase.SpeechStopped:
                StateMachine.OnSpeechStopped();
                stateChanged = true;
                break;
            case VoiceProviderEvent.EventOneofCase.FunctionCall:
                await ExecuteToolCallAsync(normalizedEvent.FunctionCall, ctx, ct);
                break;
            case VoiceProviderEvent.EventOneofCase.Disconnected:
                _runtimeState.AwaitingInjectedResponseStart = false;
                StateMachine.OnProviderDisconnected();
                _runtimeState.ProviderResponseBindings.Clear();
                _runtimeState.CancelledProviderResponseIds.Clear();
                _runtimeState.ActiveProviderResponseId = string.Empty;
                stateChanged = true;
                if (await CloseRemoteSessionAsync("provider_disconnected", ctx, ct))
                    stateChanged = false;
                break;
            case VoiceProviderEvent.EventOneofCase.AudioReceived:
                break;
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                break;
        }

        await PersistRuntimeStateIfChangedAsync(ctx, stateChanged, ct);
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: provider-specific receive loops suppressed and completed response epochs directly.
    //   New principle: this actor-turn normalizer owns cancellation suppression and response-id materialization.
    private bool TryNormalizeProviderEvent(
        VoiceProviderEvent providerEvent,
        out VoiceProviderEvent normalizedEvent)
    {
        normalizedEvent = providerEvent;
        switch (providerEvent.EventCase)
        {
            case VoiceProviderEvent.EventOneofCase.ResponseStarted:
                return TryNormalizeResponseEvent(
                    providerEvent.ResponseStarted,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { ResponseStarted = message },
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.ResponseDone:
                return TryNormalizeResponseEvent(
                    providerEvent.ResponseDone,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { ResponseDone = message },
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                return TryNormalizeResponseEvent(
                    providerEvent.ResponseCancelled,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { ResponseCancelled = message },
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.FunctionCall:
                return TryNormalizeResponseEvent(
                    providerEvent.FunctionCall,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { FunctionCall = message },
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.AudioReceived:
            {
                var audioReceived = providerEvent.AudioReceived;
                if (!string.IsNullOrWhiteSpace(audioReceived.ProviderResponseId) &&
                    _runtimeState.CancelledProviderResponseIds.Contains(audioReceived.ProviderResponseId))
                {
                    return false;
                }

                return true;
            }
            case VoiceProviderEvent.EventOneofCase.Disconnected:
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.SpeechStarted:
            case VoiceProviderEvent.EventOneofCase.SpeechStopped:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                return true;
        }
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: each response-shaped provider event repeated identity normalization inline.
    //   New principle: message-specific switch arms only select fields and wrappers; actor-turn mapping stays centralized.
    private bool TryNormalizeResponseEvent<TMessage>(
        TMessage source,
        Func<TMessage, string> getProviderResponseId,
        Func<TMessage, int> getResponseId,
        Action<TMessage, int> setResponseId,
        Func<TMessage, VoiceProviderEvent> buildEvent,
        out VoiceProviderEvent normalizedEvent)
        where TMessage : IMessage<TMessage>
    {
        var message = source.Clone();
        if (!TryNormalizeResponseIdentity(getProviderResponseId(message), getResponseId(message), out var responseId))
        {
            normalizedEvent = default!;
            return false;
        }

        setResponseId(message, responseId);
        normalizedEvent = buildEvent(message);
        return true;
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: providers allocated fallback response epochs when provider ids were missing.
    //   New principle: fallback actor response ids are allocated only by the module state machine turn.
    private bool TryNormalizeResponseIdentity(string providerResponseId, int suppliedResponseId, out int responseId)
    {
        if (!string.IsNullOrWhiteSpace(providerResponseId))
        {
            if (_runtimeState.CancelledProviderResponseIds.Contains(providerResponseId))
            {
                responseId = 0;
                return false;
            }

            responseId = GetOrCreateProviderResponse(providerResponseId, suppliedResponseId);
            return true;
        }

        if (suppliedResponseId > 0)
        {
            _runtimeState.NextResponseId = Math.Max(_runtimeState.NextResponseId, suppliedResponseId + 1);
            responseId = suppliedResponseId;
            return true;
        }

        responseId = AllocateNextResponseId();
        return true;
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: OpenAI/MiniCPM adapters owned provider-id to actor-epoch dictionaries and counters.
    //   New principle: provider-id to actor response-id mapping is actor runtime state owned by this module.
    private int GetOrCreateProviderResponse(string providerResponseId, int suppliedResponseId)
    {
        foreach (var binding in _runtimeState.ProviderResponseBindings)
        {
            if (string.Equals(binding.ProviderResponseId, providerResponseId, StringComparison.Ordinal))
                return binding.ResponseId;
        }

        var responseId = suppliedResponseId > 0 ? suppliedResponseId : AllocateNextResponseId();
        if (suppliedResponseId > 0)
            _runtimeState.NextResponseId = Math.Max(_runtimeState.NextResponseId, suppliedResponseId + 1);
        _runtimeState.ProviderResponseBindings.Add(new VoiceProviderResponseBinding
        {
            ProviderResponseId = providerResponseId,
            ResponseId = responseId,
        });
        _runtimeState.ActiveProviderResponseId = providerResponseId;
        return responseId;
    }

    private int AllocateNextResponseId()
    {
        var responseId = Math.Max(_runtimeState.NextResponseId, StateMachine.CurrentResponseId + 1);
        _runtimeState.NextResponseId = responseId + 1;
        StateMachine.OnResponseStarted(responseId);
        return responseId;
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: providers retired response epochs from background completion/cancel callbacks.
    //   New principle: the actor turn retires provider response mappings when committed lifecycle events arrive.
    private void RetireProviderResponse(string providerResponseId)
    {
        if (string.IsNullOrWhiteSpace(providerResponseId))
            return;

        for (var i = _runtimeState.ProviderResponseBindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_runtimeState.ProviderResponseBindings[i].ProviderResponseId, providerResponseId, StringComparison.Ordinal))
                _runtimeState.ProviderResponseBindings.RemoveAt(i);
        }

        if (string.Equals(_runtimeState.ActiveProviderResponseId, providerResponseId, StringComparison.Ordinal))
            _runtimeState.ActiveProviderResponseId = string.Empty;
    }

    private async Task DispatchSelfEventAsync(IMessage message, CancellationToken ct)
    {
        var dispatcher = _selfEventDispatcher;
        if (dispatcher == null)
            return;

        try
        {
            await dispatcher(message, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch voice self event {MessageType}.", message.GetType().Name);
        }
    }

    private void EnsureSelfEventDispatcher(IEventHandlerContext ctx)
    {
        if (_selfEventDispatcher != null)
            return;

        var dispatchPort = ctx.Services.GetService<IActorDispatchPort>();
        if (dispatchPort == null)
            return;

        _selfEventDispatcher = (message, token) => dispatchPort.DispatchAsync(
            ctx.AgentId,
            Hosting.VoicePresenceSessionDispatch.BuildSelfEnvelope(ctx.AgentId, Name, message),
            token);
    }

    private bool MatchesModuleName(string? moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName) &&
        string.Equals(Name, moduleName, StringComparison.OrdinalIgnoreCase);

    private async Task HandleRemoteSessionOpenRequestedAsync(
        VoiceRemoteSessionOpenRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);
        EnsureSelfEventDispatcher(ctx);
        if (string.IsNullOrWhiteSpace(request.SessionId))
            return;

        if (!IsInitialized)
        {
            await PublishRemoteOutputAsync(
                new VoiceRemoteTransportOutput
                {
                    ModuleName = Name,
                    SessionId = request.SessionId,
                    SessionClosed = new VoiceRemoteSessionClosed
                    {
                        Reason = "module_not_initialized",
                    },
                },
                ctx,
                ct);
            return;
        }

        if (_userTransport != null ||
            (!string.IsNullOrWhiteSpace(_runtimeState.RemoteSessionId) &&
             !string.Equals(_runtimeState.RemoteSessionId, request.SessionId, StringComparison.Ordinal)))
        {
            await PublishRemoteOutputAsync(
                new VoiceRemoteTransportOutput
                {
                    ModuleName = Name,
                    SessionId = request.SessionId,
                    SessionClosed = new VoiceRemoteSessionClosed
                    {
                        Reason = "transport_already_attached",
                    },
                },
                ctx,
                ct);
            return;
        }

        _runtimeState.RemoteSessionId = request.SessionId;
        _runtimeState.ActiveSessionId = request.SessionId;
        _runtimeState.TransportAttached = _userTransport != null;
        await PersistRuntimeStateAsync(ctx, ct);
        _provider.OnEvent = OnProviderEventAsync;
    }

    private async Task HandleRemoteSessionCloseRequestedAsync(
        VoiceRemoteSessionCloseRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);
        var currentSessionId = _runtimeState.RemoteSessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
            return;

        if (!string.IsNullOrWhiteSpace(request.SessionId) &&
            !string.Equals(currentSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        await CloseRemoteSessionAsync(
            string.IsNullOrWhiteSpace(request.Reason) ? "remote_session_closed" : request.Reason,
            ctx,
            ct);
    }

    private async Task HandleRemoteControlInputReceivedAsync(
        VoiceRemoteControlInputReceived request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);
        if (string.IsNullOrWhiteSpace(_runtimeState.RemoteSessionId) ||
            !string.Equals(_runtimeState.RemoteSessionId, request.SessionId, StringComparison.Ordinal) ||
            request.ControlFrame == null)
        {
            return;
        }

        await HandleControlFrameAsync(request.ControlFrame, ctx, ct);
    }

    private async Task<bool> CloseRemoteSessionAsync(
        string reason,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var currentSessionId = _runtimeState.RemoteSessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
            return false;

        _runtimeState.RemoteSessionId = string.Empty;
        _runtimeState.ActiveSessionId = string.Empty;
        _runtimeState.TransportAttached = false;
        _runtimeState.ProviderResponseBindings.Clear();
        _runtimeState.CancelledProviderResponseIds.Clear();
        _runtimeState.ActiveProviderResponseId = string.Empty;
        _provider.OnEvent = _userTransport == null ? null : OnProviderEventAsync;
        await PersistRuntimeStateAsync(ctx, ct);
        await PublishRemoteOutputAsync(
            new VoiceRemoteTransportOutput
            {
                ModuleName = Name,
                SessionId = currentSessionId,
                SessionClosed = new VoiceRemoteSessionClosed
                {
                    Reason = reason,
                },
            },
            ctx,
            ct);
        return true;
    }

    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    internal async Task HandleSessionLeaseRequestedAsync(
        VoicePresenceSessionLeaseRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);
        if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
            return;

        var currentSessionId = _runtimeState.ActiveSessionId;
        if (!string.IsNullOrWhiteSpace(currentSessionId) &&
            !string.Equals(currentSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _runtimeState.Initialized = IsInitialized;
        _runtimeState.TransportAttached = _userTransport != null;
        _runtimeState.PcmSampleRateHz = PcmSampleRateHz;
        _runtimeState.ActiveSessionId = request.SessionId;
        _runtimeState.LeaseExpiresAt = request.ExpiresAt?.Clone();
        _runtimeState.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;
        await PersistRuntimeStateAsync(ctx, ct);
    }

    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    internal async Task HandleSessionLeaseReleasedAsync(
        VoicePresenceSessionLeaseReleased request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);
        if (request == null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            !string.Equals(_runtimeState.ActiveSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _runtimeState.ActiveSessionId = string.Empty;
        _runtimeState.LeaseExpiresAt = null;
        _runtimeState.TransportAttached = _userTransport != null;
        await PersistRuntimeStateAsync(ctx, ct);
    }

    private Task PublishRemoteOutputAsync(
        VoiceRemoteTransportOutput output,
        IEventHandlerContext ctx,
        CancellationToken ct) =>
        ctx.PublishAsync(output, TopologyAudience.Self, ct);

    private async Task ExecuteToolCallAsync(
        VoiceFunctionCallRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var invoker = _toolInvoker ?? ctx.Services.GetService<IVoiceToolInvoker>();
        var resultJson = "{}";

        if (invoker == null)
        {
            resultJson = BuildToolErrorJson($"tool '{request.ToolName}' is not available");
        }
        else
        {
            CancellationTokenSource? timeoutCts = null;

            try
            {
                var executionToken = ct;
                if (_options.ToolExecutionTimeout > TimeSpan.Zero)
                {
                    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_options.ToolExecutionTimeout);
                    executionToken = timeoutCts.Token;
                }

                resultJson = await invoker.ExecuteAsync(
                    request.ToolName,
                    string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
                    executionToken);

                if (string.IsNullOrWhiteSpace(resultJson))
                    resultJson = "{}";
            }
            catch (OperationCanceledException) when (
                !ct.IsCancellationRequested &&
                timeoutCts is { IsCancellationRequested: true })
            {
                resultJson = BuildToolErrorJson(
                    $"tool '{request.ToolName}' timed out after {(int)_options.ToolExecutionTimeout.TotalMilliseconds} ms");
            }
            catch (Exception ex)
            {
                resultJson = BuildToolErrorJson(
                    $"tool '{request.ToolName}' execution failed: {ex.Message}");
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }

        await _provider.SendToolResultAsync(request.CallId, resultJson, ct);
    }

    private async Task<VoiceSessionConfig?> BuildEffectiveSessionConfigAsync(CancellationToken ct)
    {
        var effectiveSession = _sessionConfig?.Clone();
        if (_toolCatalog == null)
            return effectiveSession;

        IReadOnlyList<VoiceToolDefinition> discoveredTools;
        try
        {
            discoveredTools = await _toolCatalog.DiscoverAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice tool discovery failed during session initialization.");
            return effectiveSession;
        }

        if (discoveredTools.Count == 0)
            return effectiveSession;

        effectiveSession ??= new VoiceSessionConfig();
        var knownNames = new HashSet<string>(
            effectiveSession.ToolDefinitions
                .Select(static definition => definition.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var discoveredTool in discoveredTools)
        {
            var toolName = discoveredTool.Name?.Trim();
            if (string.IsNullOrWhiteSpace(toolName) || !knownNames.Add(toolName))
                continue;

            effectiveSession.ToolDefinitions.Add(new VoiceToolDefinition
            {
                Name = toolName,
                Description = discoveredTool.Description ?? string.Empty,
                ParametersSchema = string.IsNullOrWhiteSpace(discoveredTool.ParametersSchema)
                    ? "{}"
                    : discoveredTool.ParametersSchema,
            });
        }

        return effectiveSession;
    }

    private static string BuildToolErrorJson(string message) =>
        JsonSerializer.Serialize(new { error = message });

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: drain acks only updated the in-memory module state machine, so queued injections were lost after a fresh turn.
    //   New principle: control frames first hydrate actor-owned voice runtime state, then persist the post-drain injection fence.
    private async Task HandleControlFrameAsync(VoiceControlFrame frame, IEventHandlerContext ctx, CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);

        switch (frame.FrameCase)
        {
            case VoiceControlFrame.FrameOneofCase.DrainAcknowledged:
                StateMachine.OnDrainAcknowledged(
                    frame.DrainAcknowledged.ResponseId,
                    frame.DrainAcknowledged.PlayoutSequence);
                SyncRuntimeStateFromStateMachine();
                await FlushPendingEventInjectionsAsync(ct);
                await PersistRuntimeStateAsync(ctx, ct);
                break;
            case VoiceControlFrame.FrameOneofCase.None:
            default:
                break;
        }
    }

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: external publication injection checked only volatile module fields for pending/awaiting state.
    //   New principle: every injection decision starts from RoleGAgent-owned voice runtime state and persists the updated fence.
    private async Task HandleExternalEventAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        HydrateRuntimeStateFromActor(ctx);

        if (!ShouldInjectExternalEvent(envelope, ctx.AgentId))
            return;

        var now = _options.TimeProvider.GetUtcNow();
        var decision = EventPolicy.Evaluate(envelope, now);
        if (decision != VoicePresenceEventPolicyDecision.Admit)
            return;

        var injection = BuildPendingInjection(envelope, now);
        if (!IsReadyToInject())
        {
            EnqueuePendingInjection(injection);
            await PersistRuntimeStateAsync(ctx, ct);
            return;
        }

        if (await TryInjectEventAsync(injection, ct))
            await PersistRuntimeStateAsync(ctx, ct);
    }

    private bool ShouldInjectExternalEvent(EventEnvelope envelope, string agentId)
    {
        if (envelope.Payload == null)
            return false;

        if (envelope.Payload.Is(VoiceProviderEvent.Descriptor) ||
            envelope.Payload.Is(VoiceControlFrame.Descriptor) ||
            envelope.Payload.Is(VoiceModuleSignal.Descriptor) ||
            envelope.Payload.Is(VoiceRemoteTransportOutput.Descriptor))
        {
            return false;
        }

        if (envelope.Route?.IsPublication() != true)
            return false;

        return !string.Equals(envelope.Route.PublisherActorId, agentId, StringComparison.Ordinal);
    }

    private VoicePendingEventInjection BuildPendingInjection(EventEnvelope envelope, DateTimeOffset now)
    {
        var observedAt = envelope.Timestamp?.ToDateTimeOffset() ?? now;
        return new VoicePendingEventInjection
        {
            EnvelopeId = envelope.Id ?? string.Empty,
            PublisherActorId = envelope.Route?.PublisherActorId ?? string.Empty,
            EventType = envelope.Payload?.TypeUrl ?? string.Empty,
            Payload = envelope.Payload?.Clone(),
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        };
    }

    private void EnqueuePendingInjection(VoicePendingEventInjection injection)
    {
        if (_options.PendingInjectionCapacity <= 0)
            return;

        while (_runtimeState.PendingInjections.Count >= _options.PendingInjectionCapacity)
            _runtimeState.PendingInjections.RemoveAt(0);

        _runtimeState.PendingInjections.Add(injection);
    }

    private async Task FlushPendingEventInjectionsAsync(CancellationToken ct)
    {
        while (_runtimeState.PendingInjections.Count > 0 && IsReadyToInject())
        {
            var next = _runtimeState.PendingInjections[0];
            _runtimeState.PendingInjections.RemoveAt(0);
            if (IsExpired(next))
                continue;

            if (await TryInjectEventAsync(next, ct))
                return;

            return;
        }
    }

    private bool IsExpired(VoicePendingEventInjection injection)
    {
        var observedAt = injection.ObservedAt?.ToDateTimeOffset() ?? _options.TimeProvider.GetUtcNow();
        return _options.TimeProvider.GetUtcNow() - observedAt > _options.StaleAfter;
    }

    private bool IsReadyToInject() =>
        IsInitialized &&
        StateMachine.IsSafeToInject &&
        !_runtimeState.AwaitingInjectedResponseStart;

    private async Task<bool> TryInjectEventAsync(VoicePendingEventInjection injection, CancellationToken ct)
    {
        var providerInjection = BuildProviderInjection(injection);
        try
        {
            await _provider.InjectEventAsync(providerInjection, ct);
            _runtimeState.AwaitingInjectedResponseStart = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject external voice event {EventType}.", providerInjection.EventType);
            return false;
        }
    }

    private static VoiceConversationEventInjection BuildProviderInjection(VoicePendingEventInjection injection) =>
        new()
        {
            EnvelopeId = injection.EnvelopeId,
            PublisherActorId = injection.PublisherActorId,
            EventType = injection.EventType,
            PayloadJson = injection.Payload == null ? "{}" : FormatPayloadJson(injection.Payload),
            ObservedAt = injection.ObservedAt?.Clone(),
        };

    private static string FormatPayloadJson(Any payload)
    {
        try
        {
            var descriptor = ResolvePayloadDescriptor(payload.TypeUrl);
            if (descriptor?.Parser == null)
                return BuildOpaquePayloadJson(payload);

            var message = descriptor.Parser.ParseFrom(payload.Value);
            return PayloadJsonFormatter.Format(message);
        }
        catch
        {
            return BuildOpaquePayloadJson(payload);
        }
    }

    private static MessageDescriptor? ResolvePayloadDescriptor(string typeUrl)
    {
        if (string.IsNullOrWhiteSpace(typeUrl))
            return null;

        var typeName = typeUrl[(typeUrl.LastIndexOf('/') + 1)..];
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            System.Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(static t => t != null).Cast<System.Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (!typeof(IMessage).IsAssignableFrom(type))
                    continue;

                var descriptorProperty = type.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static);
                if (descriptorProperty?.GetValue(null) is MessageDescriptor descriptor &&
                    string.Equals(descriptor.FullName, typeName, StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }
        }

        return null;
    }

    private static string BuildOpaquePayloadJson(Any payload) =>
        JsonSerializer.Serialize(new
        {
            typeUrl = payload.TypeUrl,
            valueBase64 = payload.Value.IsEmpty ? string.Empty : Convert.ToBase64String(payload.Value.ToByteArray()),
        });

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: VoicePresenceModule reflected over local actor State/Persist members to find voice runtime facts.
    //   New principle: hydrate through the explicit actor-owned voice runtime state contract.
    private void HydrateRuntimeStateFromActor(IEventHandlerContext ctx)
    {
        if (ctx.Agent is not IVoicePresenceRuntimeStateOwner stateOwner ||
            !stateOwner.TryGetVoicePresenceRuntimeState(Name, out var stored))
            return;

        _runtimeState = NormalizeRuntimeState(stored);
        RestoreStateMachineFromRuntimeState();
    }

    private async Task PersistRuntimeStateIfChangedAsync(
        IEventHandlerContext ctx,
        bool stateChanged,
        CancellationToken ct)
    {
        if (!stateChanged)
            return;

        await PersistRuntimeStateAsync(ctx, ct);
    }

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: voice response bindings, remote session id, and pending injections lived only in module memory.
    //   New principle: synchronize runtime facts into the actor-owned protobuf sub-state through a narrow state-owner contract.
    private async Task PersistRuntimeStateAsync(IEventHandlerContext ctx, CancellationToken ct)
    {
        SyncRuntimeStateFromStateMachine();

        if (ctx.Agent is not IVoicePresenceRuntimeStateOwner stateOwner)
            return;

        await stateOwner.PersistVoicePresenceRuntimeStateAsync(Name, _runtimeState.Clone(), ct);
    }

    private void SyncRuntimeStateFromStateMachine()
    {
        _runtimeState.Status = ToRuntimeStatus(StateMachine.State);
        _runtimeState.CurrentResponseId = StateMachine.CurrentResponseId;
        _runtimeState.LastDrainAckResponseId = StateMachine.LastDrainAckResponseId;
        _runtimeState.LastDrainAckPlayoutSequence = StateMachine.LastDrainAckPlayoutSequence;
        _runtimeState.NextResponseId = Math.Max(_runtimeState.NextResponseId, StateMachine.CurrentResponseId + 1);
        _runtimeState.Initialized = IsInitialized;
        _runtimeState.TransportAttached = _userTransport != null;
        _runtimeState.PcmSampleRateHz = PcmSampleRateHz;
        if (_runtimeState.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified)
            _runtimeState.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;
    }

    private void RestoreStateMachineFromRuntimeState()
    {
        StateMachine.Restore(
            FromRuntimeStatus(_runtimeState.Status),
            _runtimeState.CurrentResponseId,
            _runtimeState.LastDrainAckResponseId,
            _runtimeState.LastDrainAckPlayoutSequence);
    }

    private static VoicePresenceRuntimeState NormalizeRuntimeState(VoicePresenceRuntimeState? state)
    {
        var normalized = state?.Clone() ?? CreateInitialRuntimeState();
        if (normalized.Status == VoicePresenceRuntimeStatus.Unspecified)
            normalized.Status = VoicePresenceRuntimeStatus.Idle;
        if (normalized.LastDrainAckResponseId == 0 && normalized.CurrentResponseId == 0)
            normalized.LastDrainAckResponseId = DefaultLastDrainAckResponseId;
        if (normalized.LastDrainAckPlayoutSequence == 0 && normalized.CurrentResponseId == 0)
            normalized.LastDrainAckPlayoutSequence = DefaultLastDrainAckPlayoutSequence;
        if (normalized.NextResponseId <= normalized.CurrentResponseId)
            normalized.NextResponseId = normalized.CurrentResponseId + 1;
        if (normalized.PcmSampleRateHz <= 0)
            normalized.PcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz;
        if (normalized.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified)
            normalized.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;

        return normalized;
    }

    private static VoicePresenceRuntimeState CreateInitialRuntimeState() =>
        new()
        {
            Status = VoicePresenceRuntimeStatus.Idle,
            LastDrainAckResponseId = DefaultLastDrainAckResponseId,
            LastDrainAckPlayoutSequence = DefaultLastDrainAckPlayoutSequence,
            NextResponseId = 1,
        };

    private static VoicePresenceRuntimeStatus ToRuntimeStatus(VoicePresenceState state) =>
        state switch
        {
            VoicePresenceState.UserSpeaking => VoicePresenceRuntimeStatus.UserSpeaking,
            VoicePresenceState.ResponseInProgress => VoicePresenceRuntimeStatus.ResponseInProgress,
            VoicePresenceState.AudioDraining => VoicePresenceRuntimeStatus.AudioDraining,
            _ => VoicePresenceRuntimeStatus.Idle,
        };

    private static VoicePresenceState FromRuntimeStatus(VoicePresenceRuntimeStatus state) =>
        state switch
        {
            VoicePresenceRuntimeStatus.UserSpeaking => VoicePresenceState.UserSpeaking,
            VoicePresenceRuntimeStatus.ResponseInProgress => VoicePresenceState.ResponseInProgress,
            VoicePresenceRuntimeStatus.AudioDraining => VoicePresenceState.AudioDraining,
            _ => VoicePresenceState.Idle,
        };
}
