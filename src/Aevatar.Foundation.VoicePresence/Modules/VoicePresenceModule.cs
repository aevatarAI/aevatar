using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
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
/// with <see cref="IRealtimeVoiceProvider"/>. Transport attachment is actor-owned;
/// this module only applies typed actor signals during event turns.
/// </summary>
public sealed class VoicePresenceModule : ILifecycleAwareEventModule, IRouteBypassModule
{
    private static readonly JsonFormatter PayloadJsonFormatter = new(JsonFormatter.Settings.Default);
    private const string TrustedDirectExternalEventPublisherActorId = "device-events.callback";
    private const int DefaultLastDrainAckResponseId = -1;
    private const long DefaultLastDrainAckPlayoutSequence = -1;

    private enum VoiceLeaseUpstreamDeliveryStatus
    {
        NoLease,
        Delivered,
        DeliveryGap,
    }

    private readonly IRealtimeVoiceProvider _provider;
    private readonly VoiceProviderConfig _providerConfig;
    private readonly VoiceSessionConfig? _sessionConfig;
    private readonly VoicePresenceModuleOptions _options;
    private readonly IReadOnlySet<string> _directExternalEventTypeUrls;
    private readonly IVoiceToolInvoker? _toolInvoker;
    private readonly IVoiceToolCatalog? _toolCatalog;
    private readonly ILogger _logger;

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
        _directExternalEventTypeUrls = new HashSet<string>(
            _options.DirectExternalEventTypeUrls,
            StringComparer.Ordinal);
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

    public int PcmSampleRateHz => ResolveConfiguredSampleRateHz(_sessionConfig);

    // ── IEventModule ──────────────────────────────────────────

    public bool CanHandle(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return false;

        return envelope.Payload.Is(VoiceModuleSignal.Descriptor) ||
               envelope.Payload.Is(VoiceProviderEvent.Descriptor) ||
               envelope.Payload.Is(VoiceControlFrame.Descriptor) ||
               envelope.Route?.IsPublication() == true ||
               IsConfiguredDirectExternalEvent(envelope, null);
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(VoiceModuleSignal.Descriptor))
        {
            await HandleModuleSignalAsync(
                envelope.Payload.Unpack<VoiceModuleSignal>(),
                ResolveIssuedAtUnixMs(envelope),
                ctx,
                ct);
            return;
        }

        if (envelope.Payload.Is(VoiceProviderEvent.Descriptor))
        {
            await HandleProviderEventAsync(
                envelope.Payload.Unpack<VoiceProviderEvent>(),
                ResolveIssuedAtUnixMs(envelope),
                ctx,
                ct);
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
        long issuedAtUnixMs,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (!MatchesModuleName(signal.ModuleName))
            return;

        switch (signal.SignalCase)
        {
            case VoiceModuleSignal.SignalOneofCase.ProviderEvent:
                await HandleProviderEventAsync(signal.ProviderEvent, issuedAtUnixMs, ctx, ct);
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
            case VoiceModuleSignal.SignalOneofCase.TransportLeaseRenewRequested:
                await HandleTransportLeaseRenewRequestedAsync(signal.TransportLeaseRenewRequested, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.DrainTimeoutExpired:
                await HandleDrainTimeoutExpiredAsync(signal.DrainTimeoutExpired, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.ClientToolCallTimeoutExpired:
                await HandleClientToolCallTimeoutExpiredAsync(signal.ClientToolCallTimeoutExpired, ctx, ct);
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
                await HandleProviderEventReceivedAsync(
                    signal.ProviderEventReceived,
                    issuedAtUnixMs,
                    ctx,
                    ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.InputImageReceived:
                await HandleInputImageReceivedAsync(signal.InputImageReceived, ctx, ct);
                break;
            case VoiceModuleSignal.SignalOneofCase.None:
            default:
                break;
        }
    }

    // ── ILifecycleAwareEventModule ────────────────────────────

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (IsInitialized)
            return;

        IsInitialized = true;
    }

    public async ValueTask DisposeAsync()
    {
        IsInitialized = false;

        await _provider.DisposeAsync();
    }

    // ── Phase 3: Transport attachment + bidirectional relay ──

    // ── State machine dispatch (used by both event pipeline and relay) ──

    internal async Task HandleProviderEventAsync(
        VoiceProviderEvent providerEvent,
        long issuedAtUnixMs,
        IEventHandlerContext ctx,
        CancellationToken ct,
        string? transportLeaseId = null)
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
            {
                var previousStatus = state.Status;
                ApplyResponseDone(state, normalizedEvent.ResponseDone.ResponseId);
                if (previousStatus != VoicePresenceRuntimeStatus.AudioDraining &&
                    state.Status == VoicePresenceRuntimeStatus.AudioDraining)
                {
                    await ScheduleDrainTimeoutAsync(state, normalizedEvent.ResponseDone.ResponseId, ctx, ct);
                }

                RetireProviderResponse(state, normalizedEvent.ResponseDone.ProviderResponseId);
                stateChanged = true;
                break;
            }
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                state.AwaitingInjectedResponseStart = false;
                ApplyResponseCancelled(state, normalizedEvent.ResponseCancelled.ResponseId);
                VoiceClientToolCallStateMachine.RemovePendingCallsForProviderResponse(
                    state,
                    normalizedEvent.ResponseCancelled.ProviderResponseId);
                RetireProviderResponse(state, normalizedEvent.ResponseCancelled.ProviderResponseId);
                stateChanged = true;
                await FlushPendingEventInjectionsAsync(state, ctx, ct);
                break;
            case VoiceProviderEvent.EventOneofCase.SpeechStarted:
            {
                var wasInProgress = state.Status == VoicePresenceRuntimeStatus.ResponseInProgress;
                if (wasInProgress)
                {
                    var responseId = state.CurrentResponseId;
                    var providerResponseId = state.ActiveProviderResponseId;
                    var deliveryStatus = await TrySendLeaseUpstreamAsync(
                        state,
                        ctx,
                        transportLeaseId,
                        "response.cancel",
                        static (mediaPort, upstreamTransportLeaseId, upstreamCt) =>
                            mediaPort.TryCancelResponseAsync(upstreamTransportLeaseId, upstreamCt),
                        ct);
                    if (deliveryStatus == VoiceLeaseUpstreamDeliveryStatus.NoLease)
                    {
                        await using var providerSession = await ConnectProviderSessionAsync(state, ct);
                        await providerSession.CancelResponseAsync(ct);
                    }

                    if (!string.IsNullOrWhiteSpace(providerResponseId))
                    {
                        if (!state.CancelledProviderResponseIds.Contains(providerResponseId))
                            state.CancelledProviderResponseIds.Add(providerResponseId);
                        VoiceClientToolCallStateMachine.RemovePendingCallsForProviderResponse(state, providerResponseId);
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
                stateChanged = await HandleFunctionCallRequestedAsync(
                    normalizedEvent.FunctionCall,
                    state,
                    issuedAtUnixMs,
                    ctx,
                    ct,
                    transportLeaseId);
                break;
            case VoiceProviderEvent.EventOneofCase.Disconnected:
                state.AwaitingInjectedResponseStart = false;
                ApplyProviderDisconnected(state);
                state.ProviderResponseBindings.Clear();
                state.CancelledProviderResponseIds.Clear();
                state.ActiveProviderResponseId = string.Empty;
                state.PendingClientToolCalls.Clear();
                stateChanged = true;
                if (await CloseRemoteSessionAsync(state, "provider_disconnected", ctx, ct))
                    stateChanged = false;
                break;
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                break;
        }

        await PublishRealtimeFrameAsync(normalizedEvent, state, ctx, ct);
        await PersistRuntimeStateIfChangedAsync(ctx, state, stateChanged, ct);
    }

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
            case VoiceProviderEvent.EventOneofCase.Disconnected:
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.SpeechStarted:
            case VoiceProviderEvent.EventOneofCase.SpeechStopped:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                return true;
        }
    }

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

    private async Task<bool> HandleFunctionCallRequestedAsync(
        VoiceFunctionCallRequested request,
        VoicePresenceRuntimeState state,
        long issuedAtUnixMs,
        IEventHandlerContext ctx,
        CancellationToken ct,
        string? transportLeaseId)
    {
        if (await ResolveToolOwnerAsync(state, request.ToolName, ct) != VoiceToolOwner.Client)
        {
            await ExecuteToolCallAsync(request, issuedAtUnixMs, ctx, ct, transportLeaseId);
            return false;
        }

        var pendingCall = VoiceClientToolCallStateMachine.RecordPendingCall(
            state,
            request,
            _options.TimeProvider.GetUtcNow().Add(_options.ToolExecutionTimeout),
            transportLeaseId);

        await ScheduleClientToolCallTimeoutAsync(pendingCall, ctx, ct);
        return true;
    }

    private bool MatchesModuleName(string? moduleName) =>
        !string.IsNullOrWhiteSpace(moduleName) &&
        string.Equals(Name, moduleName, StringComparison.OrdinalIgnoreCase);

    private async Task HandleProviderEventReceivedAsync(
        VoiceProviderEventReceived request,
        long issuedAtUnixMs,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!IsAcceptedProviderCallbackSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch))
        {
            return;
        }

        if (request.ProviderEvent == null)
            return;

        // The envelope carries the transport lease (== the VoiceVolatileMediaStreamPort relay key).
        // Thread it through so a tool result reaches the LIVE relay session even when the actor's
        // persisted state.ActiveTransportLeaseId is empty — which it is on the policy-aware /ws/voice
        // relay path, where the FunctionCall is admitted via the RemoteSessionId fallback and the
        // transport lease is never persisted into runtime state.
        await HandleProviderEventAsync(
            request.ProviderEvent,
            issuedAtUnixMs,
            ctx,
            ct,
            request.TransportLeaseId);
    }

    private async Task HandleInputImageReceivedAsync(
        VoiceInputImageReceived request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!IsAcceptedInputImageSignal(state, request) ||
            request.InputImage == null)
        {
            return;
        }

        var deliveryStatus = await TrySendLeaseUpstreamAsync(
            state,
            ctx,
            request.TransportLeaseId,
            "input_image",
            (mediaPort, transportLeaseId, upstreamCt) =>
                mediaPort.TrySendInputImageAsync(transportLeaseId, request.InputImage, upstreamCt),
            ct);
        if (deliveryStatus != VoiceLeaseUpstreamDeliveryStatus.NoLease)
            return;

        await using var providerSession = await ConnectProviderSessionAsync(state, ct);
        await providerSession.SendInputImageAsync(request.InputImage, ct);
    }

    private async Task HandleTransportAttachRequestedAsync(
        VoiceTransportAttachRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (request == null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.TransportLeaseId) ||
            !string.Equals(state.ActiveSessionId, request.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        if (!MatchesLeaseOwner(state, request.OwnerId) ||
            !MatchesLeaseExpiry(state, request.LeaseExpiresAt) ||
            !MatchesLeaseEpoch(state, request.LeaseEpoch) ||
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
        RefreshCapabilityFacts(state, ctx);

        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task ScheduleDrainTimeoutAsync(
        VoicePresenceRuntimeState state,
        int responseId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (_options.DrainTimeout <= TimeSpan.Zero)
            return;

        if (responseId <= 0 || state.LeaseEpoch <= 0)
            return;

        await ctx.ScheduleSelfDurableTimeoutAsync(
            BuildDrainTimeoutCallbackId(state.LeaseEpoch, responseId),
            _options.DrainTimeout,
            new VoiceModuleSignal
            {
                ModuleName = Name,
                DrainTimeoutExpired = new VoiceDrainTimeoutExpired
                {
                    SessionId = state.ActiveSessionId ?? string.Empty,
                    OwnerId = state.ActiveLeaseOwnerId ?? string.Empty,
                    TransportLeaseId = state.ActiveTransportLeaseId ?? string.Empty,
                    LeaseEpoch = state.LeaseEpoch,
                    ResponseId = responseId,
                },
            },
            ct: ct);
    }

    private async Task ScheduleClientToolCallTimeoutAsync(
        VoicePendingClientToolCall pendingCall,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (_options.ToolExecutionTimeout <= TimeSpan.Zero)
            return;

        if (string.IsNullOrWhiteSpace(pendingCall.CallId) ||
            pendingCall.LeaseEpoch <= 0)
        {
            return;
        }

        await ctx.ScheduleSelfDurableTimeoutAsync(
            BuildClientToolCallTimeoutCallbackId(pendingCall),
            _options.ToolExecutionTimeout,
            new VoiceModuleSignal
            {
                ModuleName = Name,
                ClientToolCallTimeoutExpired = new VoiceClientToolCallTimeoutExpired
                {
                    SessionId = pendingCall.SessionId,
                    OwnerId = pendingCall.OwnerId,
                    TransportLeaseId = pendingCall.TransportLeaseId,
                    LeaseEpoch = pendingCall.LeaseEpoch,
                    CallId = pendingCall.CallId,
                    ToolName = pendingCall.ToolName,
                },
            },
            ct: ct);
    }

    private async Task HandleTransportLeaseRenewRequestedAsync(
        VoiceTransportLeaseRenewRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch) ||
            request.RenewExpiresAt == null)
        {
            return;
        }

        if (state.LeaseExpiresAt == null ||
            request.RenewExpiresAt.ToDateTimeOffset() <= state.LeaseExpiresAt.ToDateTimeOffset())
        {
            return;
        }

        state.LeaseExpiresAt = request.RenewExpiresAt.Clone();
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task HandleDrainTimeoutExpiredAsync(
        VoiceDrainTimeoutExpired request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (request == null ||
            state.Status != VoicePresenceRuntimeStatus.AudioDraining ||
            request.ResponseId != state.CurrentResponseId ||
            request.LeaseEpoch <= 0 ||
            state.LeaseEpoch != request.LeaseEpoch ||
            !string.Equals(state.ActiveSessionId, request.SessionId, StringComparison.Ordinal) ||
            !string.Equals(state.ActiveLeaseOwnerId ?? string.Empty, request.OwnerId, StringComparison.Ordinal) ||
            !string.Equals(
                state.ActiveTransportLeaseId ?? string.Empty,
                request.TransportLeaseId,
                StringComparison.Ordinal))
        {
            return;
        }

        state.LastDrainAckResponseId = request.ResponseId;
        state.Status = VoicePresenceRuntimeStatus.Idle;
        await FlushPendingEventInjectionsAsync(state, ctx, ct);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task HandleClientToolCallTimeoutExpiredAsync(
        VoiceClientToolCallTimeoutExpired request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        var pendingCall = VoiceClientToolCallStateMachine.ExpirePendingCall(
            state,
            request,
            _options.TimeProvider.GetUtcNow());
        if (pendingCall == null)
            return;

        var resultJson = VoiceClientToolCallStateMachine.BuildTimeoutJson(
            pendingCall,
            _options.ToolExecutionTimeout);
        await DeliverToolResultAsync(
            state,
            pendingCall.CallId,
            resultJson,
            pendingCall.TransportLeaseId,
            ctx,
            ct);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task HandleTransportDetachRequestedAsync(
        VoiceTransportDetachRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch))
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
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch) ||
            request.ControlFrame == null)
        {
            return;
        }

        await HandleControlFrameAsync(
            request.ControlFrame,
            ctx,
            state,
            new VoiceClientToolCallCompletionFence(
                request.SessionId,
                request.OwnerId ?? string.Empty,
                request.TransportLeaseId,
                request.LeaseEpoch),
            ct);
    }

    private async Task HandleTransportRelayStoppedAsync(
        VoiceTransportRelayStopped request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch))
            return;

        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task HandleTransportLifetimeCompletedAsync(
        VoiceTransportLifetimeCompleted request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        if (!IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch))
            return;

        ClearTransportLeaseState(state);
        state.ActiveSessionId = string.Empty;
        state.LeaseExpiresAt = null;
        state.ActiveLeaseOwnerId = string.Empty;
        state.ActiveToolContext = null;
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private bool IsAcceptedTransportSignal(
        VoicePresenceRuntimeState state,
        string sessionId,
        string transportLeaseId,
        string? ownerId = null,
        long leaseEpoch = 0)
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
               MatchesLeaseEpoch(state, leaseEpoch);
    }

    private bool IsAcceptedProviderCallbackSignal(
        VoicePresenceRuntimeState state,
        string sessionId,
        string transportLeaseId,
        string? ownerId,
        long leaseEpoch)
    {
        if (!string.IsNullOrWhiteSpace(transportLeaseId))
        {
            return IsAcceptedTransportSignal(state, sessionId, transportLeaseId, ownerId, leaseEpoch);
        }

        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(state.RemoteSessionId))
        {
            return false;
        }

        return string.Equals(state.RemoteSessionId, sessionId, StringComparison.Ordinal) &&
               string.Equals(state.ActiveSessionId, sessionId, StringComparison.Ordinal) &&
               MatchesLeaseEpoch(state, leaseEpoch);
    }

    private bool IsAcceptedInputImageSignal(
        VoicePresenceRuntimeState state,
        VoiceInputImageReceived request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            IsLeaseExpired(state.LeaseExpiresAt))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.TransportLeaseId))
        {
            return IsAcceptedTransportSignal(
                state,
                request.SessionId,
                request.TransportLeaseId,
                request.OwnerId,
                request.LeaseEpoch);
        }

        if (string.IsNullOrWhiteSpace(state.RemoteSessionId))
            return false;

        return string.Equals(state.RemoteSessionId, request.SessionId, StringComparison.Ordinal) &&
               string.Equals(state.ActiveSessionId, request.SessionId, StringComparison.Ordinal) &&
               MatchesLeaseEpoch(state, request.LeaseEpoch);
    }

    private static bool MatchesLeaseEpoch(VoicePresenceRuntimeState state, long leaseEpoch) =>
        leaseEpoch > 0 && state.LeaseEpoch == leaseEpoch;

    private static long NextLeaseEpoch(VoicePresenceRuntimeState state) =>
        state.LeaseEpoch <= 0 ? 1 : state.LeaseEpoch + 1;

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

    private string BuildDrainTimeoutCallbackId(long leaseEpoch, int responseId) =>
        $"{Name}:voice-drain-timeout:{leaseEpoch}:{responseId}";

    private string BuildClientToolCallTimeoutCallbackId(VoicePendingClientToolCall pendingCall) =>
        $"{Name}:voice-client-tool-timeout:{pendingCall.LeaseEpoch}:{pendingCall.CallId}";

    private static void ClearTransportLeaseState(VoicePresenceRuntimeState state)
    {
        VoiceClientToolCallStateMachine.RemovePendingCallsForTransport(state, state.ActiveTransportLeaseId);
        state.TransportAttached = false;
        state.ActiveTransportLeaseId = string.Empty;
    }

    private async Task HandleRemoteSessionOpenRequestedAsync(
        VoiceRemoteSessionOpenRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
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
        state.LeaseEpoch = NextLeaseEpoch(state);
        state.ActiveToolContext = null;
        state.PendingClientToolCalls.Clear();
        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
        await using (await ConnectProviderSessionAsync(state, ct))
        {
        }
    }

    private async Task HandleRemoteSessionCloseRequestedAsync(
        VoiceRemoteSessionCloseRequested request,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
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
        if (string.IsNullOrWhiteSpace(state.RemoteSessionId) ||
            !string.Equals(state.RemoteSessionId, request.SessionId, StringComparison.Ordinal) ||
            request.ControlFrame == null)
        {
            return;
        }

        await HandleControlFrameAsync(
            request.ControlFrame,
            ctx,
            state,
            new VoiceClientToolCallCompletionFence(
                state.ActiveSessionId ?? string.Empty,
                state.ActiveLeaseOwnerId ?? string.Empty,
                state.ActiveTransportLeaseId ?? string.Empty,
                state.LeaseEpoch),
            ct);
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
        state.ActiveToolContext = null;
        ClearTransportLeaseState(state);
        state.ProviderResponseBindings.Clear();
        state.CancelledProviderResponseIds.Clear();
        state.ActiveProviderResponseId = string.Empty;
        state.PendingClientToolCalls.Clear();
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

        state.ActiveSessionId = request.SessionId;
        state.ActiveLeaseOwnerId = request.OwnerId;
        state.LeaseExpiresAt = request.ExpiresAt?.Clone();
        state.LeaseEpoch = NextLeaseEpoch(state);
        state.ActiveToolContext = request.ToolContext?.Clone();
        state.PendingClientToolCalls.Clear();
        RefreshCapabilityFacts(state, ctx, request.SessionOverrides);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

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
        state.ActiveToolContext = null;
        state.PendingClientToolCalls.Clear();
        ClearTransportLeaseState(state);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task PublishRemoteOutputAsync(
        VoiceRemoteTransportOutput output,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        await PublishRealtimeFrameAsync(output, state, ctx, ct);
        await ctx.PublishAsync(output, TopologyAudience.Self, ct);
    }

    private Task PublishRealtimeFrameAsync(
        VoiceProviderEvent providerEvent,
        VoicePresenceRuntimeState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var frame = BuildRealtimeFrame(providerEvent, state);
        return frame == null ? Task.CompletedTask : PublishRealtimeFrameAsync(frame, ctx, ct);
    }

    private Task PublishRealtimeFrameAsync(
        VoiceRemoteTransportOutput output,
        VoicePresenceRuntimeState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        if (output.OutputCase != VoiceRemoteTransportOutput.OutputOneofCase.SessionClosed)
            return Task.CompletedTask;

        var sessionId = string.IsNullOrWhiteSpace(output.SessionId) ? state.ActiveSessionId : output.SessionId;
        var frame = CreateRealtimeFrame(sessionId, new VoiceRealtimeFrame
        {
            SessionClosed = output.SessionClosed?.Clone(),
        });
        return frame == null ? Task.CompletedTask : PublishRealtimeFrameAsync(frame, ctx, ct);
    }

    private VoiceRealtimeFrame? BuildRealtimeFrame(
        VoiceProviderEvent providerEvent,
        VoicePresenceRuntimeState state)
    {
        return providerEvent.EventCase switch
        {
            VoiceProviderEvent.EventOneofCase.ResponseStarted => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                ResponseStarted = providerEvent.ResponseStarted?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.ResponseDone => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                ResponseDone = providerEvent.ResponseDone?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.ResponseCancelled => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                ResponseCancelled = providerEvent.ResponseCancelled?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.FunctionCall => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                FunctionCall = providerEvent.FunctionCall?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.SpeechStarted => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                SpeechStarted = providerEvent.SpeechStarted?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.SpeechStopped => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                SpeechStopped = providerEvent.SpeechStopped?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.Error => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                Error = providerEvent.Error?.Clone(),
            }),
            VoiceProviderEvent.EventOneofCase.Disconnected => CreateRealtimeFrame(state.ActiveSessionId, new VoiceRealtimeFrame
            {
                Disconnected = providerEvent.Disconnected?.Clone(),
            }),
            _ => null,
        };
    }

    private VoiceRealtimeFrame? CreateRealtimeFrame(string? sessionId, VoiceRealtimeFrame frame)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            frame.FrameCase == VoiceRealtimeFrame.FrameOneofCase.None)
        {
            return null;
        }

        frame.ModuleName = Name;
        frame.SessionId = sessionId;
        return frame;
    }

    private Task PublishRealtimeFrameAsync(
        VoiceRealtimeFrame frame,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var hub = ctx.Services.GetService<IProjectionSessionEventHub<VoiceRealtimeFrame>>();
        return hub == null
            ? Task.CompletedTask
            : hub.PublishAsync(ctx.AgentId, frame.SessionId, frame, ct);
    }

    private async Task<RealtimeVoiceProviderSession> ConnectProviderSessionAsync(
        VoicePresenceRuntimeState state,
        CancellationToken ct)
    {
        var key = BuildProviderSessionKey(state);
        var session = await _provider.ConnectAsync(
            key,
            _providerConfig,
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => Task.CompletedTask,
            ct);
        var effectiveSessionConfig = await BuildEffectiveSessionConfigAsync(state, ct);
        if (effectiveSessionConfig != null)
            await session.UpdateSessionAsync(effectiveSessionConfig, ct);

        return session;
    }

    private VoiceProviderSessionKey BuildProviderSessionKey(VoicePresenceRuntimeState state) =>
        new(
            state.ActiveSessionId ?? string.Empty,
            state.ActiveLeaseOwnerId ?? string.Empty,
            state.ActiveTransportLeaseId ?? string.Empty,
            state.LeaseEpoch,
            state.LeaseExpiresAt?.Clone(),
            string.Empty,
            Name,
            state.ActiveToolContext?.Clone());

    private async Task ExecuteToolCallAsync(
        VoiceFunctionCallRequested request,
        long issuedAtUnixMs,
        IEventHandlerContext ctx,
        CancellationToken ct,
        string? transportLeaseId = null)
    {
        var state = HydrateRuntimeStateFromActor(ctx);

        _logger.LogDebug(
            "Voice tool call {ToolName} callId={CallId} envelopeLease={EnvelopeLease} stateLease={StateLease} status={Status}",
            request.ToolName,
            request.CallId,
            transportLeaseId,
            state.ActiveTransportLeaseId,
            state.Status);

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
                    ctx.AgentId,
                    state.ActiveSessionId ?? string.Empty,
                    request.CallId,
                    issuedAtUnixMs,
                    request.ToolName,
                    string.IsNullOrWhiteSpace(request.ArgumentsJson) ? "{}" : request.ArgumentsJson,
                    state.ActiveToolContext?.Clone(),
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

        await DeliverToolResultAsync(state, request.CallId, resultJson, transportLeaseId, ctx, ct);
    }

    private async Task HandleFunctionCallOutputAsync(
        VoiceFunctionCallOutput output,
        VoicePresenceRuntimeState state,
        VoiceClientToolCallCompletionFence completionFence,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var pendingCall = VoiceClientToolCallStateMachine.CompletePendingCall(
            state,
            output,
            completionFence);
        if (pendingCall == null)
            return;

        await DeliverToolResultAsync(
            state,
            pendingCall.CallId,
            VoiceClientToolCallStateMachine.BuildOutputJson(output),
            pendingCall.TransportLeaseId,
            ctx,
            ct);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private async Task DeliverToolResultAsync(
        VoicePresenceRuntimeState state,
        string callId,
        string resultJson,
        string? envelopeLeaseId,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var deliveryStatus = await TrySendLeaseUpstreamAsync(
            state,
            ctx,
            envelopeLeaseId,
            "tool_result",
            (mediaPort, transportLeaseId, upstreamCt) =>
                mediaPort.TrySendToolResultAsync(transportLeaseId, callId, resultJson, upstreamCt),
            ct);
        if (deliveryStatus != VoiceLeaseUpstreamDeliveryStatus.NoLease)
            return;

        _logger.LogWarning(
            "Voice tool result callId={CallId} had no transport lease (envelope and state both empty); using a throwaway provider session.",
            callId);

        await using var providerSession = await ConnectProviderSessionAsync(state, ct);
        await providerSession.SendToolResultAsync(callId, resultJson, ct);
    }

    private async Task<VoiceLeaseUpstreamDeliveryStatus> TrySendLeaseUpstreamAsync(
        VoicePresenceRuntimeState state,
        IEventHandlerContext ctx,
        string? envelopeLeaseId,
        string operationName,
        Func<IVoiceVolatileMediaStreamPort, string, CancellationToken, Task<bool>> sendAsync,
        CancellationToken ct)
    {
        var transportLeaseId = !string.IsNullOrWhiteSpace(envelopeLeaseId)
            ? envelopeLeaseId
            : state.ActiveTransportLeaseId;
        if (string.IsNullOrWhiteSpace(transportLeaseId))
            return VoiceLeaseUpstreamDeliveryStatus.NoLease;

        var mediaPort = ctx.Services.GetService<IVoiceVolatileMediaStreamPort>();
        if (mediaPort == null)
        {
            _logger.LogWarning(
                "Voice upstream operation {OperationName} for lease={TransportLeaseId} could not be delivered because the live media port is unavailable.",
                operationName,
                transportLeaseId);
            return VoiceLeaseUpstreamDeliveryStatus.DeliveryGap;
        }

        if (await sendAsync(mediaPort, transportLeaseId, ct))
        {
            _logger.LogInformation(
                "Voice upstream operation {OperationName} delivered via live relay lease={TransportLeaseId}.",
                operationName,
                transportLeaseId);
            return VoiceLeaseUpstreamDeliveryStatus.Delivered;
        }

        _logger.LogWarning(
            "Voice upstream operation {OperationName} for lease={TransportLeaseId} could not be delivered because no live relay exists on this host.",
            operationName,
            transportLeaseId);
        return VoiceLeaseUpstreamDeliveryStatus.DeliveryGap;
    }

    private async Task<VoiceSessionConfig?> BuildEffectiveSessionConfigAsync(
        VoicePresenceRuntimeState state,
        CancellationToken ct)
    {
        var effectiveSession = ResolveBaseSessionConfig(state);
        if (_toolCatalog == null)
            return effectiveSession;

        VoiceToolCatalogSnapshot snapshot;
        try
        {
            snapshot = await _toolCatalog.DiscoverAsync(state.ActiveToolContext?.Clone(), ct);
            VoiceToolCatalogSnapshotValidator.Validate(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice tool catalog materialization failed closed during session initialization.");
            effectiveSession ??= new VoiceSessionConfig();
            effectiveSession.ToolNames.Clear();
            effectiveSession.ToolDefinitions.Clear();
            return effectiveSession;
        }

        effectiveSession ??= new VoiceSessionConfig();
        effectiveSession.ToolNames.Clear();
        effectiveSession.ToolDefinitions.Clear();
        foreach (var discoveredTool in snapshot.Tools)
        {
            var toolName = discoveredTool.Name?.Trim();
            effectiveSession.ToolDefinitions.Add(new VoiceToolDefinition
            {
                Name = toolName!,
                Description = discoveredTool.Description ?? string.Empty,
                ParametersSchema = discoveredTool.ParametersSchema,
                Owner = discoveredTool.Owner,
            });
        }

        return effectiveSession;
    }

    private async Task<VoiceToolOwner> ResolveToolOwnerAsync(
        VoicePresenceRuntimeState state,
        string toolName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return VoiceToolOwner.Actor;

        var effectiveSessionConfig = await BuildEffectiveSessionConfigAsync(state, ct);
        var toolDefinition = effectiveSessionConfig?.ToolDefinitions.FirstOrDefault(definition =>
            string.Equals(definition.Name, toolName, StringComparison.OrdinalIgnoreCase));

        return toolDefinition?.Owner == VoiceToolOwner.Client
            ? VoiceToolOwner.Client
            : VoiceToolOwner.Actor;
    }

    private VoiceSessionConfig? ResolveBaseSessionConfig(VoicePresenceRuntimeState state)
    {
        if (state.ActiveSessionConfig != null)
            return state.ActiveSessionConfig.Clone();

        var effectiveSession = _sessionConfig?.Clone();
        return effectiveSession == null ? null : NormalizeProviderSessionConfig(effectiveSession);
    }

    private static string BuildToolErrorJson(string message) =>
        JsonSerializer.Serialize(new { error = message });

    private static long ResolveIssuedAtUnixMs(EventEnvelope envelope) =>
        envelope.Timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds() ?? 0;

    private async Task HandleControlFrameAsync(VoiceControlFrame frame, IEventHandlerContext ctx, CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);
        await HandleControlFrameAsync(frame, ctx, state, null, ct);
    }

    private async Task HandleControlFrameAsync(
        VoiceControlFrame frame,
        IEventHandlerContext ctx,
        VoicePresenceRuntimeState state,
        VoiceClientToolCallCompletionFence? clientToolCallCompletionFence,
        CancellationToken ct)
    {
        switch (frame.FrameCase)
        {
            case VoiceControlFrame.FrameOneofCase.DrainAcknowledged:
                ApplyDrainAcknowledged(
                    state,
                    frame.DrainAcknowledged.ResponseId,
                    frame.DrainAcknowledged.PlayoutSequence);
                await FlushPendingEventInjectionsAsync(state, ctx, ct);
                await PersistRuntimeStateAsync(ctx, state, ct);
                break;
            case VoiceControlFrame.FrameOneofCase.FunctionCallOutput:
                if (clientToolCallCompletionFence != null)
                {
                    await HandleFunctionCallOutputAsync(
                        frame.FunctionCallOutput,
                        state,
                        clientToolCallCompletionFence,
                        ctx,
                        ct);
                }

                break;
            case VoiceControlFrame.FrameOneofCase.InputImage:
                break;
            case VoiceControlFrame.FrameOneofCase.None:
            default:
                break;
        }
    }

    private async Task HandleExternalEventAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        var state = HydrateRuntimeStateFromActor(ctx);

        if (!ShouldInjectExternalEvent(envelope, ctx.AgentId, state))
            return;

        var now = _options.TimeProvider.GetUtcNow();
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

        await TryInjectEventAsync(state, injection, ctx, ct);
        await PersistRuntimeStateAsync(ctx, state, ct);
    }

    private bool ShouldInjectExternalEvent(
        EventEnvelope envelope,
        string agentId,
        VoicePresenceRuntimeState state)
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

        if (envelope.Route?.IsPublication() == true)
            return !string.Equals(envelope.Route.PublisherActorId, agentId, StringComparison.Ordinal);

        if (!IsConfiguredDirectExternalEvent(envelope, agentId))
            return false;

        if (HasActiveSession(state))
            return true;

        if (_options.DirectExternalEventNoActiveSessionPolicy ==
            VoiceDirectExternalEventNoActiveSessionPolicy.DropAndLog)
        {
            _logger.LogInformation(
                "Dropping direct external voice event: reason=no_active_session envelope_id={EnvelopeId} publisher_actor_id={PublisherActorId} event_type={EventType} target_actor_id={TargetActorId}.",
                envelope.Id ?? string.Empty,
                envelope.Route?.PublisherActorId ?? string.Empty,
                envelope.Payload.TypeUrl ?? string.Empty,
                envelope.Route?.GetTargetActorId() ?? string.Empty);
        }

        return false;
    }

    private bool IsConfiguredDirectExternalEvent(EventEnvelope envelope, string? agentId)
    {
        if (_directExternalEventTypeUrls.Count == 0 ||
            envelope.Payload == null ||
            envelope.Route?.IsDirect() != true ||
            !string.Equals(
                envelope.Route.PublisherActorId,
                TrustedDirectExternalEventPublisherActorId,
                StringComparison.Ordinal) ||
            !_directExternalEventTypeUrls.Contains(envelope.Payload.TypeUrl ?? string.Empty))
        {
            return false;
        }

        return agentId == null ||
               string.Equals(envelope.Route.GetTargetActorId(), agentId, StringComparison.Ordinal);
    }

    private bool HasActiveSession(VoicePresenceRuntimeState state) =>
        !string.IsNullOrWhiteSpace(state.ActiveSessionId) &&
        !IsLeaseExpired(state.LeaseExpiresAt);

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

    private async Task FlushPendingEventInjectionsAsync(
        VoicePresenceRuntimeState state,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        while (state.PendingInjections.Count > 0 && IsReadyToInject(state))
        {
            var next = state.PendingInjections[0];
            state.PendingInjections.RemoveAt(0);
            if (IsExpired(next))
                continue;

            if (await TryInjectEventAsync(state, next, ctx, ct))
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
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        var providerInjection = BuildProviderInjection(injection);
        try
        {
            var deliveryStatus = await TrySendLeaseUpstreamAsync(
                state,
                ctx,
                null,
                "event_injection",
                (mediaPort, transportLeaseId, upstreamCt) =>
                    mediaPort.TryInjectEventAsync(transportLeaseId, providerInjection, upstreamCt),
                ct);
            if (deliveryStatus == VoiceLeaseUpstreamDeliveryStatus.Delivered)
            {
                state.AwaitingInjectedResponseStart = true;
                return true;
            }

            if (deliveryStatus == VoiceLeaseUpstreamDeliveryStatus.DeliveryGap)
                return false;

            await using var providerSession = await ConnectProviderSessionAsync(state, ct);
            await providerSession.InjectEventAsync(providerInjection, ct);
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

    private async Task PersistRuntimeStateAsync(
        IEventHandlerContext ctx,
        VoicePresenceRuntimeState state,
        CancellationToken ct)
    {
        var normalized = NormalizeRuntimeState(state);
        RefreshCapabilityFacts(normalized, ctx, preserveActiveSessionConfig: true);

        if (ctx.Agent is not IVoicePresenceRuntimeStateOwner stateOwner)
            return;

        await stateOwner.PersistVoicePresenceRuntimeStateAsync(Name, normalized, ct);
    }

    private void RefreshCapabilityFacts(
        VoicePresenceRuntimeState state,
        IEventHandlerContext ctx,
        VoiceSessionOverrides? sessionOverrides = null,
        bool preserveActiveSessionConfig = false)
    {
        if (preserveActiveSessionConfig &&
            sessionOverrides == null &&
            state.ActiveSessionConfig != null)
        {
            state.ActiveSessionConfig = NormalizeProviderSessionConfig(state.ActiveSessionConfig);
        }
        else
        {
            state.ActiveSessionConfig = BuildResolvedProviderSessionConfig(ctx, sessionOverrides);
        }

        state.Initialized = IsInitialized;
        state.PcmSampleRateHz = ResolveConfiguredSampleRateHz(state.ActiveSessionConfig);
        if (state.RemoteAudioSupport == VoiceRemoteAudioSupport.Unspecified)
            state.RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly;
    }

    private VoiceSessionConfig BuildResolvedProviderSessionConfig(
        IEventHandlerContext ctx,
        VoiceSessionOverrides? sessionOverrides)
    {
        var session = _sessionConfig?.Clone() ?? new VoiceSessionConfig();
        if (ctx.Agent is IVoicePresenceRuntimeStateOwner stateOwner &&
            stateOwner.TryGetVoiceSessionDefaults(Name, out var defaults))
        {
            ApplyDefaults(session, defaults);
        }

        ApplyOverrides(session, sessionOverrides);
        return NormalizeProviderSessionConfig(session);
    }

    private static void ApplyDefaults(VoiceSessionConfig session, VoiceSessionDefaults defaults)
    {
        if (defaults.HasVoice)
            session.Voice = defaults.Voice?.Trim() ?? string.Empty;
        if (defaults.HasInstructions)
            session.Instructions = defaults.Instructions ?? string.Empty;
        if (defaults.HasSampleRateHz)
            session.SampleRateHz = defaults.SampleRateHz;
        if (defaults.HasTurnDetectionMode)
            session.TurnDetectionMode = defaults.TurnDetectionMode;
        if (defaults.HasVadDetectionThreshold)
            session.VadDetectionThreshold = defaults.VadDetectionThreshold;
        if (defaults.HasVadPrefixPaddingMs)
            session.VadPrefixPaddingMs = defaults.VadPrefixPaddingMs;
        if (defaults.HasVadSilenceDurationMs)
            session.VadSilenceDurationMs = defaults.VadSilenceDurationMs;
    }

    private static void ApplyOverrides(VoiceSessionConfig session, VoiceSessionOverrides? overrides)
    {
        if (overrides == null)
            return;

        if (overrides.HasVoice)
            session.Voice = overrides.Voice?.Trim() ?? string.Empty;
        if (overrides.HasInstructions)
            session.Instructions = overrides.Instructions ?? string.Empty;
        if (overrides.HasSampleRateHz)
            session.SampleRateHz = overrides.SampleRateHz;
        if (overrides.HasTurnDetectionMode)
            session.TurnDetectionMode = overrides.TurnDetectionMode;
        if (overrides.HasVadDetectionThreshold)
            session.VadDetectionThreshold = overrides.VadDetectionThreshold;
        if (overrides.HasVadPrefixPaddingMs)
            session.VadPrefixPaddingMs = overrides.VadPrefixPaddingMs;
        if (overrides.HasVadSilenceDurationMs)
            session.VadSilenceDurationMs = overrides.VadSilenceDurationMs;
    }

    private static VoiceSessionConfig NormalizeProviderSessionConfig(VoiceSessionConfig session)
    {
        var normalized = session.Clone();
        normalized.Voice = normalized.Voice?.Trim() ?? string.Empty;
        normalized.Instructions ??= string.Empty;
        normalized.SampleRateHz = ResolveConfiguredSampleRateHz(normalized);
        if (normalized.TurnDetectionMode == VoiceTurnDetectionMode.Unspecified)
            normalized.TurnDetectionMode = VoiceTurnDetectionMode.ServerVad;
        return normalized;
    }

    private static int ResolveConfiguredSampleRateHz(VoiceSessionConfig? session) =>
        session is { SampleRateHz: > 0 }
            ? session.SampleRateHz
            : WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz;

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
        if (normalized.ActiveSessionConfig != null)
            normalized.ActiveSessionConfig = NormalizeProviderSessionConfig(normalized.ActiveSessionConfig);

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
        if (state.LastDrainAckResponseId >= responseId)
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
