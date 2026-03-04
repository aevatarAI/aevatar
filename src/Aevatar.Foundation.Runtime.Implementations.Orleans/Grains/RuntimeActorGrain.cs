using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[ImplicitStreamSubscription(OrleansRuntimeConstants.ActorEventStreamNamespace)]
public sealed class RuntimeActorGrain : Grain, IRuntimeActorGrain
{
    private const string RetryAttemptMetadataKey = "aevatar.retry.attempt";
    private const string RetryOriginEventIdMetadataKey = "aevatar.retry.origin_event_id";

    private readonly IPersistentState<RuntimeActorGrainState> _state;
    private IAgent? _agent;
    private IEventDeduplicator? _deduplicator;
    private IEnvelopePropagationPolicy _propagationPolicy =
        new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy());
    private Aevatar.Foundation.Abstractions.IStreamProvider _streams = null!;
    private IRuntimeActorStateBindingAccessor? _stateBindingAccessor;
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

                if (envelope.Metadata.TryGetValue("__source_actor_id", out var sourceActorId) &&
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
        gAgent.ManifestStore = ServiceProvider.GetService<IAgentManifestStore>();
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

}
