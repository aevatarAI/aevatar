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
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Runtime.Actors;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Delivery;
using Aevatar.Foundation.Runtime.Observability;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;

[ImplicitStreamSubscription(OrleansRuntimeConstants.ActorEventStreamNamespace)]
[RuntimeFleetCapabilityManifest]
public sealed class RuntimeActorGrain : Grain, IRuntimeActorGrain
{
    private readonly IPersistentState<RuntimeActorGrainState> _state;
    private readonly IPersistentState<RuntimeActorCommittedStatePublicationGrainState>
        _committedStatePublication;
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
    private IRuntimeActorStateSchemaContextBinder? _stateSchemaContextBinder;
    private IRuntimeFleetReconcileDeliveryVerifier? _fleetReconcileVerifier;
    private IRuntimeFleetReconcileDeliveryAttestationBinder? _fleetReconcileAttestationBinder;
    private IActorDeactivationHookDispatcher? _deactivationHookDispatcher;
    private ILogger<RuntimeActorGrain> _logger = NullLogger<RuntimeActorGrain>.Instance;
    private IAsyncStream<EventEnvelope>? _selfStream;
    private StreamSubscriptionHandle<EventEnvelope>? _selfStreamHandle;
    private CompatibilityFailureInjectionPolicy _compatibilityFailureInjectionPolicy =
        CompatibilityFailureInjectionPolicy.Disabled;
    private RuntimeEnvelopeRetryPolicy _runtimeEnvelopeRetryPolicy =
        RuntimeEnvelopeRetryPolicy.Disabled;

    public RuntimeActorGrain(
        [PersistentState("agent", OrleansRuntimeConstants.RuntimeActorGrainStateStorageName)]
        IPersistentState<RuntimeActorGrainState> state,
        [PersistentState("committed-state-publication", OrleansRuntimeConstants.RuntimeActorGrainStateStorageName)]
        IPersistentState<RuntimeActorCommittedStatePublicationGrainState> committedStatePublication)
    {
        _state = state;
        _committedStatePublication = committedStatePublication;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _propagationPolicy = ServiceProvider.GetService<IEnvelopePropagationPolicy>() ?? _propagationPolicy;
        _streams = ServiceProvider.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
        _stateBindingAccessor = ServiceProvider.GetService<IRuntimeActorStateBindingAccessor>();
        _stateSchemaContextBinder = ServiceProvider.GetService<IRuntimeActorStateSchemaContextBinder>();
        _fleetReconcileVerifier = ServiceProvider.GetService<IRuntimeFleetReconcileDeliveryVerifier>();
        _fleetReconcileAttestationBinder = ServiceProvider.GetService<IRuntimeFleetReconcileDeliveryAttestationBinder>();
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

        await ResumeFromPersistedIdentityAsync(cancellationToken);
        if (_agent != null || _state.State.StorageRecovery != null)
            await SubscribeSelfStreamAsync();
    }

    /// <summary>
    /// Resolves the persisted primary kind identity into an
    /// <see cref="AgentImplementation"/> and binds it to the grain.
    /// </summary>
    private async Task ResumeFromPersistedIdentityAsync(CancellationToken ct)
    {
        _identityResolutionAttempted = true;

        if (_state.State.StorageRecovery != null)
            return;

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
        Exception? unsubscribeFailure = null;
        Exception? agentCleanupFailure = null;

        var selfStreamHandle = _selfStreamHandle;
        _selfStreamHandle = null;
        if (selfStreamHandle != null)
        {
            try
            {
                await selfStreamHandle.UnsubscribeAsync();
            }
            catch (Exception ex)
            {
                if (ShouldIgnoreSelfStreamUnsubscribeFailure(ex))
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to unsubscribe self stream for actor {ActorId} during deactivation.",
                        SafeGetActorIdForLog());
                }
                else
                {
                    unsubscribeFailure = ex;
                    _logger.LogError(
                        ex,
                        "Failed to unsubscribe self stream for actor {ActorId} during deactivation; agent cleanup will still run.",
                        SafeGetActorIdForLog());
                }
            }
        }

        var agent = _agent;
        if (agent != null)
        {
            try
            {
                using var stateBinding = _stateBindingAccessor?.Bind(_state, _committedStatePublication);
                using var schemaContext = BindStateSchemaContext();
                await agent.DeactivateAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                agentCleanupFailure = ex;
                _logger.LogError(
                    ex,
                    "Agent cleanup failed for actor {ActorId} during deactivation.",
                    SafeGetActorIdForLog());
            }
            finally
            {
                _agent = null;
                _activeKind = null;
            }
        }

        TriggerDeactivationHook();

        if (unsubscribeFailure != null && agentCleanupFailure != null)
        {
            throw new AggregateException(
                "Self-stream unsubscribe and agent cleanup both failed during runtime actor deactivation.",
                unsubscribeFailure,
                agentCleanupFailure);
        }

        if (unsubscribeFailure != null)
            ExceptionDispatchInfo.Capture(unsubscribeFailure).Throw();
        if (agentCleanupFailure != null)
            ExceptionDispatchInfo.Capture(agentCleanupFailure).Throw();
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
        {
            if (!KindResolvesToActiveImplementation(kind))
                return false;

            // Binding and stream subscription form one initialization boundary. A previous
            // attempt can bind the implementation and then fail while subscribing; retry must
            // finish that boundary instead of reporting a sticky false success.
            await SubscribeSelfStreamAsync();
            _identityResolutionAttempted = true;
            return true;
        }

        var implementation = await BindAgentByKindAsync(kind, establishIdentity: true);
        if (implementation == null)
            return false;
        await SubscribeSelfStreamAsync();
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
            _state.State.StorageRecovery == null &&
            (_agent != null || !string.IsNullOrWhiteSpace(_state.State.Identity?.Kind)));

    public Task HandleEnvelopeAsync(byte[] envelopeBytes) =>
        HandleEnvelopeAsyncCore(envelopeBytes, propagateFailure: false);

    private async Task HandleEnvelopeAsyncCore(byte[] envelopeBytes, bool propagateFailure)
    {
        var envelope = EventEnvelope.Parser.ParseFrom(envelopeBytes);
        propagateFailure = propagateFailure || ShouldPropagateDirectDispatchFailure(envelope);

        if (!await EnsureAgentAvailableForEnvelopeAsync(envelope, propagateFailure))
            return;

        await ThrowIfStateSchemaTurnoverAdmittedAsync();

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

        var storageRecovery = _state.State.StorageRecovery;
        if (storageRecovery != null)
        {
            _logger.LogError(
                "Runtime actor {ActorId} requires durable state recovery; envelope {EnvelopeId} remains unacknowledged until an authoritative Agent Kind re-establishes the actor. recoveryReason={RecoveryReason}",
                SafeGetActorIdForLog(),
                envelope.Id,
                storageRecovery.Reason);
            throw new RuntimeActorStateStorageRecoveryRequiredException(
                SafeGetActorIdForLog(),
                storageRecovery.Reason);
        }

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

            // A persisted identity exists but could not be re-bound (for
            // example the replay failed against a damaged stream). Resolution
            // is attempted at most once per activation, so a camped activation
            // would drop every future envelope for as long as it lives. Shed
            // the activation instead: each new envelope then re-activates the
            // grain and retries the bind, which is also the natural retry path
            // once the underlying store has been repaired.
            DeactivateOnIdle();
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

    private async Task ThrowIfStateSchemaTurnoverAdmittedAsync()
    {
        var identity = _state.State.Identity;
        if (identity == null || string.IsNullOrWhiteSpace(identity.Kind))
            return;

        var registry = ServiceProvider?.GetService<IAgentKindRegistry>();
        if (registry == null)
            return;

        var implementation = registry.Resolve(identity.Kind);
        if (identity.StateSchemaVersion >= implementation.Metadata.StateSchemaVersion)
            return;

        var decision = await RuntimeActorStateMigrationAdmission.EvaluateAsync(
            identity,
            _state.State.AgentStateTypeName,
            _state.State.AgentStateSnapshot,
            implementation,
            ServiceProvider?.GetService<IRuntimeFleetCapabilityAdmissionReader>() ??
                new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            ServiceProvider?.GetService<IRuntimeLocalMembershipIdentityReader>() ??
                new UnavailableRuntimeLocalMembershipIdentityReader(),
            ServiceProvider?.GetService<TimeProvider>(),
            ServiceProvider?.GetService<RuntimeActorStateMigrationAdmissionOptions>(),
            CancellationToken.None,
            ServiceProvider?.GetService<IRuntimeFleetCapabilityQuiescenceReader>());
        if (!decision.IsAdmitted)
            return;

        _logger.LogInformation(
            "Runtime actor is turning over an older active state schema before handling the envelope. actorId={ActorId} kind={Kind} persistedVersion={PersistedVersion} targetVersion={TargetVersion}",
            SafeGetActorIdForLog(),
            implementation.Metadata.Kind,
            identity.StateSchemaVersion,
            decision.StateSchemaVersion);
        DeactivateOnIdle();
        throw new RuntimeActorStateSchemaTurnoverRequiredException(
            SafeGetActorIdForLog(),
            implementation.Metadata.Kind,
            identity.StateSchemaVersion,
            decision.StateSchemaVersion);
    }

    private async Task HandleAgentEnvelopeAsync(EventEnvelope envelope, bool propagateFailure)
    {
        using var scope = EventHandleScope.Begin(_logger, this.GetPrimaryKeyString(), envelope);
        try
        {
            // Verify asynchronously, but bind the attestation synchronously in this frame:
            // an AsyncLocal assigned inside an awaited helper does not flow back to the
            // caller, so the actor handler would observe no attestation and fail closed.
            var reconcileAttestation = await VerifyFleetReconcileAttestationAsync(
                envelope);
            using var reconcileAttestationBinding = reconcileAttestation == null
                ? null
                : _fleetReconcileAttestationBinder!.Bind(reconcileAttestation);
            using var stateBinding = _stateBindingAccessor?.Bind(_state, _committedStatePublication);
            using var schemaContext = BindStateSchemaContext();
            await _agent!.HandleEventAsync(envelope);
            await CompleteHandledRetryCoalescingCursorAsync(envelope);
        }
        catch (Exception ex)
        {
            scope.MarkError(ex);
            var hasCommitConsistencyFailure =
                RuntimeEnvelopeRetryPolicy.ContainsCommitConsistencyFailure(ex);
            Exception? retrySchedulingFailure = null;
            try
            {
                if (await TryScheduleRetryAsync(envelope, ex))
                {
                    if (hasCommitConsistencyFailure)
                        ShedActivationAfterCommitConsistencyFailure();
                    return;
                }
            }
            catch (Exception scheduleException)
            {
                retrySchedulingFailure = scheduleException;
                _logger.LogError(
                    scheduleException,
                    "Runtime envelope retry scheduling failed for actor {ActorId}, envelope {EnvelopeId}; preserving the original handler failure.",
                    this.GetPrimaryKeyString(),
                    envelope.Id);
            }

            _logger.LogError(
                ex,
                "Runtime envelope handling failed after retry exhausted, retry disabled, or retry scheduling failed for actor {ActorId}, envelope {EnvelopeId}, event type '{EventTypeUrl}'.",
                this.GetPrimaryKeyString(),
                envelope.Id,
                envelope.Payload?.TypeUrl ?? "(none)");

            var requiresTransportRedelivery =
                RuntimeEnvelopeRetryPolicy.ContainsRuntimeEnvelopeRetryableFailure(ex);
            var shouldPropagateFailure =
                propagateFailure || requiresTransportRedelivery || retrySchedulingFailure != null;
            AgentMetrics.RecordEnvelopeTerminalFailure(
                AgentMetrics.FailureReasonHandlerRetryExhausted,
                ResolveTerminalFailureDisposition(shouldPropagateFailure));

            if (requiresTransportRedelivery)
            {
                // The actor either exhausted its own retry budget or could not
                // persist the durable wakeup. Keep the provider delivery
                // unacknowledged and shed the activation so redelivery rehydrates
                // committed state.
                // Preserve _agent until OnDeactivateAsync so actor-owned background
                // work is canceled through the normal lifecycle hook.
                _logger.LogWarning(
                    "Runtime actor {ActorId} requires transport redelivery after actor-owned retry was unavailable; shedding the activation without acknowledging the envelope.",
                    this.GetPrimaryKeyString());
                DeactivateOnIdle();
            }

            if (hasCommitConsistencyFailure)
                ShedActivationAfterCommitConsistencyFailure();

            if (shouldPropagateFailure)
            {
                throw;
            }
        }
    }

    private void ShedActivationAfterCommitConsistencyFailure()
    {
        // The activation's memory and the event store disagree about committed
        // history. Preserve the agent until OnDeactivateAsync so actor-owned
        // cleanup still runs, then force the retry to rehydrate committed state.
        _logger.LogWarning(
            "Runtime actor {ActorId} is shedding its activation after a commit-consistency failure; the next envelope will rehydrate from committed state.",
            this.GetPrimaryKeyString());
        DeactivateOnIdle();
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
            using var schemaContext = BindStateSchemaContext();
            await _agent.DeactivateAsync();
            _agent = null;
            _activeKind = null;
        }

        DeactivateOnIdle();
    }

    public async Task PurgeAsync()
    {
        var actorId = TryGetActorId() ?? _state.State?.AgentId;
        if (string.Equals(
                actorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime fleet capability authority is runtime-reserved and cannot be purged.");
        }

        if (_agent != null)
        {
            using var schemaContext = BindStateSchemaContext();
            await _agent.DeactivateAsync();
            _agent = null;
            _activeKind = null;
        }

        _committedStatePublication.State = new RuntimeActorCommittedStatePublicationGrainState();
        await _committedStatePublication.ClearStateAsync();
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
        bool throwOnFailure = false,
        bool establishIdentity = false)
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

        EnsureReservedFleetAuthorityIdentity(
            this.GetPrimaryKeyString(),
            implementation.Metadata.Kind);

        var originalRecordExists = _state.RecordExists;
        _state.State ??= new RuntimeActorGrainState();
        var originalState = CloneState(_state.State);
        var isStorageRecovery = originalState.StorageRecovery != null;
        var createdIdentity = false;
        if (_state.State.Identity == null)
        {
            if (!establishIdentity)
            {
                throw new InvalidOperationException(
                    $"Cannot bind actor '{SafeGetActorIdForLog()}' without a persisted runtime identity.");
            }

            _state.State.AgentId = this.GetPrimaryKeyString();
            _state.State.Identity = new RuntimeActorIdentity
            {
                Kind = implementation.Metadata.Kind,
                StateSchemaVersion = 0,
            };
            _state.State.StorageRecovery = null;
            createdIdentity = true;
        }

        try
        {
            // Admission runs before constructing or activating the agent. A
            // granted cutover writes snapshot, schema marker, and immutable
            // receipt in one state row. Without a fresh proof, a new actor is
            // durably established at the legacy schema-zero baseline.
            var migrated = await ApplyAdmittedMigrationAsync(implementation, createdIdentity, ct);
            if (createdIdentity && !migrated)
                await _state.WriteStateAsync(ct);

        }
        catch
        {
            if (createdIdentity)
            {
                _state.State = originalState;
                if (isStorageRecovery)
                    DeactivateOnIdle();
            }
            throw;
        }

        bool bound;
        try
        {
            bound = await BindAgentAsync(
                implementation,
                ct,
                throwOnFailure || isStorageRecovery);
        }
        catch when (isStorageRecovery)
        {
            // The authoritative kind is already durable. Shed this activation
            // so the next attempt re-reads that row and retries business-state
            // replay; never restore the unreadable source payload after the
            // identity write has succeeded.
            DeactivateOnIdle();
            throw;
        }

        if (!bound)
        {
            if (createdIdentity)
            {
                _state.State = originalState;
                if (originalRecordExists)
                    await _state.WriteStateAsync(ct);
                else
                    await _state.ClearStateAsync(ct);
            }
            return null;
        }

        if (isStorageRecovery)
        {
            _logger.LogWarning(
                "Runtime actor durable state recovery completed. actorId={ActorId} kind={Kind} recoveryReason={RecoveryReason}",
                SafeGetActorIdForLog(),
                implementation.Metadata.Kind,
                originalState.StorageRecovery!.Reason);
        }

        // Track the *canonical* kind from the registry, not the caller's
        // input. Aliases resolve to the same impl but should not surface as
        // separate identities once activation succeeds.
        _activeKind = implementation.Metadata.Kind;
        return implementation;
    }

    private async Task<bool> ApplyAdmittedMigrationAsync(
        AgentImplementation implementation,
        bool createdIdentity,
        CancellationToken ct)
    {
        try
        {
            return await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
                _state,
                implementation,
                ServiceProvider?.GetService<IRuntimeFleetCapabilityAdmissionReader>() ??
                    new DenyAllRuntimeFleetCapabilityAdmissionReader(),
                ServiceProvider?.GetService<IRuntimeLocalMembershipIdentityReader>() ??
                    new UnavailableRuntimeLocalMembershipIdentityReader(),
                ServiceProvider?.GetService<TimeProvider>(),
                ServiceProvider?.GetService<RuntimeActorStateMigrationAdmissionOptions>(),
                ct,
                ServiceProvider?.GetService<IRuntimeFleetCapabilityQuiescenceReader>());
        }
        catch (RuntimeActorStateMigrationPersistenceException exception)
        {
            // Migration write failure leaves the actor unavailable, never partially migrated:
            // the store may have committed the new schema before the acknowledgement was lost,
            // so neither the restored in-memory shape nor the target shape is known to match
            // the durable row. This activation is discarded without constructing, binding or
            // serving the agent (its inbox is not consumed); the next activation re-reads the
            // durable state and activates at whichever schema is actually persisted.
            _logger.LogError(
                exception,
                "Runtime actor state schema migration persistence failed or is unknown; the activation is discarded and the actor stays unavailable until durable state is re-read. actorId={ActorId} kind={Kind} persistedVersion={PersistedVersion} targetVersion={TargetVersion}",
                SafeGetActorIdForLog(),
                exception.AgentKind,
                exception.PersistedStateSchemaVersion,
                exception.TargetStateSchemaVersion);
            DeactivateOnIdle();
            throw;
        }
    }

    private static void EnsureReservedFleetAuthorityIdentity(
        string actorId,
        string agentKind)
    {
        var hasReservedId = string.Equals(
            actorId,
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StringComparison.Ordinal);
        var hasReservedKind = string.Equals(
            agentKind,
            RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
            StringComparison.Ordinal);
        if (hasReservedId != hasReservedKind)
        {
            throw new InvalidOperationException(
                $"Runtime fleet authority identity requires the exact actor id/kind pair " +
                $"'{RuntimeFleetCapabilityAuthorityIdentity.ActorId}' / " +
                $"'{RuntimeFleetCapabilityAuthorityIdentity.AgentKind}'.");
        }
    }

    private static RuntimeActorGrainState CloneState(RuntimeActorGrainState state)
    {
#pragma warning disable CS0612, CS0618
        return new RuntimeActorGrainState
        {
            AgentId = state.AgentId,
            AgentTypeName = state.AgentTypeName,
            ParentId = state.ParentId,
            Children = [.. state.Children],
            AgentStateTypeName = state.AgentStateTypeName,
            AgentStateSnapshot = state.AgentStateSnapshot?.ToArray(),
            AgentStateSnapshotVersion = state.AgentStateSnapshotVersion,
            Identity = state.Identity?.Clone(),
            CommittedStatePublicationState = state.CommittedStatePublicationState?.ToArray(),
            StorageRecovery = state.StorageRecovery?.Clone(),
        };
#pragma warning restore CS0612, CS0618
    }

    private string SafeGetActorIdForLog()
        => TryGetActorId() ?? "(uninitialized)";

    private string? TryGetActorId()
    {
        try
        {
            return this.GetPrimaryKeyString();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Runtime actor primary key lookup failed before actor identity was available.");
            return null;
        }
    }

    private async Task<bool> BindAgentAsync(
        AgentImplementation implementation,
        CancellationToken ct,
        bool throwOnFailure)
    {
        try
        {
            using var stateBinding = _stateBindingAccessor?.Bind(_state, _committedStatePublication);
            using var schemaContext = BindStateSchemaContext();
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

    private IDisposable? BindStateSchemaContext()
    {
        var identity = _state.State?.Identity;
        return identity == null ? null : _stateSchemaContextBinder?.Bind(identity);
    }

    private async Task<RuntimeFleetReconcileDeliveryAttestation?> VerifyFleetReconcileAttestationAsync(
        EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) != true)
            return null;
        if (!string.Equals(
                this.GetPrimaryKeyString(),
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            _fleetReconcileVerifier == null ||
            _fleetReconcileAttestationBinder == null)
        {
            throw new InvalidOperationException(
                "Runtime fleet reconcile callback reached an invalid runtime ingress.");
        }

        var attestation = await _fleetReconcileVerifier.VerifyAsync(
            envelope,
            CancellationToken.None);
        if (attestation == null)
        {
            throw new InvalidOperationException(
                "Runtime fleet reconcile callback delivery is not current in scheduler-owned state.");
        }

        return attestation;
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
        var retryUntilResolved =
            RuntimeEnvelopeRetryPolicy.ContainsRuntimeEnvelopeRetryUntilResolvedFailure(ex);
        var retryCoalescingCursor = retryUntilResolved
            ? RuntimeEnvelopeRetryPolicy.ResolveRetryCoalescingCursor(ex)
            : null;
        if (!_runtimeEnvelopeRetryPolicy.TryBuildRetryEnvelope(
                envelope,
                ex,
                out var retryEnvelope,
                out var nextAttempt))
            return false;

        if (retryUntilResolved || _runtimeEnvelopeRetryPolicy.RetryDelayMs > 0)
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
            var actorId = this.GetPrimaryKeyString();
            var callbackId = BuildRuntimeRetryCallbackId(
                envelope,
                nextAttempt,
                retryUntilResolved,
                retryCoalescingCursor);
            await scheduler.ScheduleTimeoutAsync(
                new RuntimeCallbackTimeoutRequest
                {
                    ActorId = actorId,
                    CallbackId = callbackId,
                    DueTime = TimeSpan.FromMilliseconds(
                        _runtimeEnvelopeRetryPolicy.ResolveRetryDelayMs(
                            nextAttempt,
                            retryUntilResolved,
                            RuntimeCallbackKeyComposer.BuildKey('|', actorId, callbackId))),
                    TriggerEnvelope = retryEnvelope,
                    DeliveryMode = RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
                    CoalescingCursor = retryCoalescingCursor,
                });
        }
        else
        {
            await _streams.GetStream(this.GetPrimaryKeyString()).ProduceAsync(retryEnvelope);
        }

        LogScheduledRuntimeEnvelopeRetry(ex, nextAttempt, retryUntilResolved);
        return true;
    }

    private void LogScheduledRuntimeEnvelopeRetry(
        Exception exception,
        int nextAttempt,
        bool retryUntilResolved)
    {
        var disposition = RuntimeEnvelopeRetryPolicy.ResolveRetryLogDisposition(nextAttempt);
        if (retryUntilResolved)
        {
            switch (disposition)
            {
                case RuntimeEnvelopeRetryLogDisposition.WarningWithException:
                    _logger.LogWarning(
                        exception,
                        "Runtime envelope durable retry-until-resolved scheduled for actor {ActorId}, attempt {Attempt}.",
                        this.GetPrimaryKeyString(),
                        nextAttempt);
                    break;
                case RuntimeEnvelopeRetryLogDisposition.Warning:
                    _logger.LogWarning(
                        "Runtime envelope durable retry-until-resolved remains pending for actor {ActorId}, attempt {Attempt}; exception={ExceptionType}.",
                        this.GetPrimaryKeyString(),
                        nextAttempt,
                        exception.GetType().Name);
                    break;
                case RuntimeEnvelopeRetryLogDisposition.Debug:
                    _logger.LogDebug(
                        "Runtime envelope durable retry-until-resolved remains pending for actor {ActorId}, attempt {Attempt}; exception={ExceptionType}.",
                        this.GetPrimaryKeyString(),
                        nextAttempt,
                        exception.GetType().Name);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(disposition),
                        disposition,
                        "Unknown runtime envelope retry log disposition.");
            }

            return;
        }

        switch (disposition)
        {
            case RuntimeEnvelopeRetryLogDisposition.WarningWithException:
                _logger.LogWarning(
                    exception,
                    "Runtime envelope retry scheduled for actor {ActorId}, attempt {Attempt}/{MaxAttempts}.",
                    this.GetPrimaryKeyString(),
                    nextAttempt,
                    _runtimeEnvelopeRetryPolicy.MaxAttempts);
                break;
            case RuntimeEnvelopeRetryLogDisposition.Warning:
                _logger.LogWarning(
                    "Runtime envelope retry scheduled for actor {ActorId}, attempt {Attempt}/{MaxAttempts}; exception={ExceptionType}.",
                    this.GetPrimaryKeyString(),
                    nextAttempt,
                    _runtimeEnvelopeRetryPolicy.MaxAttempts,
                    exception.GetType().Name);
                break;
            case RuntimeEnvelopeRetryLogDisposition.Debug:
                _logger.LogDebug(
                    "Runtime envelope retry scheduled for actor {ActorId}, attempt {Attempt}/{MaxAttempts}; exception={ExceptionType}.",
                    this.GetPrimaryKeyString(),
                    nextAttempt,
                    _runtimeEnvelopeRetryPolicy.MaxAttempts,
                    exception.GetType().Name);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(disposition),
                    disposition,
                    "Unknown runtime envelope retry log disposition.");
        }
    }

    private async Task CompleteHandledRetryCoalescingCursorAsync(EventEnvelope envelope)
    {
        if (_agent is not IRuntimeEnvelopeRetryCoalescingCompletionSource completionSource)
            return;

        var cursor = completionSource.ResolveHandledRetryCoalescingCursor(envelope);
        if (cursor == null)
            return;

        try
        {
            var scheduler = ServiceProvider.GetRequiredService<IActorRuntimeCallbackScheduler>();
            if (scheduler is not IRuntimeEnvelopeRetryCoalescingCallbackScheduler completionScheduler)
            {
                throw new InvalidOperationException(
                    $"Runtime callback scheduler '{scheduler.GetType().FullName}' does not support coalesced retry completion.");
            }

            await completionScheduler.CompleteRuntimeEnvelopeRetryAsync(
                this.GetPrimaryKeyString(),
                cursor,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new RuntimeEnvelopeRetryCoalescingCompletionException(
                this.GetPrimaryKeyString(),
                cursor,
                ex);
        }
    }

    private string BuildRuntimeRetryCallbackId(
        EventEnvelope envelope,
        int nextAttempt,
        bool retryUntilResolved,
        RuntimeEnvelopeRetryCoalescingCursor? retryCoalescingCursor)
    {
        if (retryCoalescingCursor != null)
        {
            return RuntimeEnvelopeRetryCoalescingCallbackSlot.BuildCallbackId(
                retryCoalescingCursor.Key);
        }

        var originId = RuntimeEnvelopeDeliveryIdentity.ResolveDeliveryLineageId(envelope) ?? envelope.Id;

        if (string.IsNullOrWhiteSpace(originId))
            originId = envelope.Id ?? Guid.NewGuid().ToString("N");

        if (retryUntilResolved)
        {
            return RuntimeCallbackKeyComposer.BuildCallbackId(
                "runtime-envelope-retry-until-resolved",
                originId);
        }

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

public sealed class RuntimeActorStateSchemaTurnoverRequiredException(
    string actorId,
    string agentKind,
    int persistedStateSchemaVersion,
    int targetStateSchemaVersion)
    : InvalidOperationException(
        $"Actor '{actorId}' of kind '{agentKind}' must turn over from state schema " +
        $"{persistedStateSchemaVersion} to {targetStateSchemaVersion} before handling the envelope."),
        IRuntimeEnvelopeRetryableException
{
    public string ActorId { get; } = actorId;

    public string AgentKind { get; } = agentKind;

    public int PersistedStateSchemaVersion { get; } = persistedStateSchemaVersion;

    public int TargetStateSchemaVersion { get; } = targetStateSchemaVersion;
}

public sealed class RuntimeActorStateStorageRecoveryRequiredException(
    string actorId,
    RuntimeActorStateStorageRecoveryReason recoveryReason)
    : InvalidOperationException(
        $"Actor '{actorId}' requires durable runtime state recovery ({recoveryReason}) before handling inbox delivery."),
      IRuntimeEnvelopeRetryableException
{
    public string ActorId { get; } = actorId;

    public RuntimeActorStateStorageRecoveryReason RecoveryReason { get; } = recoveryReason;
}

internal sealed class RuntimeEnvelopeRetryCoalescingCompletionException(
    string actorId,
    RuntimeEnvelopeRetryCoalescingCursor cursor,
    Exception innerException)
    : InvalidOperationException(
        $"Actor '{actorId}' handled authoritative source '{cursor.Key}' at sequence {cursor.Sequence}, but its durable retry completion could not be committed.",
        innerException),
      IRuntimeEnvelopeRetryCoalescingException
{
    public RuntimeEnvelopeRetryCoalescingCursor RetryCoalescingCursor { get; } = cursor;
}
