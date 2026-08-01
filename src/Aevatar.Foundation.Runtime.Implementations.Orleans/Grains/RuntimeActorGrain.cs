using System.Globalization;
using System.Runtime.ExceptionServices;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
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
using Aevatar.Foundation.Runtime.Delivery;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
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
    // Set once OnActivateAsync has finished its identity-resolution attempt.
    // Without this, every inbound envelope that arrives while the agent is
    // unbound retries the registry probe, which amplifies a persistent
    // misconfiguration into per-envelope I/O.
    private bool _identityResolutionAttempted;
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
    /// Resolves the persisted primary kind identity into an
    /// <see cref="AgentImplementation"/> and binds it to the grain.
    /// </summary>
    private async Task ResumeFromPersistedIdentityAsync(CancellationToken ct)
    {
        _identityResolutionAttempted = true;

        var identity = _state.State.Identity;
        if (identity == null)
            return;

        if (string.IsNullOrWhiteSpace(identity.Kind))
        {
            throw new InvalidOperationException(
                $"Persisted runtime identity for actor '{SafeGetActorIdForLog()}' has no agent kind.");
        }

        await BindAgentByKindAsync(identity.Kind, ct, throwOnFailure: true);
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

    public async Task<bool> InitializeAgentByKindAsync(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        if (_agent != null)
            return KindResolvesToActiveImplementation(kind);

        var implementation = await BindAgentByKindAsync(kind);
        if (implementation == null)
            return false;

        var canonicalKind = implementation.Metadata.Kind;

        _state.State.AgentId = this.GetPrimaryKeyString();
        _state.State.Identity = new RuntimeActorIdentity { Kind = canonicalKind };
        await _state.WriteStateAsync();
        _identityResolutionAttempted = true;
        return true;
    }

    private bool KindResolvesToActiveImplementation(string kind) =>
        RuntimeActorIdentityResolution.ResolvesToSameImplementation(
            ServiceProvider?.GetService<IAgentKindRegistry>(),
            _activeKind,
            kind);

    public Task<bool> IsInitializedAsync() =>
        Task.FromResult(
            _agent != null
            || !string.IsNullOrWhiteSpace(_state.State.Identity?.Kind));

    public Task HandleEnvelopeAsync(byte[] envelopeBytes) =>
        HandleEnvelopeAsyncCore(envelopeBytes, propagateFailure: false);

    private async Task HandleEnvelopeAsyncCore(byte[] envelopeBytes, bool propagateFailure)
    {
        var envelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
        propagateFailure = propagateFailure || ShouldPropagateDirectDispatchFailure(envelope);

        if (!await EnsureAgentAvailableForEnvelopeAsync(envelope, propagateFailure))
            return;

        if (await TryHandleCompatibilityRetryAsync(envelope, propagateFailure))
            return;

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

        await HandleAgentEnvelopeAsync(envelope, propagateFailure);
    }

    private async Task<bool> EnsureAgentAvailableForEnvelopeAsync(
        EventEnvelope envelope,
        bool propagateFailure)
    {
        if (_agent != null)
            return true;

        // Only attempt resolution when OnActivateAsync has not already tried. Otherwise a
        // persistent missing registration would amplify into per-envelope registry I/O.
        if (!_identityResolutionAttempted)
            await ResumeFromPersistedIdentityAsync(CancellationToken.None);

        if (_agent != null)
            return true;

        if (!string.IsNullOrWhiteSpace(_state.State.Identity?.Kind))
        {
            _logger.LogWarning(
                "Runtime actor {ActorId} is unavailable; applying terminal failure policy to the envelope",
                this.GetPrimaryKeyString());
        }
        else
        {
            _logger.LogDebug(
                "Runtime actor {ActorId} has no agent identity; applying terminal failure policy to the envelope",
                this.GetPrimaryKeyString());
        }

        AgentMetrics.RecordEnvelopeTerminalFailure(
            AgentMetrics.FailureReasonActorUnavailable,
            ResolveTerminalFailureDisposition(propagateFailure));
        if (propagateFailure)
        {
            throw new InvalidOperationException(
                $"Runtime actor '{this.GetPrimaryKeyString()}' is unavailable for envelope '{envelope.Id}'.");
        }

        return false;
    }

    private async Task HandleAgentEnvelopeAsync(EventEnvelope envelope, bool propagateFailure)
    {
        using var scope = EventHandleScope.Begin(_logger, this.GetPrimaryKeyString(), envelope);
        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            await _agent!.HandleEventAsync(envelope);
        }
        catch (Exception ex)
        {
            scope.MarkError(ex);
            try
            {
                if (await TryScheduleRetryAsync(envelope, ex))
                    return;
            }
            catch
            {
                throw;
            }

            _logger.LogError(
                ex,
                "Runtime envelope handling failed after retry exhausted (or retry disabled) for actor {ActorId}, envelope {EnvelopeId}, event type '{EventTypeUrl}'.",
                this.GetPrimaryKeyString(),
                envelope.Id,
                envelope.Payload?.TypeUrl ?? "(none)");

            AgentMetrics.RecordEnvelopeTerminalFailure(
                AgentMetrics.FailureReasonHandlerRetryExhausted,
                ResolveTerminalFailureDisposition(propagateFailure));
            if (propagateFailure)
            {
                throw;
            }
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
        // Clearing state takes us back to "no identity configured"; let any
        // future envelope re-attempt resolution rather than treating it as
        // permanently failed.
        _identityResolutionAttempted = false;
        await _state.ClearStateAsync();
    }

    private async Task<AgentImplementation?> BindAgentByKindAsync(
        string kind,
        CancellationToken ct = default,
        bool throwOnFailure = false)
    {
        var registry = ServiceProvider?.GetService<IAgentKindRegistry>();
        if (registry == null)
        {
            _logger.LogError(
                "Cannot bind actor {ActorId} by kind '{Kind}': IAgentKindRegistry not registered.",
                SafeGetActorIdForLog(),
                kind);
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    $"Cannot resume actor '{SafeGetActorIdForLog()}' because IAgentKindRegistry is not registered.");
            }

            return null;
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
                SafeGetActorIdForLog());
            if (throwOnFailure)
                throw;

            return null;
        }

        if (!await BindAgentAsync(implementation, ct, throwOnFailure))
            return null;

        // Track the *canonical* kind from the registry, not the caller's
        // input. Aliases resolve to the same impl but should not surface as
        // separate identities once activation succeeds.
        _activeKind = implementation.Metadata.Kind;
        return implementation;
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

    private async Task<bool> BindAgentAsync(
        AgentImplementation implementation,
        CancellationToken ct,
        bool throwOnFailure)
    {
        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state);
            // Pass the grain's activation-time ServiceProvider so the agent's
            // constructor-injected scoped dependencies resolve in the grain's
            // own container, not the silo root.
            var agent = implementation.Factory(ServiceProvider)
                ?? throw new InvalidOperationException(
                    $"Agent factory for kind '{implementation.Metadata.Kind}' returned null.");
            InjectDependencies(agent, this.GetPrimaryKeyString());
            await agent.ActivateAsync(ct);
            _agent = agent;
            return true;
        }
        catch (Exception ex) when (IsCommittedStatePublicationActivationFailure(ex))
        {
            _logger.LogError(
                ex,
                "Committed-state publication recovery prevented activation of grain actor {ActorId} for kind '{Kind}' (impl '{ImplClr}').",
                SafeGetActorIdForLog(),
                implementation.Metadata.Kind,
                implementation.Metadata.ImplementationClrTypeName);
            throw;
        }
        catch (Exception ex) when (throwOnFailure)
        {
            _logger.LogError(
                ex,
                "Failed to resume persisted grain actor {ActorId} for kind '{Kind}' (impl '{ImplClr}').",
                SafeGetActorIdForLog(),
                implementation.Metadata.Kind,
                implementation.Metadata.ImplementationClrTypeName);
            throw;
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

    private static bool IsCommittedStatePublicationActivationFailure(Exception exception) =>
        exception is CommittedStatePublicationException
            or CommittedStatePublicationRecoveryException;

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
            if (DurableCallbackEnvelopeCredentialGuard.TryFindRuntimeCredential(retryEnvelope, out var credentialFieldPath))
            {
                // The durable callback store rejects runtime credentials
                // (RuntimeCallbackSchedulerGrain.ValidateScheduleRequest), and the
                // handler cannot re-resolve a stripped credential on redelivery.
                // Fail the delivery with the original handler exception instead of
                // the guard error so stream redelivery semantics stay intact.
                _logger.LogWarning(
                    ex,
                    "Durable runtime retry unavailable for actor {ActorId}, envelope {EnvelopeId}: envelope carries runtime credential field '{CredentialFieldPath}'.",
                    this.GetPrimaryKeyString(),
                    envelope.Id,
                    credentialFieldPath);
                ExceptionDispatchInfo.Capture(ex).Throw();
            }

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
        var originId = RuntimeEnvelopeDeliveryIdentity.ResolveDeliveryLineageId(envelope) ?? envelope.Id;

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

        AgentMetrics.RecordEnvelopeTerminalFailure(
            AgentMetrics.FailureReasonCompatibilityRetryExhausted,
            ResolveTerminalFailureDisposition(propagateFailure));
        if (propagateFailure)
            throw compatibilityException;

        return true;
    }

    private static bool ShouldPropagateDirectDispatchFailure(EventEnvelope envelope) =>
        envelope.Runtime?.Dispatch?.PropagateFailure == true;

    private static string ResolveTerminalFailureDisposition(bool propagateFailure) =>
        propagateFailure
            ? AgentMetrics.FailureDispositionPropagated
            : AgentMetrics.FailureDispositionReturned;
}
