using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Deduplication;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[ImplicitStreamSubscription(OrleansRuntimeConstants.ActorEventStreamNamespace)]
public sealed class RuntimeActorGrain : Grain, IRuntimeActorGrain
{
    private readonly IPersistentState<RuntimeActorGrainState> _state;
    private IAgent? _agent;
    private string? _activeKind;
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

        await ResumeFromPersistedIdentityAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the persisted identity (kind-first, legacy <c>AgentTypeName</c>
    /// second) into an <see cref="AgentImplementation"/> and binds it to the
    /// grain. On the legacy path, lazy-tags <c>Identity.Kind</c> back onto
    /// state so subsequent activations skip the CLR-name lookup; the
    /// existing <c>AgentTypeName</c> field is preserved untouched until
    /// Phase 3 hard-deprecation so mixed-version pods stay compatible.
    /// </summary>
    private async Task ResumeFromPersistedIdentityAsync(CancellationToken ct)
    {
        var identity = _state.State.Identity;
        if (identity != null && !string.IsNullOrWhiteSpace(identity.Kind))
        {
            await BindAgentByKindAsync(identity.Kind, ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_state.State.AgentTypeName))
            await BindAgentByLegacyClrTypeAsync(_state.State.AgentTypeName, ct);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (_selfStreamHandle != null)
        {
            try
            {
                await _selfStreamHandle.UnsubscribeAsync();
            }
            catch (Exception ex)
            {
                if (!ShouldIgnoreSelfStreamUnsubscribeFailure(ex))
                    throw;

                _logger.LogWarning(
                    ex,
                    "Failed to unsubscribe self stream for actor {ActorId} during deactivation.",
                    this.GetPrimaryKeyString());
            }

            _selfStreamHandle = null;
        }

        if (_agent != null)
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            await _agent.DeactivateAsync(cancellationToken);
            _agent = null;
            _activeKind = null;
        }

        TriggerDeactivationHook();
    }

    private static bool ShouldIgnoreSelfStreamUnsubscribeFailure(Exception ex)
    {
        return ex switch
        {
            ObjectDisposedException => true,
            OrleansMessageRejectionException => true,
            AggregateException aggregate => aggregate.InnerExceptions.All(ShouldIgnoreSelfStreamUnsubscribeFailure),
            _ when ex.InnerException != null => ShouldIgnoreSelfStreamUnsubscribeFailure(ex.InnerException),
            _ => false,
        };
    }

    public async Task<bool> InitializeAgentAsync(string agentTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentTypeName);

        if (_agent != null)
            return string.Equals(_state.State.AgentTypeName, agentTypeName, StringComparison.Ordinal);

        var bound = await BindAgentByLegacyClrTypeAsync(agentTypeName);
        if (!bound)
            return false;

        _state.State.AgentId = this.GetPrimaryKeyString();
        _state.State.AgentTypeName = agentTypeName;
        await _state.WriteStateAsync();
        return true;
    }

    public async Task<bool> InitializeAgentByKindAsync(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (_agent != null)
            return string.Equals(_activeKind, kind, StringComparison.Ordinal);

        var bound = await BindAgentByKindAsync(kind);
        if (!bound)
            return false;

        _state.State.AgentId = this.GetPrimaryKeyString();
        _state.State.Identity = new RuntimeActorIdentity { Kind = kind };
        await _state.WriteStateAsync();
        return true;
    }

    public Task<bool> IsInitializedAsync() =>
        Task.FromResult(
            _agent != null
            || !string.IsNullOrWhiteSpace(_state.State.AgentTypeName)
            || !string.IsNullOrWhiteSpace(_state.State.Identity?.Kind));

    public Task HandleEnvelopeAsync(byte[] envelopeBytes) =>
        HandleEnvelopeAsyncCore(envelopeBytes, propagateFailure: false);

    private async Task HandleEnvelopeAsyncCore(byte[] envelopeBytes, bool propagateFailure)
    {
        if (_agent == null)
        {
            await ResumeFromPersistedIdentityAsync(CancellationToken.None);

            if (_agent == null)
            {
                if (!string.IsNullOrWhiteSpace(_state.State.AgentTypeName)
                    || !string.IsNullOrWhiteSpace(_state.State.Identity?.Kind))
                {
                    _logger.LogWarning(
                        "Dropping envelope for actor {ActorId}: initialization failed",
                        this.GetPrimaryKeyString());
                }
                else
                {
                    _logger.LogDebug(
                        "Dropping envelope for actor {ActorId}: no agent identity configured",
                        this.GetPrimaryKeyString());
                }

                return;
            }
        }

        var envelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
        propagateFailure = propagateFailure || ShouldPropagateDirectDispatchFailure(envelope);
        if (await TryHandleCompatibilityRetryAsync(envelope, propagateFailure))
            return;

        if (_deduplicator != null &&
            RuntimeEnvelopeDeduplication.TryBuildDedupKey(this.GetPrimaryKeyString(), envelope, out var dedupKey))
        {
            if (!await _deduplicator.TryRecordAsync(dedupKey))
                return;
        }

        if (VisitedActorChain.ShouldDropForReceiver(envelope, this.GetPrimaryKeyString()))
            return;

        var selfActorId = this.GetPrimaryKeyString();
        var route = envelope.Route;
        var isObserverPublication = route.IsObserverPublication();
        if (isObserverPublication)
        {
            if (!StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, selfActorId) ||
                StreamForwardingRules.IsTransitOnlyForwarding(envelope))
            {
                return;
            }
        }

        if (isObserverPublication)
        {
            // Forwarded observer publications are already explicitly targeted by the
            // stream-layer relay path and should not fall through topology routing.
        }
        else if (route.IsDirect())
        {
            if (!string.Equals(route.GetTargetActorId(), selfActorId, StringComparison.Ordinal))
                return;
        }
        else
        {
            switch (route.GetTopologyAudience())
            {
                case TopologyAudience.Self:
                    break;
                case TopologyAudience.Parent:
                    // Skip orphan-fallback events published by self to own stream
                    if (string.Equals(route?.PublisherActorId, selfActorId, StringComparison.Ordinal))
                        return;
                    break;
                case TopologyAudience.Children:
                case TopologyAudience.ParentAndChildren:
                    if (StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, selfActorId))
                    {
                        if (StreamForwardingRules.IsTransitOnlyForwarding(envelope))
                            return;
                        break;
                    }

                    if (string.Equals(envelope.Runtime?.SourceActorId, selfActorId, StringComparison.Ordinal))
                    {
                        return;
                    }
                    break;
                default:
                    return;
            }
        }

        using var scope = EventHandleScope.Begin(_logger, this.GetPrimaryKeyString(), envelope);
        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            await _agent.HandleEventAsync(envelope);
        }
        catch (Exception ex)
        {
            scope.MarkError(ex);
            if (await TryScheduleRetryAsync(envelope, ex))
                return;

            _logger.LogError(
                ex,
                "Runtime envelope handling failed after retry exhausted (or retry disabled) for actor {ActorId}, envelope {EnvelopeId}, event type '{EventTypeUrl}'.",
                this.GetPrimaryKeyString(),
                envelope.Id,
                envelope.Payload?.TypeUrl ?? "(none)");

            if (propagateFailure)
                throw;
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

    public Task<string> GetAgentKindAsync() =>
        Task.FromResult(_state.State.Identity?.Kind ?? _activeKind ?? string.Empty);

    public async Task DeactivateAsync()
    {
        if (_agent != null)
        {
            await _agent.DeactivateAsync();
            _agent = null;
            _activeKind = null;
        }

        DeactivateOnIdle();
    }

    public async Task PurgeAsync()
    {
        if (_agent != null)
        {
            await _agent.DeactivateAsync();
            _agent = null;
            _activeKind = null;
        }

        _state.State = new RuntimeActorGrainState();
        await _state.ClearStateAsync();
    }

    private async Task<bool> BindAgentByKindAsync(string kind, CancellationToken ct = default)
    {
        var registry = ServiceProvider?.GetService<IAgentKindRegistry>();
        if (registry == null)
        {
            _logger.LogError(
                "Cannot bind actor {ActorId} by kind '{Kind}': IAgentKindRegistry not registered.",
                this.GetPrimaryKeyString(),
                kind);
            return false;
        }

        AgentImplementation implementation;
        try
        {
            implementation = registry.Resolve(kind);
        }
        catch (UnknownAgentKindException ex)
        {
            _logger.LogError(
                ex,
                "Unable to resolve agent kind '{Kind}' for actor {ActorId}.",
                kind,
                this.GetPrimaryKeyString());
            return false;
        }

        var bound = await BindAgentAsync(implementation, ct);
        if (!bound)
            return false;

        _activeKind = kind;
        return true;
    }

    private async Task<bool> BindAgentByLegacyClrTypeAsync(string clrTypeName, CancellationToken ct = default)
    {
        var registry = ServiceProvider?.GetService<IAgentKindRegistry>();
        var legacyResolver = ServiceProvider?.GetService<ILegacyAgentClrTypeResolver>();

        // Prefer kind resolution: if a registered class lists this CLR full
        // name (current Type.FullName or [LegacyClrTypeName]), bind by kind
        // and lazy-tag Identity.Kind so subsequent activations skip this lane.
        if (registry != null && TryNormalizeClrTypeName(clrTypeName, out var normalizedClrName) &&
            registry.TryResolveKindByClrTypeName(normalizedClrName, out var resolvedKind))
        {
            var implementation = registry.Resolve(resolvedKind);
            if (await BindAgentAsync(implementation, ct))
            {
                await LazyTagIdentityAsync(resolvedKind, clrTypeName);
                _activeKind = resolvedKind;
                return true;
            }

            return false;
        }

        // Phase 1 transitional fallback: un-decorated [GAgent] classes still
        // need to activate. Reflection encapsulated here, never in the grain
        // body, so Phase 3 hard-deprecation drops it by removing the
        // ILegacyAgentClrTypeResolver registration.
        if (legacyResolver != null && legacyResolver.TryResolve(clrTypeName, out var legacyImpl))
        {
            var bound = await BindAgentAsync(legacyImpl, ct);
            if (bound)
                _activeKind = legacyImpl.Metadata.Kind;
            return bound;
        }

        _logger.LogError(
            "Unable to resolve agent for actor {ActorId}: persisted AgentTypeName '{ClrTypeName}' is not registered with IAgentKindRegistry and no transitional fallback is available.",
            SafeGetActorIdForLog(),
            clrTypeName);
        return false;
    }

    private string SafeGetActorIdForLog()
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch
        {
            // Bare-grain unit-test scenarios construct RuntimeActorGrain
            // without a runtime context; logging must degrade rather than
            // mask the original activation failure with NRE noise.
            return "(uninitialized)";
        }
    }

    private async Task<bool> BindAgentAsync(AgentImplementation implementation, CancellationToken ct)
    {
        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            var agent = implementation.Factory()
                ?? throw new InvalidOperationException(
                    $"Agent factory for kind '{implementation.Metadata.Kind}' returned null.");
            InjectDependencies(agent, this.GetPrimaryKeyString());
            await agent.ActivateAsync(ct);
            _agent = agent;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to initialize grain actor {ActorId} for kind '{Kind}' (impl '{ImplClr}').",
                this.GetPrimaryKeyString(),
                implementation.Metadata.Kind,
                implementation.Metadata.ImplementationClrTypeName);
            return false;
        }
    }

    private async Task LazyTagIdentityAsync(string kind, string legacyClrTypeName)
    {
        var existing = _state.State.Identity;
        if (existing != null && string.Equals(existing.Kind, kind, StringComparison.Ordinal))
            return;

        _state.State.Identity = new RuntimeActorIdentity
        {
            Kind = kind,
            StateSchemaVersion = existing?.StateSchemaVersion ?? 0,
            LegacyClrTypeName = legacyClrTypeName,
        };
        await _state.WriteStateAsync();
    }

    /// <summary>
    /// Strip the assembly-qualifier from a CLR type token so the registry
    /// can match against <see cref="Type.FullName"/>. Tests + bootstrap
    /// helpers historically pass <see cref="Type.AssemblyQualifiedName"/>.
    /// </summary>
    private static bool TryNormalizeClrTypeName(string clrTypeName, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName))
        {
            normalized = string.Empty;
            return false;
        }

        var commaIndex = clrTypeName.IndexOf(',');
        normalized = commaIndex < 0 ? clrTypeName.Trim() : clrTypeName[..commaIndex].Trim();
        return normalized.Length > 0;
    }

    private void InjectDependencies(IAgent agent, string actorId)
    {
        if (agent is not GAgentBase gAgent)
            return;

        var loggerFactory = ServiceProvider.GetService<ILoggerFactory>();
        var agentLogger = loggerFactory?.CreateLogger(agent.GetType().Name) ?? NullLogger.Instance;

        gAgent.SetId(actorId);
        var publisher = new Actors.OrleansGrainEventPublisher(
            actorId,
            () => _state.State.ParentId,
            _propagationPolicy,
            _streams);
        gAgent.EventPublisher = publisher;
        gAgent.CommittedStateEventPublisher = publisher;
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
        if (envelope.Route.IsObserverPublication() &&
            (!StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, this.GetPrimaryKeyString()) ||
             StreamForwardingRules.IsTransitOnlyForwarding(envelope)))
        {
            return Task.CompletedTask;
        }

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
        {
            var scheduler = ServiceProvider.GetRequiredService<IActorRuntimeCallbackScheduler>();
            await scheduler.ScheduleTimeoutAsync(
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = this.GetPrimaryKeyString(),
                    CallbackId = BuildRuntimeRetryCallbackId(envelope, nextAttempt),
                    DueTime = TimeSpan.FromMilliseconds(_runtimeEnvelopeRetryPolicy.RetryDelayMs),
                    TriggerEnvelope = retryEnvelope,
                    DeliveryMode = RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                });
        }
        else
        {
            await _streams.GetStream(this.GetPrimaryKeyString()).ProduceAsync(retryEnvelope);
        }

        _logger.LogWarning(
            ex,
            "Runtime envelope retry scheduled for actor {ActorId}, attempt {Attempt}/{MaxAttempts}.",
            this.GetPrimaryKeyString(),
            nextAttempt,
            _runtimeEnvelopeRetryPolicy.MaxAttempts);
        return true;
    }

    private string BuildRuntimeRetryCallbackId(EventEnvelope envelope, int nextAttempt)
    {
        var originId = RuntimeEnvelopeDeduplication.ResolveOriginId(envelope) ?? envelope.Id;

        if (string.IsNullOrWhiteSpace(originId))
            originId = envelope.Id ?? Guid.NewGuid().ToString("N");

        return RuntimeCallbackKeyComposer.BuildCallbackId(
            "runtime-envelope-retry",
            originId,
            nextAttempt.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<bool> TryHandleCompatibilityRetryAsync(EventEnvelope envelope, bool propagateFailure)
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

        if (propagateFailure)
            throw compatibilityException;

        return true;
    }

    private static bool ShouldPropagateDirectDispatchFailure(EventEnvelope envelope) =>
        envelope.Runtime?.Dispatch?.PropagateFailure == true;
}
