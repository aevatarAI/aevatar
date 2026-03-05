using System.Globalization;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[ImplicitStreamSubscription(OrleansRuntimeConstants.ActorEventStreamNamespace)]
public sealed class RuntimeActorGrain : Grain, IRuntimeActorGrain, IRuntimeActorInlineCallbackScheduler
{
    private const string RetryAttemptMetadataKey = "aevatar.retry.attempt";
    private const string RetryOriginEventIdMetadataKey = "aevatar.retry.origin_event_id";

    private readonly IPersistentState<RuntimeActorGrainState> _state;
    private readonly Dictionary<string, ScheduledRuntimeCallback> _runtimeCallbacks = new(StringComparer.Ordinal);
    private IAgent? _agent;
    private IEventDeduplicator? _deduplicator;
    private IEnvelopePropagationPolicy _propagationPolicy =
        new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy());
    private Aevatar.Foundation.Abstractions.IStreamProvider _streams = null!;
    private IRuntimeActorStateBindingAccessor? _stateBindingAccessor;
    private IRuntimeActorInlineCallbackSchedulerBindingAccessor? _inlineCallbackSchedulerBindingAccessor;
    private IActorDeactivationHookDispatcher? _deactivationHookDispatcher;
    private ILogger<RuntimeActorGrain> _logger = NullLogger<RuntimeActorGrain>.Instance;
    private IAsyncStream<EventEnvelope>? _selfStream;
    private StreamSubscriptionHandle<EventEnvelope>? _selfStreamHandle;
    private CompatibilityFailureInjectionPolicy _compatibilityFailureInjectionPolicy =
        CompatibilityFailureInjectionPolicy.Disabled;
    private RuntimeEnvelopeRetryPolicy _runtimeEnvelopeRetryPolicy =
        RuntimeEnvelopeRetryPolicy.Disabled;

    public RuntimeActorGrain(
        [PersistentState("agent", OrleansRuntimeConstants.GrainStateStorageName)] IPersistentState<RuntimeActorGrainState> state)
    {
        _state = state;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _deduplicator = ServiceProvider.GetService<IEventDeduplicator>();
        _propagationPolicy = ServiceProvider.GetService<IEnvelopePropagationPolicy>() ?? _propagationPolicy;
        _streams = ServiceProvider.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
        _stateBindingAccessor = ServiceProvider.GetService<IRuntimeActorStateBindingAccessor>();
        _inlineCallbackSchedulerBindingAccessor = ServiceProvider.GetService<IRuntimeActorInlineCallbackSchedulerBindingAccessor>();
        _deactivationHookDispatcher = ServiceProvider.GetService<IActorDeactivationHookDispatcher>();

        var loggerFactory = ServiceProvider.GetService<ILoggerFactory>();
        _logger = loggerFactory?.CreateLogger<RuntimeActorGrain>() ?? NullLogger<RuntimeActorGrain>.Instance;
        _compatibilityFailureInjectionPolicy = CompatibilityFailureInjectionPolicy.FromEnvironment();
        _runtimeEnvelopeRetryPolicy = RuntimeEnvelopeRetryPolicy.FromEnvironment();
        if (_compatibilityFailureInjectionPolicy.Enabled)
        {
            _logger.LogWarning(
                "Compatibility failure injection is enabled for node version tag '{NodeVersionTag}'.",
                Environment.GetEnvironmentVariable("AEVATAR_TEST_NODE_VERSION_TAG") ?? "(none)");
        }

        await SubscribeSelfStreamAsync();

        if (!string.IsNullOrWhiteSpace(_state.State.AgentTypeName))
            await InitializeAgentInternalAsync(_state.State.AgentTypeName, cancellationToken);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (_selfStreamHandle != null)
        {
            await _selfStreamHandle.UnsubscribeAsync();
            _selfStreamHandle = null;
        }

        foreach (var callback in _runtimeCallbacks.Values)
            callback.Timer.Dispose();
        _runtimeCallbacks.Clear();

        if (_agent != null)
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            await _agent.DeactivateAsync(cancellationToken);
            _agent = null;
        }

        TriggerDeactivationHook();
    }

    public async Task<bool> InitializeAgentAsync(string agentTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentTypeName);

        if (_agent != null)
            return string.Equals(_state.State.AgentTypeName, agentTypeName, StringComparison.Ordinal);

        var initialized = await InitializeAgentInternalAsync(agentTypeName);
        if (!initialized)
            return false;

        _state.State.AgentId = this.GetPrimaryKeyString();
        _state.State.AgentTypeName = agentTypeName;
        await _state.WriteStateAsync();
        return true;
    }

    public Task<bool> IsInitializedAsync() =>
        Task.FromResult(_agent != null || !string.IsNullOrWhiteSpace(_state.State.AgentTypeName));

    public async Task HandleEnvelopeAsync(byte[] envelopeBytes)
    {
        if (_agent == null)
        {
            if (!string.IsNullOrWhiteSpace(_state.State.AgentTypeName))
            {
                var initialized = await InitializeAgentInternalAsync(_state.State.AgentTypeName);
                if (!initialized || _agent == null)
                {
                    _logger.LogWarning("Dropping envelope for actor {ActorId}: initialization failed", this.GetPrimaryKeyString());
                    return;
                }
            }
            else
            {
                _logger.LogDebug("Dropping envelope for actor {ActorId}: no agent type configured", this.GetPrimaryKeyString());
                return;
            }
        }

        var envelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
        using var instrumentation = TracingContextHelpers.BeginHandleEnvelopeInstrumentation(
            _logger,
            this.GetPrimaryKeyString(),
            envelope);
        if (await TryHandleCompatibilityRetryAsync(envelope))
            return;

        if (!string.IsNullOrWhiteSpace(envelope.Id) && _deduplicator != null)
        {
            var dedupKey = BuildDedupKey(envelope);
            if (!await _deduplicator.TryRecordAsync(dedupKey))
                return;
        }

        if (PublisherChainMetadata.ShouldDropForReceiver(envelope, this.GetPrimaryKeyString()))
            return;

        var selfActorId = this.GetPrimaryKeyString();
        switch (envelope.Direction)
        {
            case EventDirection.Self:
            case EventDirection.Up:
                break;
            case EventDirection.Down:
            case EventDirection.Both:
                if (StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, selfActorId))
                {
                    if (StreamForwardingRules.IsTransitOnlyForwarding(envelope))
                        return;
                    break;
                }

                if (envelope.Metadata.TryGetValue(EnvelopeMetadataKeys.SourceActorId, out var sourceActorId) &&
                    string.Equals(sourceActorId, selfActorId, StringComparison.Ordinal))
                {
                    return;
                }
                break;
            default:
                return;
        }

        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            using var inlineSchedulerBinding = _inlineCallbackSchedulerBindingAccessor?.Bind(this);
            await _agent.HandleEventAsync(envelope);
        }
        catch (Exception ex)
        {
            if (await TryScheduleRetryAsync(envelope, ex))
                return;

            _logger.LogError(
                ex,
                "Runtime envelope handling failed after retry exhausted (or retry disabled) for actor {ActorId}, envelope {EnvelopeId}, event type '{EventTypeUrl}'.",
                this.GetPrimaryKeyString(),
                envelope.Id,
                envelope.Payload?.TypeUrl ?? "(none)");
        }
    }

    public async Task AddChildAsync(string childId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childId);
        if (_state.State.Children.Contains(childId, StringComparer.Ordinal))
            return;

        _state.State.Children.Add(childId);
        await _state.WriteStateAsync();
    }

    public async Task RemoveChildAsync(string childId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childId);
        if (!_state.State.Children.Remove(childId))
            return;

        await _state.WriteStateAsync();
    }

    public async Task SetParentAsync(string parentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        _state.State.ParentId = parentId;
        await _state.WriteStateAsync();
    }

    public async Task ClearParentAsync()
    {
        if (_state.State.ParentId == null)
            return;

        _state.State.ParentId = null;
        await _state.WriteStateAsync();
    }

    public Task<IReadOnlyList<string>> GetChildrenAsync() =>
        Task.FromResult<IReadOnlyList<string>>(_state.State.Children.ToList());

    public Task<string?> GetParentAsync() =>
        Task.FromResult(_state.State.ParentId);

    public Task<string> GetDescriptionAsync()
    {
        if (_agent == null)
            return Task.FromResult($"Uninitialized:{this.GetPrimaryKeyString()}");

        return _agent.GetDescriptionAsync();
    }

    public Task<string> GetAgentTypeNameAsync() =>
        Task.FromResult(_state.State.AgentTypeName ?? string.Empty);

    public async Task DeactivateAsync()
    {
        if (_agent != null)
        {
            await _agent.DeactivateAsync();
            _agent = null;
        }

        DeactivateOnIdle();
    }

    public async Task PurgeAsync()
    {
        if (_agent != null)
        {
            await _agent.DeactivateAsync();
            _agent = null;
        }

        _state.State.AgentId = string.Empty;
        _state.State.AgentTypeName = null;
        _state.State.ParentId = null;
        _state.State.Children.Clear();
        _state.State.AgentStateTypeName = null;
        _state.State.AgentStateSnapshot = null;
        _state.State.AgentStateSnapshotVersion = 0;
        await _state.WriteStateAsync();
    }

    private async Task<bool> InitializeAgentInternalAsync(string agentTypeName, CancellationToken ct = default)
    {
        var agentType = ResolveAgentType(agentTypeName);
        if (agentType == null)
        {
            _logger.LogError("Unable to resolve agent type {AgentTypeName}", agentTypeName);
            return false;
        }

        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            var agent = CreateAgentInstance(agentType);
            InjectDependencies(agent, this.GetPrimaryKeyString());
            await agent.ActivateAsync(ct);
            _agent = agent;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize grain actor {ActorId}", this.GetPrimaryKeyString());
            return false;
        }
    }

    private static Type? ResolveAgentType(string agentTypeName)
    {
        var resolved = Type.GetType(agentTypeName, throwOnError: false);
        if (resolved != null)
            return resolved;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolved = assembly.GetType(agentTypeName, throwOnError: false);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private IAgent CreateAgentInstance(Type agentType)
    {
        var instance = ActivatorUtilities.CreateInstance(ServiceProvider, agentType);
        return instance as IAgent
            ?? throw new InvalidOperationException($"Unable to create agent instance for {agentType.FullName}");
    }

    private void InjectDependencies(IAgent agent, string actorId)
    {
        if (agent is not GAgentBase gAgent)
            return;

        var loggerFactory = ServiceProvider.GetService<ILoggerFactory>();
        var agentLogger = loggerFactory?.CreateLogger(agent.GetType().Name) ?? NullLogger.Instance;

        gAgent.SetId(actorId);
        gAgent.EventPublisher = new Actors.OrleansGrainEventPublisher(
            actorId,
            () => _state.State.ParentId,
            envelope => HandleEnvelopeAsync(envelope.ToByteArray()),
            _propagationPolicy,
            _streams);
        gAgent.Logger = agentLogger;
        gAgent.Services = ServiceProvider;
        if (gAgent is IEventSourcingFactoryBinding statefulBinding)
            statefulBinding.BindEventSourcingFactory(ServiceProvider);
    }

    private async Task SubscribeSelfStreamAsync()
    {
        if (_selfStreamHandle != null)
            return;

        var options = ServiceProvider.GetService<AevatarOrleansRuntimeOptions>() ?? new AevatarOrleansRuntimeOptions();
        var streamProvider = this.GetStreamProvider(options.StreamProviderName);
        var streamId = StreamId.Create(options.ActorEventNamespace, this.GetPrimaryKeyString());
        _selfStream = streamProvider.GetStream<EventEnvelope>(streamId);

        _selfStreamHandle = await _selfStream.SubscribeAsync(OnSelfStreamEventAsync);
    }

    private Task OnSelfStreamEventAsync(EventEnvelope envelope, StreamSequenceToken? token = null)
    {
        _ = token;
        return HandleEnvelopeAsync(envelope.ToByteArray());
    }

    private void TriggerDeactivationHook()
    {
        if (_deactivationHookDispatcher == null)
            return;

        _ = _deactivationHookDispatcher.DispatchAsync(this.GetPrimaryKeyString(), CancellationToken.None);
    }

    private async Task<bool> TryScheduleRetryAsync(EventEnvelope envelope, Exception ex)
    {
        if (!_runtimeEnvelopeRetryPolicy.TryBuildRetryEnvelope(
                envelope,
                ex,
                out var retryEnvelope,
                out var nextAttempt))
            return false;

        if (_runtimeEnvelopeRetryPolicy.RetryDelayMs > 0)
            await Task.Delay(_runtimeEnvelopeRetryPolicy.RetryDelayMs);

        await _streams.GetStream(this.GetPrimaryKeyString()).ProduceAsync(retryEnvelope);
        _logger.LogWarning(
            ex,
            "Runtime envelope retry scheduled for actor {ActorId}, attempt {Attempt}/{MaxAttempts}.",
            this.GetPrimaryKeyString(),
            nextAttempt,
            _runtimeEnvelopeRetryPolicy.MaxAttempts);
        return true;
    }

    private string BuildDedupKey(EventEnvelope envelope)
    {
        var originId = envelope.Metadata.TryGetValue(RetryOriginEventIdMetadataKey, out var metadataOriginId) &&
                       !string.IsNullOrWhiteSpace(metadataOriginId)
            ? metadataOriginId
            : envelope.Id;

        if (string.IsNullOrWhiteSpace(originId))
            originId = envelope.Id ?? string.Empty;

        var attempt = 0;
        if (envelope.Metadata.TryGetValue(RetryAttemptMetadataKey, out var metadataAttempt) &&
            int.TryParse(metadataAttempt, out var parsedAttempt) &&
            parsedAttempt > 0)
        {
            attempt = parsedAttempt;
        }

        return $"{this.GetPrimaryKeyString()}:{originId}:{attempt}";
    }

    private async Task<bool> TryHandleCompatibilityRetryAsync(EventEnvelope envelope)
    {
        if (!_compatibilityFailureInjectionPolicy.ShouldInject(envelope.Payload?.TypeUrl))
            return false;

        _logger.LogWarning(
            "Injected compatibility failure for actor {ActorId}, event type '{EventTypeUrl}'.",
            this.GetPrimaryKeyString(),
            envelope.Payload?.TypeUrl ?? "(none)");

        var compatibilityException =
            new InvalidOperationException("Injected compatibility failure for mixed-version rollout testing.");
        if (await TryScheduleRetryAsync(envelope, compatibilityException))
            return true;

        _logger.LogError(
            compatibilityException,
            "Runtime envelope handling failed after compatibility retry exhausted (or retry disabled) for actor {ActorId}, envelope {EnvelopeId}, event type '{EventTypeUrl}'.",
            this.GetPrimaryKeyString(),
            envelope.Id,
            envelope.Payload?.TypeUrl ?? "(none)");
        return true;
    }

    string IRuntimeActorInlineCallbackScheduler.ActorId => this.GetPrimaryKeyString();

    Task<RuntimeCallbackLease> IRuntimeActorInlineCallbackScheduler.ScheduleTimeoutAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        TimeSpan dueTime,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var generation = ReplaceAndGetNextRuntimeCallbackGeneration(callbackId);
        var timer = this.RegisterGrainTimer(
            cancellationToken => OnInlineRuntimeCallbackTickAsync(callbackId, generation, cancellationToken),
            new GrainTimerCreationOptions(dueTime, Timeout.InfiniteTimeSpan)
            {
                KeepAlive = true,
                Interleave = false,
            });

        _runtimeCallbacks[callbackId] = new ScheduledRuntimeCallback(
            generation,
            false,
            triggerEnvelope.ToByteArray(),
            timer);

        return Task.FromResult(new RuntimeCallbackLease(
            this.GetPrimaryKeyString(),
            callbackId,
            generation,
            RuntimeCallbackBackend.Inline));
    }

    Task<RuntimeCallbackLease> IRuntimeActorInlineCallbackScheduler.ScheduleTimerAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        TimeSpan dueTime,
        TimeSpan period,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var generation = ReplaceAndGetNextRuntimeCallbackGeneration(callbackId);
        var timer = this.RegisterGrainTimer(
            cancellationToken => OnInlineRuntimeCallbackTickAsync(callbackId, generation, cancellationToken),
            new GrainTimerCreationOptions(dueTime, period)
            {
                KeepAlive = true,
                Interleave = false,
            });

        _runtimeCallbacks[callbackId] = new ScheduledRuntimeCallback(
            generation,
            true,
            triggerEnvelope.ToByteArray(),
            timer);

        return Task.FromResult(new RuntimeCallbackLease(
            this.GetPrimaryKeyString(),
            callbackId,
            generation,
            RuntimeCallbackBackend.Inline));
    }

    Task IRuntimeActorInlineCallbackScheduler.CancelAsync(
        string callbackId,
        long? expectedGeneration,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ct.ThrowIfCancellationRequested();

        if (!_runtimeCallbacks.TryGetValue(callbackId, out var callback))
            return Task.CompletedTask;

        if (expectedGeneration.HasValue &&
            expectedGeneration.Value > 0 &&
            callback.Generation != expectedGeneration.Value)
        {
            return Task.CompletedTask;
        }

        callback.Timer.Dispose();
        _runtimeCallbacks.Remove(callbackId);
        return Task.CompletedTask;
    }

    private long ReplaceAndGetNextRuntimeCallbackGeneration(string callbackId)
    {
        if (!_runtimeCallbacks.TryGetValue(callbackId, out var existing))
            return 1;

        existing.Timer.Dispose();
        _runtimeCallbacks.Remove(callbackId);
        return existing.Generation + 1;
    }

    private async Task OnInlineRuntimeCallbackTickAsync(
        string callbackId,
        long generation,
        CancellationToken ct)
    {
        if (!_runtimeCallbacks.TryGetValue(callbackId, out var scheduled))
            return;

        if (scheduled.Generation != generation)
            return;

        var fireIndex = scheduled.FireIndex + 1;
        var envelope = EventEnvelope.Parser.ParseFrom(scheduled.EnvelopeBytes);
        envelope.Direction = EventDirection.Self;
        envelope.TargetActorId = this.GetPrimaryKeyString();
        envelope.PublisherId = this.GetPrimaryKeyString();
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackId] = callbackId;
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackGeneration] = generation.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFireIndex] = fireIndex.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        await _streams.GetStream(this.GetPrimaryKeyString()).ProduceAsync(envelope, ct);

        if (!scheduled.Periodic)
        {
            scheduled.Timer.Dispose();
            _runtimeCallbacks.Remove(callbackId);
            return;
        }

        _runtimeCallbacks[callbackId] = scheduled with { FireIndex = fireIndex };
    }

    private sealed record ScheduledRuntimeCallback(
        long Generation,
        bool Periodic,
        byte[] EnvelopeBytes,
        IGrainTimer Timer,
        int FireIndex = 0);

}
