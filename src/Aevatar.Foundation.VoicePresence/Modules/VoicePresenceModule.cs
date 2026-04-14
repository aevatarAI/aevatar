using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Events;
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
public sealed class VoicePresenceModule : ILifecycleAwareEventModule, IAudioFastPath, IRouteBypassModule
{
    private static readonly JsonFormatter PayloadJsonFormatter = new(JsonFormatter.Settings.Default);

    private readonly IRealtimeVoiceProvider _provider;
    private readonly VoiceProviderConfig _providerConfig;
    private readonly VoiceSessionConfig? _sessionConfig;
    private readonly VoicePresenceModuleOptions _options;
    private readonly IVoiceToolInvoker? _toolInvoker;
    private readonly ILogger _logger;
    private readonly Queue<VoiceConversationEventInjection> _pendingInjections = [];

    private IVoiceTransport? _userTransport;
    private Func<IMessage, CancellationToken, Task>? _selfEventDispatcher;
    private CancellationTokenSource? _relayCts;
    private Task? _userToProviderRelay;
    private Task? _providerToUserRelay;
    private bool _awaitingInjectedResponseStart;

    public VoicePresenceModule(
        IRealtimeVoiceProvider provider,
        VoiceProviderConfig providerConfig,
        VoiceSessionConfig? sessionConfig = null,
        VoicePresenceModuleOptions? options = null,
        IVoiceToolInvoker? toolInvoker = null,
        ILogger? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerConfig = providerConfig?.Clone() ?? throw new ArgumentNullException(nameof(providerConfig));
        _sessionConfig = sessionConfig?.Clone();
        _options = options ?? new VoicePresenceModuleOptions();
        _toolInvoker = toolInvoker;
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

    public bool CanHandle(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return false;

        return envelope.Payload.Is(VoiceProviderEvent.Descriptor) ||
               envelope.Payload.Is(VoiceControlFrame.Descriptor) ||
               envelope.Route?.IsPublication() == true;
    }

    public async Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(VoiceProviderEvent.Descriptor))
        {
            await HandleProviderEventAsync(envelope.Payload.Unpack<VoiceProviderEvent>(), ctx, ct);
            return;
        }

        if (envelope.Payload.Is(VoiceControlFrame.Descriptor))
        {
            await HandleControlFrameAsync(envelope.Payload.Unpack<VoiceControlFrame>(), ct);
            return;
        }

        await HandleExternalEventAsync(envelope, ctx, ct);
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
        _pendingInjections.Clear();
        _awaitingInjectedResponseStart = false;
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
    public void AttachTransport(
        IVoiceTransport userTransport,
        Func<IMessage, CancellationToken, Task> selfEventDispatcher)
    {
        ArgumentNullException.ThrowIfNull(userTransport);
        ArgumentNullException.ThrowIfNull(selfEventDispatcher);

        if (_userTransport != null)
            throw new InvalidOperationException("A voice transport is already attached.");

        _userTransport = userTransport;
        _selfEventDispatcher = selfEventDispatcher;
        _relayCts = new CancellationTokenSource();

        _provider.OnEvent = OnProviderEventAsync;
        _userToProviderRelay = RunUserToProviderRelayAsync(_relayCts.Token);
        _providerToUserRelay = Task.CompletedTask;
    }

    /// <summary>
    /// Detaches the current transport and stops the relay loops.
    /// </summary>
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

    internal async Task HandleProviderEventAsync(
        VoiceProviderEvent providerEvent,
        IEventHandlerContext ctx,
        CancellationToken ct)
    {
        switch (providerEvent.EventCase)
        {
            case VoiceProviderEvent.EventOneofCase.ResponseStarted:
                _awaitingInjectedResponseStart = false;
                StateMachine.OnResponseStarted(providerEvent.ResponseStarted.ResponseId);
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseDone:
                StateMachine.OnResponseDone(providerEvent.ResponseDone.ResponseId);
                break;
            case VoiceProviderEvent.EventOneofCase.ResponseCancelled:
                _awaitingInjectedResponseStart = false;
                StateMachine.OnResponseCancelled(providerEvent.ResponseCancelled.ResponseId);
                await FlushPendingEventInjectionsAsync(ct);
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
            case VoiceProviderEvent.EventOneofCase.FunctionCall:
                await ExecuteToolCallAsync(providerEvent.FunctionCall, ctx, ct);
                break;
            case VoiceProviderEvent.EventOneofCase.Disconnected:
                _awaitingInjectedResponseStart = false;
                StateMachine.OnProviderDisconnected();
                break;
            case VoiceProviderEvent.EventOneofCase.AudioReceived:
            case VoiceProviderEvent.EventOneofCase.Error:
            case VoiceProviderEvent.EventOneofCase.None:
            default:
                break;
        }
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

    private static string BuildToolErrorJson(string message) =>
        JsonSerializer.Serialize(new { error = message });

    private async Task HandleControlFrameAsync(VoiceControlFrame frame, CancellationToken ct)
    {
        switch (frame.FrameCase)
        {
            case VoiceControlFrame.FrameOneofCase.DrainAcknowledged:
                StateMachine.OnDrainAcknowledged(
                    frame.DrainAcknowledged.ResponseId,
                    frame.DrainAcknowledged.PlayoutSequence);
                await FlushPendingEventInjectionsAsync(ct);
                break;
            case VoiceControlFrame.FrameOneofCase.None:
            default:
                break;
        }
    }

    private async Task HandleExternalEventAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (!ShouldInjectExternalEvent(envelope, ctx.AgentId))
            return;

        var now = _options.TimeProvider.GetUtcNow();
        var decision = EventPolicy.Evaluate(envelope, now);
        if (decision != VoicePresenceEventPolicyDecision.Admit)
            return;

        var injection = BuildInjection(envelope, now);
        if (!IsReadyToInject())
        {
            EnqueuePendingInjection(injection);
            return;
        }

        await TryInjectEventAsync(injection, ct);
    }

    private bool ShouldInjectExternalEvent(EventEnvelope envelope, string agentId)
    {
        if (envelope.Payload == null)
            return false;

        if (envelope.Payload.Is(VoiceProviderEvent.Descriptor) ||
            envelope.Payload.Is(VoiceControlFrame.Descriptor))
        {
            return false;
        }

        if (envelope.Route?.IsPublication() != true)
            return false;

        return !string.Equals(envelope.Route.PublisherActorId, agentId, StringComparison.Ordinal);
    }

    private VoiceConversationEventInjection BuildInjection(EventEnvelope envelope, DateTimeOffset now)
    {
        var observedAt = envelope.Timestamp?.ToDateTimeOffset() ?? now;
        return new VoiceConversationEventInjection
        {
            EnvelopeId = envelope.Id ?? string.Empty,
            PublisherActorId = envelope.Route?.PublisherActorId ?? string.Empty,
            EventType = envelope.Payload?.TypeUrl ?? string.Empty,
            PayloadJson = envelope.Payload == null ? "{}" : FormatPayloadJson(envelope.Payload),
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        };
    }

    private void EnqueuePendingInjection(VoiceConversationEventInjection injection)
    {
        if (_options.PendingInjectionCapacity <= 0)
            return;

        while (_pendingInjections.Count >= _options.PendingInjectionCapacity)
            _pendingInjections.Dequeue();

        _pendingInjections.Enqueue(injection);
    }

    private async Task FlushPendingEventInjectionsAsync(CancellationToken ct)
    {
        while (_pendingInjections.Count > 0 && IsReadyToInject())
        {
            var next = _pendingInjections.Dequeue();
            if (IsExpired(next))
                continue;

            if (await TryInjectEventAsync(next, ct))
                return;

            return;
        }
    }

    private bool IsExpired(VoiceConversationEventInjection injection)
    {
        var observedAt = injection.ObservedAt?.ToDateTimeOffset() ?? _options.TimeProvider.GetUtcNow();
        return _options.TimeProvider.GetUtcNow() - observedAt > _options.StaleAfter;
    }

    private bool IsReadyToInject() =>
        IsInitialized &&
        StateMachine.IsSafeToInject &&
        !_awaitingInjectedResponseStart;

    private async Task<bool> TryInjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
    {
        try
        {
            await _provider.InjectEventAsync(injection, ct);
            _awaitingInjectedResponseStart = true;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject external voice event {EventType}.", injection.EventType);
            return false;
        }
    }

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
}
