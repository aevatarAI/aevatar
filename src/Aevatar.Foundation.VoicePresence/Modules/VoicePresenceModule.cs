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
/// with <see cref="IRealtimeVoiceProvider"/>. Audio bytes may use a local volatile
/// lease, while control/session/provider facts are dispatched as typed actor signals.
/// </summary>
// Refactor (iter56/cluster-927-voice-actor-signal-pcm): old=IAudioFastPath bypass, new=typed actor self-signal PCM
// Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
//   Old pattern: VoicePresenceModule 在 module 内持有 process-local background state(unbounded channels / TaskCompletionSource waiters / 静态字段持 lifecycle),还保留 disabled remote voice fallback shell;违反 Actor 单线程事实源 + 中间层状态约束。
//   New principle: Reuse existing RoleGAgent state for voice runtime facts(typed protobuf sub-state in RoleGAgent state);transport handles 仅作 volatile process-local lease(non-fact source);provider callbacks 走 typed self-signals(self-message 到 actor inbox);**删除** disabled remote voice fallback shell。无新 actor type / 新 envelope kind。
public sealed class VoicePresenceModule : ILifecycleAwareEventModule, IRouteBypassModule
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

    // Refactor (iter44/issue-866-voice-presence-process-runtime-state):
    //   Old pattern: VoicePresenceModule kept _runtimeState/_userTransport/relay tasks/dispatcher as module fields that participated in active session, transport attach, and provider response decisions outside actor turn.
    //   New principle: RoleGAgent voice runtime state is the only authority for active session / transport attached / lease expiry / provider binding; transport handles are byte-only volatile leases; provider/transport callbacks enqueue typed self-signals only.
    // Refactor (iter101/cluster-101):
    //   Old pattern: VoicePresenceModule exposed a module-owned state-machine surface and synchronized actor state from that process-local object.
    //   New principle: VoicePresenceModule applies typed voice signals directly to RoleGAgent-owned VoicePresenceRuntimeState; module fields are byte transport lease handles only.
    private VoiceTransportRelayPump? _transportPump;
    private Func<IMessage, CancellationToken, Task>? _volatileSelfSignalDispatcher;

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
        EventPolicy = new VoicePresenceEventPolicy
        {
            StaleAfter = _options.StaleAfter,
            DedupeWindow = _options.DedupeWindow,
        };
    }

    public string Name => _options.Name;

    public int Priority => _options.Priority;

    public VoicePresenceEventPolicy EventPolicy { get; }

    public bool IsInitialized { get; private set; }

    public bool HasVolatileTransportLease => _transportPump != null;

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
            case VoiceModuleSignal.SignalOneofCase.TransportAttachRequested:
                await HandleTransportAttachRequestedAsync(signal.TransportAttachRequested, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.TransportDetachRequested:
                await HandleTransportDetachRequestedAsync(signal.TransportDetachRequested, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.TransportControlFrameReceived:
                await HandleTransportControlFrameReceivedAsync(signal.TransportControlFrameReceived, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.TransportRelayStopped:
                await HandleTransportRelayStoppedAsync(signal.TransportRelayStopped, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.TransportLifetimeCompleted:
                await HandleTransportLifetimeCompletedAsync(signal.TransportLifetimeCompleted, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.ProviderEventReceived:
                await HandleProviderEventReceivedAsync(signal.ProviderEventReceived, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.TransportAudioFrameReceived:
                await HandleTransportAudioFrameReceivedAsync(signal.TransportAudioFrameReceived, ctx, ct);
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
    }

    public async ValueTask DisposeAsync()
    {
        IsInitialized = false;
        await DisposeTransportPumpAsync();
        _volatileSelfSignalDispatcher = null;

        await _provider.DisposeAsync();
    }

    // ── Phase 3: Transport attachment + bidirectional relay ──

    /// <summary>
    /// Attaches a user-side voice transport and starts typed self-signal relay.
    /// </summary>
    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    public void AttachTransport(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher)
    {
        AttachTransport(
            userTransport,
            selfEventDispatcher,
            sessionId: string.Empty,
            ownerId: string.Empty,
            leaseExpiresAt: null);
    }

    public Task<VoiceTransportLifetimeCompleted?> AttachTransportAsync(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        string? sessionId,
        string? ownerId,
        Timestamp? leaseExpiresAt,
        CancellationToken ct = default) =>
        AttachTransportAndObserveDispatchAsync(userTransport, selfEventDispatcher, sessionId, ownerId, leaseExpiresAt, ct);

    public void AttachTransport(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        string? sessionId,
        string? ownerId,
        Timestamp? leaseExpiresAt)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("Leased voice transport attach must observe attach signal dispatch.");

        var pump = AttachTransportCore(userTransport, selfEventDispatcher, sessionId, ownerId, leaseExpiresAt);
        StartTransportRelay(pump);
    }

    private async Task<VoiceTransportLifetimeCompleted?> AttachTransportAndObserveDispatchAsync(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        string? sessionId,
        string? ownerId,
        Timestamp? leaseExpiresAt,
        CancellationToken ct)
    {
        var pump = AttachTransportCore(userTransport, selfEventDispatcher, sessionId, ownerId, leaseExpiresAt);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            StartTransportRelay(pump);
            return null;
        }

        try
        {
            await pump.DispatchAsync(BuildAttachRequested(pump.Key), ct);
        }
        catch
        {
            await DisposeTransportPumpAsync();
            throw;
        }

        StartTransportRelay(pump);
        return BuildTransportLifetimeCompleted(pump.Key);
    }

    private VoiceTransportRelayPump AttachTransportCore(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher,
        string? sessionId,
        string? ownerId,
        Timestamp? leaseExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(userTransport);
        ArgumentNullException.ThrowIfNull(selfEventDispatcher);

        if (_transportPump != null)
            throw new InvalidOperationException("A voice transport is already attached.");

        var key = new VoiceTransportRelayKey(
            string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim(),
            ownerId?.Trim() ?? string.Empty,
            Guid.NewGuid().ToString("N"),
            leaseExpiresAt);
        var pump = new VoiceTransportRelayPump(
            key,
            userTransport,
            selfEventDispatcher);
        _transportPump = pump;

        _provider.OnEvent = (evt, token) => OnProviderEventAsync(evt, pump.Key, token);
        return pump;
    }

    private void StartTransportRelay(VoiceTransportRelayPump pump)
    {
        if (pump.RelayTask != null)
            return;

        pump.RelayTask = RunUserToProviderRelayAsync(pump, pump.Cancellation.Token);
    }

    private static VoiceTransportAttachRequested BuildAttachRequested(VoiceTransportRelayKey key) =>
        new()
        {
            SessionId = key.SessionId,
            OwnerId = key.OwnerId,
            TransportLeaseId = key.TransportLeaseId,
            LeaseExpiresAt = key.LeaseExpiresAt?.Clone(),
        };

    private static VoiceTransportLifetimeCompleted BuildTransportLifetimeCompleted(VoiceTransportRelayKey key) =>
        new()
        {
            SessionId = key.SessionId,
            OwnerId = key.OwnerId,
            TransportLeaseId = key.TransportLeaseId,
            LeaseExpiresAt = key.LeaseExpiresAt?.Clone(),
            Reason = "host_transport_completed",
        };

    /// <summary>
    /// Detaches the current transport and stops the relay loops.
    /// </summary>
    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    public async Task DetachTransportAsync(IVoiceTransport? expectedTransport = null)
    {
        var pump = _transportPump;
        if (pump == null)
            return;

        if (expectedTransport != null && !ReferenceEquals(expectedTransport, pump.Transport))
            return;

        if (!string.IsNullOrWhiteSpace(pump.Key.SessionId))
        {
            await pump.DispatchAsync(new VoiceTransportDetachRequested
            {
                SessionId = pump.Key.SessionId,
                OwnerId = pump.Key.OwnerId,
                TransportLeaseId = pump.Key.TransportLeaseId,
                LeaseExpiresAt = pump.Key.LeaseExpiresAt?.Clone(),
                Reason = "host_transport_detached",
            }, CancellationToken.None);
        }

        await DisposeTransportPumpAsync();
    }

    private async Task RunUserToProviderRelayAsync(VoiceTransportRelayPump pump, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in pump.Transport.ReceiveFramesAsync(ct))
            {
                if (frame.IsAudio)
                {
                    if (!frame.AudioPcm16.IsEmpty)
                    {
                        await pump.DispatchAsync(new VoiceTransportAudioFrameReceived
                        {
                            SessionId = pump.Key.SessionId,
                            OwnerId = pump.Key.OwnerId,
                            TransportLeaseId = pump.Key.TransportLeaseId,
                            LeaseExpiresAt = pump.Key.LeaseExpiresAt?.Clone(),
                            Pcm16 = ByteString.CopyFrom(frame.AudioPcm16.Span),
                            SampleRateHz = PcmSampleRateHz,
                        }, ct);
                    }
                }
                else if (frame.Control != null)
                {
                    await pump.DispatchAsync(new VoiceTransportControlFrameReceived
                    {
                        SessionId = pump.Key.SessionId,
                        OwnerId = pump.Key.OwnerId,
                        TransportLeaseId = pump.Key.TransportLeaseId,
                        LeaseExpiresAt = pump.Key.LeaseExpiresAt?.Clone(),
                        ControlFrame = frame.Control.Clone(),
                    }, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User-to-provider relay terminated unexpectedly.");
            await pump.DispatchAsync(new VoiceTransportRelayStopped
            {
                SessionId = pump.Key.SessionId,
                OwnerId = pump.Key.OwnerId,
                TransportLeaseId = pump.Key.TransportLeaseId,
                LeaseExpiresAt = pump.Key.LeaseExpiresAt?.Clone(),
                Reason = $"error:{ex.Message}",
            }, CancellationToken.None);
        }
    }

    private Task OnProviderEventAsync(VoiceProviderEvent evt, CancellationToken ct) =>
        OnProviderEventAsync(evt, _transportPump?.Key, ct);

    private async Task OnProviderEventAsync(VoiceProviderEvent evt, VoiceTransportRelayKey? callbackKey, CancellationToken ct)
    {
        if (callbackKey != null)
        {
            await DispatchSelfEventAsync(new VoiceProviderEventReceived
            {
                SessionId = callbackKey.SessionId,
                OwnerId = callbackKey.OwnerId,
                TransportLeaseId = callbackKey.TransportLeaseId,
                LeaseExpiresAt = callbackKey.LeaseExpiresAt?.Clone(),
                ProviderEvent = evt.Clone(),
            }, ct);
            return;
        }

        await DispatchSelfEventAsync(new VoiceProviderEventReceived
        {
            ProviderEvent = evt.Clone(),
        }, ct);
    }

    private async Task DisposeTransportPumpAsync()
    {
        var pump = _transportPump;
        _transportPump = null;
        _provider.OnEvent = null;
        if (pump == null)
            return;

        await pump.DisposeAsync();
    }

    private sealed class VoiceTransportRelayKey
    {
        public VoiceTransportRelayKey(
            string sessionId,
            string ownerId,
            string transportLeaseId,
            Timestamp? leaseExpiresAt)
        {
            SessionId = sessionId;
            OwnerId = ownerId;
            TransportLeaseId = transportLeaseId;
            LeaseExpiresAt = leaseExpiresAt?.Clone();
        }

        public string SessionId { get; }
        public string OwnerId { get; }
        public string TransportLeaseId { get; }
        public Timestamp? LeaseExpiresAt { get; }

        public bool Matches(VoiceProviderEventReceived request) =>
            string.Equals(SessionId, request.SessionId, StringComparison.Ordinal) &&
            string.Equals(TransportLeaseId, request.TransportLeaseId, StringComparison.Ordinal) &&
            string.Equals(OwnerId, request.OwnerId, StringComparison.Ordinal);
    }

    private sealed class VoiceTransportRelayPump : IAsyncDisposable
    {
        public VoiceTransportRelayPump(
            VoiceTransportRelayKey key,
            IVoiceTransport transport,
            Func<IMessage, CancellationToken, Task> selfEventDispatcher)
        {
            Key = key;
            Transport = transport;
            SelfEventDispatcher = selfEventDispatcher;
        }

        public VoiceTransportRelayKey Key { get; }
        public IVoiceTransport Transport { get; }
        public Func<IMessage, CancellationToken, Task> SelfEventDispatcher { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? RelayTask { get; set; }

        public Task DispatchAsync(IMessage message, CancellationToken ct) =>
            SelfEventDispatcher(message, ct);

        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();

            if (RelayTask != null)
            {
                try { await RelayTask; }
                catch (OperationCanceledException) { }
            }

            await Transport.DisposeAsync();
            Cancellation.Dispose();
        }
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
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!TryNormalizeProviderEvent(state, providerEvent, out var normalizedEvent))
            return;

        var stateChanged = false;
        switch (normalizedEvent.EventCase)
        {
            case VoiceProviderEvent.EventOneofCase.ResponseStarted:
                state.AwaitingInjectedResponseStart = false;
                ApplyResponseStarted(state, normalizedEvent.ResponseStarted.ResponseId);
                stateChanged = true;
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseDone:
                ApplyResponseDone(state, normalizedEvent.ResponseDone.ResponseId);
                RetireProviderResponse(state, normalizedEvent.ResponseDone.ProviderResponseId);
                stateChanged = true;
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                state.AwaitingInjectedResponseStart = false;
                ApplyResponseCancelled(state, normalizedEvent.ResponseCancelled.ResponseId);
                RetireProviderResponse(state, normalizedEvent.ResponseCancelled.ProviderResponseId);
                stateChanged = true;
                await FlushPendingEventInjectionsAsync(state, ct);
                break;
            case VoiceProviderEvent.EventOneofCase.SpeechStarted:
            {
                var wasInProgress = state.Status == VoicePresenceRuntimeStatus.ResponseInProgress;
                if (wasInProgress)
                {
                    var responseId = state.CurrentResponseId;
                    var providerResponseId = state.ActiveProviderResponseId;
                    await _provider.CancelResponseAsync(ct);
                    if (!string.IsNullOrWhiteSpace(providerResponseId))
                    {
                        if (!state.CancelledProviderResponseIds.Contains(providerResponseId))
                            state.CancelledProviderResponseIds.Add(providerResponseId);
                        RetireProviderResponse(state, providerResponseId);
                    }

                    ApplyResponseCancelled(state, responseId);
                }

                ApplySpeechStarted(state);
                stateChanged = true;
                break;
            }
            case VoiceProviderEvent.EventOneofCase.SpeechStopped:
                ApplySpeechStopped(state);
                stateChanged = true;
                break;
            case VoiceProviderEvent.EventOneofCase.FunctionCall:
                await ExecuteToolCallAsync(normalizedEvent.FunctionCall, ctx, ct);
                break;
            case VoiceProviderEvent.EventOneofCase.Disconnected:
                state.AwaitingInjectedResponseStart = false;
                ApplyProviderDisconnected(state);
                state.ProviderResponseBindings.Clear();
                state.CancelledProviderResponseIds.Clear();
                state.ActiveProviderResponseId = string.Empty;
                stateChanged = true;
                if (await CloseRemoteSessionAsync(state, "provider_disconnected", ctx, ct))
                    stateChanged = false;
                break;
            case VoiceProviderEvent.EventOneofCase.AudioReceived:
                break;
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                break;
        }

        await PersistRuntimeStateIfChangedAsync(ctx, state, stateChanged, ct);
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: provider-specific receive loops suppressed and completed response epochs directly.
    //   New principle: this actor-turn normalizer owns cancellation suppression and response-id materialization.
    private bool TryNormalizeProviderEvent(
        VoicePresenceRuntimeState state,
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
                    state,
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.ResponseDone:
                return TryNormalizeResponseEvent(
                    providerEvent.ResponseDone,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { ResponseDone = message },
                    state,
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                return TryNormalizeResponseEvent(
                    providerEvent.ResponseCancelled,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { ResponseCancelled = message },
                    state,
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.FunctionCall:
                return TryNormalizeResponseEvent(
                    providerEvent.FunctionCall,
                    static message => message.ProviderResponseId,
                    static message => message.ResponseId,
                    static (message, responseId) => message.ResponseId = responseId,
                    static message => new VoiceProviderEvent { FunctionCall = message },
                    state,
                    out normalizedEvent);
            case VoiceProviderEvent.EventOneofCase.AudioReceived:
            {
                var audioReceived = providerEvent.AudioReceived;
                if (!string.IsNullOrWhiteSpace(audioReceived.ProviderResponseId) &&
                    state.CancelledProviderResponseIds.Contains(audioReceived.ProviderResponseId))
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
        VoicePresenceRuntimeState state,
        out VoiceProviderEvent normalizedEvent)
        where TMessage : IMessage<TMessage>
    {
        var message = source.Clone();
        if (!TryNormalizeResponseIdentity(state, getProviderResponseId(message), getResponseId(message), out var responseId))
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
    private bool TryNormalizeResponseIdentity(
        VoicePresenceRuntimeState state,
        string providerResponseId,
        int suppliedResponseId,
        out int responseId)
    {
        if (!string.IsNullOrWhiteSpace(providerResponseId))
        {
            if (state.CancelledProviderResponseIds.Contains(providerResponseId))
            {
                responseId = 0;
                return false;
            }

            responseId = GetOrCreateProviderResponse(state, providerResponseId, suppliedResponseId);
            return true;
        }

        if (suppliedResponseId > 0)
        {
            state.NextResponseId = Math.Max(state.NextResponseId, suppliedResponseId + 1);
            responseId = suppliedResponseId;
            return true;
        }

        responseId = AllocateNextResponseId(state);
        return true;
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: OpenAI/MiniCPM adapters owned provider-id to actor-epoch dictionaries and counters.
    //   New principle: provider-id to actor response-id mapping is actor runtime state owned by this module.
    private int GetOrCreateProviderResponse(
        VoicePresenceRuntimeState state,
        string providerResponseId,
        int suppliedResponseId)
    {
        foreach (var binding in state.ProviderResponseBindings)
        {
            if (string.Equals(binding.ProviderResponseId, providerResponseId, StringComparison.Ordinal))
                return binding.ResponseId;
        }

        var responseId = suppliedResponseId > 0 ? suppliedResponseId : AllocateNextResponseId(state);
        if (suppliedResponseId > 0)
            state.NextResponseId = Math.Max(state.NextResponseId, suppliedResponseId + 1);
        state.ProviderResponseBindings.Add(new VoiceProviderResponseBinding
        {
            ProviderResponseId = providerResponseId,
            ResponseId = responseId,
        });
        state.ActiveProviderResponseId = providerResponseId;
        return responseId;
    }

    private int AllocateNextResponseId(VoicePresenceRuntimeState state)
    {
        var responseId = Math.Max(state.NextResponseId, state.CurrentResponseId + 1);
        state.NextResponseId = responseId + 1;
        ApplyResponseStarted(state, responseId);
        return responseId;
    }

    // Refactor (iter15/cluster-026-voice-provider-background-state):
    //   Old pattern: providers retired response epochs from background completion/cancel callbacks.
    //   New principle: the actor turn retires provider response mappings when committed lifecycle events arrive.
    private void RetireProviderResponse(VoicePresenceRuntimeState state, string providerResponseId)
    {
        if (string.IsNullOrWhiteSpace(providerResponseId))
            return;

        for (var i = state.ProviderResponseBindings.Count - 1; i >= 0; i--)
        {
            if (string.Equals(state.ProviderResponseBindings[i].ProviderResponseId, providerResponseId, StringComparison.Ordinal))
                state.ProviderResponseBindings.RemoveAt(i);
        }

        if (string.Equals(state.ActiveProviderResponseId, providerResponseId, StringComparison.Ordinal))
            state.ActiveProviderResponseId = string.Empty;
    }

    private async Task DispatchSelfEventAsync(IMessage message, CancellationToken ct)
    {
        var dispatcher = _transportPump?.SelfEventDispatcher ?? _volatileSelfSignalDispatcher;
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

    private bool MatchesModuleName(string? moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName) &&
        string.Equals(Name, moduleName, StringComparison.OrdinalIgnoreCase);

    private void EnsureVolatileSelfSignalDispatcher(IEventHandlerContext ctx)
    {
        if (_volatileSelfSignalDispatcher != null)
            return;

        var dispatchPort = ctx.Services.GetService<IActorDispatchPort>();
        if (dispatchPort == null)
            return;

        _volatileSelfSignalDispatcher = (message, token) => dispatchPort.DispatchAsync(
            ctx.AgentId,
            Hosting.VoicePresenceSessionDispatch.BuildSelfEnvelope(ctx.AgentId, Name, message),
            token);
    }

    private async Task HandleProviderEventReceivedAsync(
        VoiceProviderEventReceived request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (!IsAcceptedProviderCallbackSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseExpiresAt))
        {
            return;
        }

        if (request.ProviderEvent == null)
            return;

        if (request.ProviderEvent.EventCase == VoiceProviderEvent.EventOneofCase.AudioReceived)
            await SendProviderAudioToCurrentTransportAsync(request, ct);

        await HandleProviderEventAsync(request.ProviderEvent, ctx, ct);
    }

    private async Task HandleTransportAudioFrameReceivedAsync(
        VoiceTransportAudioFrameReceived request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (request.Pcm16.IsEmpty ||
            !IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseExpiresAt))
        {
            return;
        }

        await _provider.SendAudioAsync(request.Pcm16.Memory, ct);
    }

    private async Task SendProviderAudioToCurrentTransportAsync(
        VoiceProviderEventReceived request,
        CancellationToken ct)
    {
        var pump = _transportPump;
        if (pump == null || !pump.Key.Matches(request))
        {
            return;
        }

        try
        {
            await pump.Transport.SendAudioAsync(request.ProviderEvent.AudioReceived.Pcm16.Memory, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send audio to user transport.");
        }
    }

    private async Task HandleTransportAttachRequestedAsync(
        VoiceTransportAttachRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (request == null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.TransportLeaseId) ||
            !string.Equals(state.ActiveSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        if (!MatchesLeaseOwner(state, request.OwnerId) ||
            !MatchesLeaseExpiry(state, request.LeaseExpiresAt) ||
            IsLeaseExpired(state.LeaseExpiresAt))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(state.RemoteSessionId) &&
            !string.Equals(state.RemoteSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        state.TransportAttached = true;
        state.ActiveLeaseOwnerId = request.OwnerId;
        state.ActiveTransportLeaseId = request.TransportLeaseId;
        state.Initialized = IsInitialized;
        state.PcmSampleRateHz = PcmSampleRateHz;
        if (state.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified)
            state.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;

        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task HandleTransportDetachRequestedAsync(
        VoiceTransportDetachRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseExpiresAt))
            return;

        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task HandleTransportControlFrameReceivedAsync(
        VoiceTransportControlFrameReceived request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseExpiresAt) ||
            request.ControlFrame == null)
        {
            return;
        }

        await HandleControlFrameAsync(request.ControlFrame, ctx, state, ct);
    }

    private async Task HandleTransportRelayStoppedAsync(
        VoiceTransportRelayStopped request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseExpiresAt))
            return;

        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    // Refactor (iter103/cluster-voice-whip): Old pattern: host fire-and-forget background callback calls DetachTransportAsync + lease release directly. New principle: callback publishes typed VoiceTransportLifetimeCompleted; actor reconciles and detaches.
    private async Task HandleTransportLifetimeCompletedAsync(
        VoiceTransportLifetimeCompleted request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseExpiresAt))
            return;

        await DisposeTransportPumpAsync();
        ClearTransportLeaseState(state);
        state.ActiveSessionId = string.Empty;
        state.LeaseExpiresAt = null;
        state.ActiveLeaseOwnerId = string.Empty;
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private bool IsAcceptedTransportSignal(
        VoicePresenceRuntimeState state,
        string sessionId,
        string transportLeaseId,
        string? ownerId = null,
        Timestamp? leaseExpiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(transportLeaseId) ||
            !state.TransportAttached ||
            IsLeaseExpired(state.LeaseExpiresAt))
        {
            return false;
        }

        return string.Equals(state.ActiveSessionId, sessionId, StringComparison.Ordinal) &&
               string.Equals(state.ActiveTransportLeaseId, transportLeaseId, StringComparison.Ordinal) &&
               MatchesLeaseOwner(state, ownerId) &&
               MatchesLeaseExpiry(state, leaseExpiresAt);
    }

    private bool IsAcceptedProviderCallbackSignal(
        VoicePresenceRuntimeState state,
        string sessionId,
        string transportLeaseId,
        string? ownerId,
        Timestamp? leaseExpiresAt)
    {
        if (!string.IsNullOrWhiteSpace(transportLeaseId))
        {
            return IsAcceptedTransportSignal(state, sessionId, transportLeaseId, ownerId, leaseExpiresAt);
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(state.RemoteSessionId))
        {
            return false;
        }

        return string.Equals(state.RemoteSessionId, sessionId, StringComparison.Ordinal) &&
               string.Equals(state.ActiveSessionId, sessionId, StringComparison.Ordinal);
    }

    private bool MatchesLeaseOwner(VoicePresenceRuntimeState state, string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(state.ActiveLeaseOwnerId))
            return string.IsNullOrWhiteSpace(ownerId);

        return !string.IsNullOrWhiteSpace(ownerId) &&
               string.Equals(state.ActiveLeaseOwnerId, ownerId, StringComparison.Ordinal);
    }

    private bool MatchesLeaseExpiry(VoicePresenceRuntimeState state, Timestamp? leaseExpiresAt)
    {
        if (state.LeaseExpiresAt == null)
            return leaseExpiresAt == null;

        return leaseExpiresAt != null &&
               state.LeaseExpiresAt.ToDateTimeOffset() == leaseExpiresAt.ToDateTimeOffset();
    }

    private bool IsLeaseExpired(Timestamp? leaseExpiresAt) =>
        leaseExpiresAt != null &&
        leaseExpiresAt.ToDateTimeOffset() <= _options.TimeProvider.GetUtcNow();

    private static void ClearTransportLeaseState(VoicePresenceRuntimeState state)
    {
        state.TransportAttached = false;
        state.ActiveTransportLeaseId = string.Empty;
    }

    private async Task HandleRemoteSessionOpenRequestedAsync(
        VoiceRemoteSessionOpenRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
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

        if (state.TransportAttached ||
            (!string.IsNullOrWhiteSpace(state.RemoteSessionId) &&
             !string.Equals(state.RemoteSessionId, request.SessionId, StringComparison.Ordinal)))
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

        state.RemoteSessionId = request.SessionId;
        state.ActiveSessionId = request.SessionId;
        state.ActiveLeaseOwnerId = string.Empty;
        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
        var remoteSessionId = request.SessionId;
        _provider.OnEvent = (evt, token) => OnProviderEventAsync(evt, remoteSessionId, token);
    }

    private async Task OnProviderEventAsync(VoiceProviderEvent evt, string remoteSessionId, CancellationToken ct)
    {
        await DispatchSelfEventAsync(new VoiceProviderEventReceived
        {
            SessionId = remoteSessionId,
            ProviderEvent = evt.Clone(),
        }, ct);
    }

    private async Task HandleRemoteSessionCloseRequestedAsync(
        VoiceRemoteSessionCloseRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        var currentSessionId = state.RemoteSessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
            return;

        if (!string.IsNullOrWhiteSpace(request.SessionId) &&
            !string.Equals(currentSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        await CloseRemoteSessionAsync(
            state,
            string.IsNullOrWhiteSpace(request.Reason) ? "remote_session_closed" : request.Reason,
            ctx,
            ct);
    }

    private async Task HandleRemoteControlInputReceivedAsync(
        VoiceRemoteControlInputReceived request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        EnsureVolatileSelfSignalDispatcher(ctx);
        if (string.IsNullOrWhiteSpace(state.RemoteSessionId) ||
            !string.Equals(state.RemoteSessionId, request.SessionId, StringComparison.Ordinal) ||
            request.ControlFrame == null)
        {
            return;
        }

        await HandleControlFrameAsync(request.ControlFrame, ctx, state, ct);
    }

    private async Task<bool> CloseRemoteSessionAsync(
        VoicePresenceRuntimeState state,
        string reason,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var currentSessionId = state.RemoteSessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
            return false;

        state.RemoteSessionId = string.Empty;
        state.ActiveSessionId = string.Empty;
        state.ActiveLeaseOwnerId = string.Empty;
        ClearTransportLeaseState(state);
        state.ProviderResponseBindings.Clear();
        state.CancelledProviderResponseIds.Clear();
        state.ActiveProviderResponseId = string.Empty;
        _provider.OnEvent = _transportPump == null ? null : OnProviderEventAsync;
        await PersistRuntimeStateAsync(ctx, state, ct);
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
        var state = HydrateRuntimeStateFromActor(ctx);
        if (request == null || string.IsNullOrWhiteSpace(request.SessionId))
            return;

        var currentSessionId = state.ActiveSessionId;
        if (!string.IsNullOrWhiteSpace(currentSessionId) &&
            !string.Equals(currentSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        state.Initialized = IsInitialized;
        state.PcmSampleRateHz = PcmSampleRateHz;
        state.ActiveSessionId = request.SessionId;
        state.ActiveLeaseOwnerId = request.OwnerId;
        state.LeaseExpiresAt = request.ExpiresAt?.Clone();
        state.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    // Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
    //   Old pattern: InProcessActorVoicePresenceSessionResolver 通过 runtime instance shape 判定 voice session capability(违反"运行时形态不是业务事实")。
    //   New principle: voice capability/session facts 由 actor-owned VoicePresenceCapabilityReadModel 暴露;host resolver 只 obtain lease/session handle;走 existing typed lease command/event flow,no runtime-shape inspection。
    internal async Task HandleSessionLeaseReleasedAsync(
        VoicePresenceSessionLeaseReleased request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (request == null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            !string.Equals(state.ActiveSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        state.ActiveSessionId = string.Empty;
        state.LeaseExpiresAt = null;
        state.ActiveLeaseOwnerId = string.Empty;
        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
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
        var state = HydrateRuntimeStateFromActor(ctx);
        await HandleControlFrameAsync(frame, ctx, state, ct);
    }

    private async Task HandleControlFrameAsync(
        VoiceControlFrame frame,
        IEventHandlerContext ctx,
        VoicePresenceRuntimeState state,
        CancellationToken ct)
    {
        switch (frame.FrameCase)
        {
            case VoiceControlFrame.FrameOneofCase.DrainAcknowledged:
                ApplyDrainAcknowledged(
                    state,
                    frame.DrainAcknowledged.ResponseId,
                    frame.DrainAcknowledged.PlayoutSequence);
                await FlushPendingEventInjectionsAsync(state, ct);
                await PersistRuntimeStateAsync(ctx, state, ct);
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
        var state = HydrateRuntimeStateFromActor(ctx);

        if (!ShouldInjectExternalEvent(envelope, ctx.AgentId))
            return;

        var now = _options.TimeProvider.GetUtcNow();
        // Refactor (iter104/cluster-3): Old pattern: VoicePresenceEventPolicy kept module-local in-memory recent-event dedupe set. New principle: dedupe fence in VoicePresenceRuntimeState (actor-owned); policy is pure evaluator over passed-in actor state.
        var verdict = EventPolicy.Evaluate(envelope, now, state.EventDedupeFence);
        var fenceChanged = ReplaceEventDedupeFence(state, EventPolicy.BuildFence(state.EventDedupeFence, verdict, now));
        if (verdict.Decision != VoicePresenceEventPolicyDecision.Admit)
        {
            await PersistRuntimeStateIfChangedAsync(ctx, state, fenceChanged, ct);
            return;
        }

        var injection = BuildPendingInjection(envelope, now);
        if (!IsReadyToInject(state))
        {
            EnqueuePendingInjection(state, injection);
            await PersistRuntimeStateAsync(ctx, state, ct);
            return;
        }

        await TryInjectEventAsync(state, injection, ct);
        await PersistRuntimeStateAsync(ctx, state, ct);
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

    private void EnqueuePendingInjection(
        VoicePresenceRuntimeState state,
        VoicePendingEventInjection injection)
    {
        if (_options.PendingInjectionCapacity <= 0)
            return;

        while (state.PendingInjections.Count >= _options.PendingInjectionCapacity)
            state.PendingInjections.RemoveAt(0);

        state.PendingInjections.Add(injection);
    }

    private static bool ReplaceEventDedupeFence(
        VoicePresenceRuntimeState state,
        IReadOnlyList<VoicePresenceEventDedupeFenceEntry> fence)
    {
        if (state.EventDedupeFence.Count == fence.Count &&
            state.EventDedupeFence.Zip(fence).All(static pair => DedupeFenceEntryEquals(pair.First, pair.Second)))
        {
            return false;
        }

        state.EventDedupeFence.Clear();
        state.EventDedupeFence.AddRange(fence.Select(static entry => entry.Clone()));
        return true;
    }

    private static bool DedupeFenceEntryEquals(
        VoicePresenceEventDedupeFenceEntry left,
        VoicePresenceEventDedupeFenceEntry right) =>
        string.Equals(left.Key, right.Key, StringComparison.Ordinal) &&
        Equals(left.RecordedAt, right.RecordedAt);

    private async Task FlushPendingEventInjectionsAsync(VoicePresenceRuntimeState state, CancellationToken ct)
    {
        while (state.PendingInjections.Count > 0 && IsReadyToInject(state))
        {
            var next = state.PendingInjections[0];
            state.PendingInjections.RemoveAt(0);
            if (IsExpired(next))
                continue;

            if (await TryInjectEventAsync(state, next, ct))
                return;

            return;
        }
    }

    private bool IsExpired(VoicePendingEventInjection injection)
    {
        var observedAt = injection.ObservedAt?.ToDateTimeOffset() ?? _options.TimeProvider.GetUtcNow();
        return _options.TimeProvider.GetUtcNow() - observedAt > _options.StaleAfter;
    }

    private bool IsReadyToInject(VoicePresenceRuntimeState state) =>
        IsInitialized &&
        IsSafeToInject(state) &&
        !state.AwaitingInjectedResponseStart;

    private async Task<bool> TryInjectEventAsync(
        VoicePresenceRuntimeState state,
        VoicePendingEventInjection injection,
        CancellationToken ct)
    {
        var providerInjection = BuildProviderInjection(injection);
        try
        {
            await _provider.InjectEventAsync(providerInjection, ct);
            state.AwaitingInjectedResponseStart = true;
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
    private VoicePresenceRuntimeState HydrateRuntimeStateFromActor(IEventHandlerContext ctx)
    {
        if (ctx.Agent is not IVoicePresenceRuntimeStateOwner stateOwner ||
            !stateOwner.TryGetVoicePresenceRuntimeState(Name, out var stored))
        {
            return CreateInitialRuntimeState();
        }

        return NormalizeRuntimeState(stored);
    }

    private async Task PersistRuntimeStateIfChangedAsync(
        IEventHandlerContext ctx,
        VoicePresenceRuntimeState state,
        bool stateChanged,
        CancellationToken ct)
    {
        if (!stateChanged)
            return;

        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    // Refactor (iter35/cluster-036-voice-presence-rolegagent-state):
    //   Old pattern: voice response bindings, remote session id, and pending injections lived only in module memory.
    //   New principle: synchronize runtime facts into the actor-owned protobuf sub-state through a narrow state-owner contract.
    private async Task PersistRuntimeStateAsync(
        IEventHandlerContext ctx,
        VoicePresenceRuntimeState state,
        CancellationToken ct)
    {
        var normalized = NormalizeRuntimeState(state);
        ApplyTransportFacts(normalized);

        if (ctx.Agent is not IVoicePresenceRuntimeStateOwner stateOwner)
            return;

        await stateOwner.PersistVoicePresenceRuntimeStateAsync(Name, normalized, ct);
    }

    private void ApplyTransportFacts(VoicePresenceRuntimeState state)
    {
        state.Initialized = IsInitialized;
        state.PcmSampleRateHz = PcmSampleRateHz;
        if (state.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified)
            state.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;
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

    private static void ApplySpeechStarted(VoicePresenceRuntimeState state) =>
        state.Status = VoicePresenceRuntimeStatus.UserSpeaking;

    private static void ApplySpeechStopped(VoicePresenceRuntimeState state)
    {
    }

    private static void ApplyResponseStarted(VoicePresenceRuntimeState state, int responseId)
    {
        if (responseId < state.CurrentResponseId)
            return;

        state.CurrentResponseId = responseId;
        state.NextResponseId = Math.Max(state.NextResponseId, responseId + 1);
        state.Status = VoicePresenceRuntimeStatus.ResponseInProgress;
    }

    private static void ApplyResponseDone(VoicePresenceRuntimeState state, int responseId)
    {
        if (responseId != state.CurrentResponseId)
            return;

        if (state.Status is VoicePresenceRuntimeStatus.ResponseInProgress or VoicePresenceRuntimeStatus.UserSpeaking)
            state.Status = VoicePresenceRuntimeStatus.AudioDraining;
    }

    private static void ApplyResponseCancelled(VoicePresenceRuntimeState state, int responseId)
    {
        if (responseId != state.CurrentResponseId)
            return;

        state.LastDrainAckResponseId = responseId;
        state.Status = VoicePresenceRuntimeStatus.Idle;
    }

    private static void ApplyDrainAcknowledged(
        VoicePresenceRuntimeState state,
        int responseId,
        long playoutSequence)
    {
        if (responseId != state.CurrentResponseId)
            return;

        state.LastDrainAckResponseId = responseId;
        state.LastDrainAckPlayoutSequence = playoutSequence;

        if (state.Status == VoicePresenceRuntimeStatus.AudioDraining)
            state.Status = VoicePresenceRuntimeStatus.Idle;
    }

    private static void ApplyProviderDisconnected(VoicePresenceRuntimeState state)
    {
        state.LastDrainAckResponseId = state.CurrentResponseId;
        state.Status = VoicePresenceRuntimeStatus.Idle;
    }

    private static bool IsSafeToInject(VoicePresenceRuntimeState state) =>
        state.Status == VoicePresenceRuntimeStatus.Idle &&
        (state.CurrentResponseId == 0 || state.LastDrainAckResponseId == state.CurrentResponseId);
}
