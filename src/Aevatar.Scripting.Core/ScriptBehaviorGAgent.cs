using System.Globalization;
using System.Runtime.ExceptionServices;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

[GAgent("scripting.behavior")]
public sealed class ScriptBehaviorGAgent : GAgentBase<ScriptBehaviorState>
{
    private const string RunOutcomeRetryCallbackPrefix = "script-run-terminal-retry";
    private const int RunOutcomeRetryInitialDelayMs = 250;
    private const int RunOutcomeRetryMaxDelayMs = 30_000;
    private const int RunOutcomeTerminalHistoryLimit = 64;

    // Refactor (iter149/cluster-1133): Old pattern: script run completion was inferred from domain fact/readmodel side effects.  New principle: script run completion is an actor-owned committed outcome event observed through the projection session channel.
    // Refactor (issue1289): persist only committed fact data; derived readmodel/native/graph payloads belong to projection materialization.
    // Refactor (iter76/cluster-076-scripting-domain-fact-derived-readmodel-payloads):
    //   Old pattern: ScriptDomainFactCommitted persisted derived readmodel/native_document/native_graph payloads inside the domain event
    //   New principle: domain event keeps only committed facts; projection materializer derives readmodel/native_document/(optional)native_graph from fact + state_root
    // Refactor (iter42/cluster-044-scripting-source-package-json-shadow):
    //   Old pattern: Scripting persists and republishes source_text as a compatibility shadow of ScriptPackageSpec; multi-file packages can be encoded as JSON text and reparsed from persisted source.
    //   New principle: ScriptPackageSpec is the sole internal source-package contract for commands/state/events/readmodels; source_text is only an external one-file adapter field at Host/Application boundary.
    private readonly IScriptBehaviorDispatcher _dispatcher;
    private readonly IScriptBehaviorRuntimeCapabilityFactory _capabilityFactory;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;
    private readonly TimeProvider _timeProvider;

    public ScriptBehaviorGAgent(
        IScriptBehaviorDispatcher dispatcher,
        IScriptBehaviorRuntimeCapabilityFactory capabilityFactory,
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec,
        TimeProvider? timeProvider = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _timeProvider = timeProvider ?? TimeProvider.System;
        InitializeId();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await DeliverPendingRunOutcomesAsync(ct);
    }

    [AllEventHandler(AllowSelfHandling = true)]
    public async Task HandleEnvelopeAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload == null)
            return;

        // The typed handler below is self-only. The all-event script dispatcher must not
        // reinterpret retry continuations as script input, including when they arrive from outside.
        if (envelope.Payload.Is(ScriptRunOutcomeNotificationRetryFiredEvent.Descriptor))
            return;

        if (envelope.Payload.Is(BindScriptBehaviorRequestedEvent.Descriptor))
        {
            await HandleBindRequestedAsync(envelope.Payload.Unpack<BindScriptBehaviorRequestedEvent>(), CancellationToken.None);
            return;
        }

        await DispatchBehaviorAsync(envelope, CancellationToken.None);
    }

    protected override ScriptBehaviorState TransitionState(ScriptBehaviorState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptBehaviorBoundEvent>(ApplyBound)
            .On<ScriptDomainFactCommitted>(ApplyCommittedFact)
            .On<ScriptRunOutcomeRecordedEvent>(ApplyOutcomeRecorded)
            .On<ScriptRunOutcomeNotificationRetryScheduledEvent>(ApplyOutcomeNotificationRetryScheduled)
            .On<ScriptRunOutcomeNotificationDispatchedEvent>(ApplyOutcomeNotificationDispatched)
            .On<ScriptRunOutcomeNotificationExpiredEvent>(ApplyOutcomeNotificationExpired)
            .OrCurrent();

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRunOutcomeRetryFiredAsync(
        ScriptRunOutcomeNotificationRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (!TryResolveRunOutcomeDelivery(State, retry.ScriptRunId, retry.DeliveryId, out var delivery) ||
            delivery == null)
        {
            return;
        }

        var matchesScheduledAttempt =
            delivery.Status == ScriptRunOutcomeDeliveryStatus.RetryScheduled &&
            retry.Attempt == delivery.Attempt;
        var matchesScheduledNextAttemptRecovery =
            delivery.Status == ScriptRunOutcomeDeliveryStatus.RetryScheduled &&
            retry.Attempt == delivery.Attempt + 1;
        var matchesScheduleBeforeCommitRecovery =
            delivery.Status == ScriptRunOutcomeDeliveryStatus.Prepared &&
            retry.Attempt == delivery.Attempt + 1;
        if (!matchesScheduledAttempt &&
            !matchesScheduledNextAttemptRecovery &&
            !matchesScheduleBeforeCommitRecovery)
        {
            return;
        }

        if (matchesScheduledNextAttemptRecovery || matchesScheduleBeforeCommitRecovery)
        {
            await PersistDomainEventAsync(new ScriptRunOutcomeNotificationRetryScheduledEvent
            {
                ScriptRunId = retry.ScriptRunId,
                DeliveryId = retry.DeliveryId,
                Attempt = retry.Attempt,
                RetryCallbackId = BuildRunOutcomeRetryCallbackId(retry.ScriptRunId, retry.DeliveryId),
                RetryAtUnixTimeMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            });
            delivery = State.RunOutcomes[retry.ScriptRunId].Clone();
        }

        await DeliverRunOutcomeAsync(
            retry.ScriptRunId,
            delivery,
            CancellationToken.None,
            retry.Attempt);
    }

    private async Task HandleBindRequestedAsync(
        BindScriptBehaviorRequestedEvent evt,
        CancellationToken ct)
    {
        ValidateBinding(evt);
        if (!string.IsNullOrWhiteSpace(State.ScopeId) &&
            !string.IsNullOrWhiteSpace(evt.ScopeId) &&
            !string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script behavior actor `{Id}` is already bound to scope `{State.ScopeId}` and cannot switch to `{evt.ScopeId}`.");
        }

        if (IsSameBinding(evt))
            return;

        await PersistDomainEventAsync(new ScriptBehaviorBoundEvent
        {
            DefinitionActorId = evt.DefinitionActorId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            Revision = evt.Revision ?? string.Empty,
            SourceHash = evt.SourceHash ?? string.Empty,
            StateTypeUrl = evt.StateTypeUrl ?? string.Empty,
            ReadModelTypeUrl = evt.ReadModelTypeUrl ?? string.Empty,
            ReadModelSchemaVersion = evt.ReadModelSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = evt.ReadModelSchemaHash ?? string.Empty,
            ScriptPackage = evt.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
            ProtocolDescriptorSet = evt.ProtocolDescriptorSet,
            StateDescriptorFullName = evt.StateDescriptorFullName ?? string.Empty,
            ReadModelDescriptorFullName = evt.ReadModelDescriptorFullName ?? string.Empty,
            RuntimeSemantics = evt.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
            ScopeId = evt.ScopeId ?? string.Empty,
        }, ct);
    }

    private async Task DispatchBehaviorAsync(
        EventEnvelope envelope,
        CancellationToken ct)
    {
        // Refactor (iter149/cluster-1133): Old pattern: dispatch call sites inferred script run outcome from returned facts or readmodel visibility.  New principle: the actor commits a typed run outcome event for projection-session observation.
        EnsureBound();

        var run = ResolveRunRequest(envelope);
        if (await ReplayRecordedRunOutcomeAsync(run, envelope, ct))
            return;

        try
        {
            ValidateRunTarget(run);
        }
        catch (Exception ex) when (run != null && ex is InvalidOperationException)
        {
            var failedScopeId = ResolveScopeId(envelope, State.ScopeId);
            var outcome =
                BuildOutcomeRecordedEvent(
                    run,
                    ResolveRunId(envelope),
                    ResolveCommandId(envelope),
                    ResolveCorrelationId(envelope),
                    failedScopeId,
                    ScriptRunOutcomeStatus.Failed,
                    ex.Message,
                    null,
                    0,
                    State.LastAppliedEventVersion + 1);
            await PersistDomainEventAsync(
                outcome,
                ct);
            await DeliverRunOutcomeByIdAsync(outcome.ScriptRunId, ct);
            throw;
        }

        var scopeId = ResolveScopeId(envelope, State.ScopeId);
        var runId = ResolveRunId(envelope);
        var commandId = ResolveCommandId(envelope);
        var correlationId = ResolveCorrelationId(envelope);
        var capabilities = _capabilityFactory.Create(
            new ScriptBehaviorRuntimeCapabilityContext(
                ActorId: Id,
                ScriptId: State.ScriptId ?? string.Empty,
                Revision: State.Revision ?? string.Empty,
                DefinitionActorId: State.DefinitionActorId ?? string.Empty,
                ScopeId: scopeId,
                RunId: runId,
                CorrelationId: correlationId),
            publishAsync: (message, audience, token) => PublishAsync(message, audience, token),
            sendToAsync: (targetActorId, message, token) => SendToAsync(targetActorId, message, token),
            publishToSelfAsync: (message, token) => PublishAsync(message, TopologyAudience.Self, token),
            scheduleSelfSignalAsync: (callbackId, dueTime, message, token) =>
                ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, message, ct: token),
            cancelCallbackAsync: CancelDurableCallbackAsync);

        IReadOnlyList<ScriptDomainFactCommitted> facts;
        try
        {
            facts = await _dispatcher.DispatchAsync(
                new ScriptBehaviorDispatchRequest(
                    ActorId: Id,
                    DefinitionActorId: State.DefinitionActorId ?? string.Empty,
                    ScriptId: State.ScriptId ?? string.Empty,
                    Revision: State.Revision ?? string.Empty,
                    ScopeId: scopeId,
                    SourceHash: State.SourceHash ?? string.Empty,
                    ScriptPackage: RequireBoundPackage(State.ScriptPackage),
                    StateTypeUrl: State.StateTypeUrl ?? string.Empty,
                    ReadModelTypeUrl: State.ReadModelTypeUrl ?? string.Empty,
                    CurrentStateRoot: State.StateRoot?.Clone(),
                    CurrentStateVersion: State.LastAppliedEventVersion,
                    Envelope: envelope,
                    Capabilities: capabilities),
                ct);
        }
        catch (Exception ex) when (run != null && ex is not OperationCanceledException)
        {
            var outcome =
                BuildOutcomeRecordedEvent(
                    run,
                    runId,
                    commandId,
                    correlationId,
                    scopeId,
                    ScriptRunOutcomeStatus.Failed,
                    ex.Message,
                    null,
                    0,
                    State.LastAppliedEventVersion + 1);
            await PersistDomainEventAsync(
                outcome,
                ct);
            await DeliverRunOutcomeByIdAsync(outcome.ScriptRunId, ct);
            throw;
        }

        if (run == null)
        {
            if (facts.Count > 0)
                await PersistDomainEventsAsync(facts, ct);
            return;
        }

        var result = facts.Count == 0
            ? null
            : facts[^1].DomainEventPayload?.Clone();
        var currentVersion = State.LastAppliedEventVersion;
        var outcomeStateVersion = currentVersion + facts.Count + 1;
        var outcomeEvent =
            BuildOutcomeRecordedEvent(
                run,
                runId,
                commandId,
                correlationId,
                scopeId,
                ScriptRunOutcomeStatus.Succeeded,
                string.Empty,
                result,
                facts.Count,
                outcomeStateVersion);
        await PersistDomainEventsAsync(facts.Concat<IMessage>([outcomeEvent]).ToList(), ct);
        await DeliverRunOutcomeByIdAsync(outcomeEvent.ScriptRunId, ct);
    }

    private void ValidateRunTarget(RunScriptRequestedEvent? run)
    {
        if (run == null)
            return;

        if (!string.IsNullOrWhiteSpace(run.DefinitionActorId) &&
            !string.Equals(run.DefinitionActorId, State.DefinitionActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime actor `{Id}` is bound to definition `{State.DefinitionActorId}`, but run targeted `{run.DefinitionActorId}`.");
        }

        if (!string.IsNullOrWhiteSpace(run.ScriptRevision) &&
            !string.Equals(run.ScriptRevision, State.Revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime actor `{Id}` is bound to revision `{State.Revision}`, but run targeted `{run.ScriptRevision}`.");
        }

        if (!string.IsNullOrWhiteSpace(State.ScopeId) &&
            !string.IsNullOrWhiteSpace(run.ScopeId) &&
            !string.Equals(run.ScopeId, State.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Runtime actor `{Id}` is bound to scope `{State.ScopeId}`, but run targeted `{run.ScopeId}`.");
        }
    }

    private static ScriptBehaviorState ApplyBound(
        ScriptBehaviorState state,
        ScriptBehaviorBoundEvent evt)
    {
        var next = state.Clone();
        next.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.Revision = evt.Revision ?? string.Empty;
        next.SourceHash = evt.SourceHash ?? string.Empty;
        next.StateTypeUrl = evt.StateTypeUrl ?? string.Empty;
        next.ReadModelTypeUrl = evt.ReadModelTypeUrl ?? string.Empty;
        next.ReadModelSchemaVersion = evt.ReadModelSchemaVersion ?? string.Empty;
        next.ReadModelSchemaHash = evt.ReadModelSchemaHash ?? string.Empty;
        next.ScriptPackage = evt.ScriptPackage?.Clone() ?? new ScriptPackageSpec();
        next.ProtocolDescriptorSet = evt.ProtocolDescriptorSet;
        next.StateDescriptorFullName = evt.StateDescriptorFullName ?? string.Empty;
        next.ReadModelDescriptorFullName = evt.ReadModelDescriptorFullName ?? string.Empty;
        next.RuntimeSemantics = evt.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
        next.ScopeId = string.IsNullOrWhiteSpace(evt.ScopeId) ? state.ScopeId : evt.ScopeId;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(evt.Revision ?? string.Empty, ":binding");
        return next;
    }

    private ScriptBehaviorState ApplyCommittedFact(
        ScriptBehaviorState state,
        ScriptDomainFactCommitted evt)
    {
        var next = state.Clone();
        var payload = evt.DomainEventPayload?.Clone() ?? Any.Pack(new Empty());
        var scriptPackage = RequireBoundPackage(state.ScriptPackage);
        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            string.IsNullOrWhiteSpace(evt.ScriptId) ? state.ScriptId ?? string.Empty : evt.ScriptId,
            string.IsNullOrWhiteSpace(evt.Revision) ? state.Revision ?? string.Empty : evt.Revision,
            scriptPackage,
            state.SourceHash ?? string.Empty));
        var behavior = artifact.CreateBehavior();
        try
        {
            var eventTypeUrl = payload.TypeUrl ?? string.Empty;
            if (!artifact.Descriptor.DomainEvents.TryGetValue(eventTypeUrl, out var domainEventRegistration))
            {
                throw new InvalidOperationException(
                    $"Script behavior actor `{Id}` cannot apply undeclared domain event type `{eventTypeUrl}`.");
            }

            var currentState = _codec.Unpack(state.StateRoot, artifact.Descriptor.StateClrType);
            var domainEvent = _codec.Unpack(payload, domainEventRegistration.MessageClrType)
                ?? throw new InvalidOperationException($"Failed to unpack domain event payload `{eventTypeUrl}`.");
            var factContext = new Aevatar.Scripting.Abstractions.Behaviors.ScriptFactContext(
                evt.ActorId ?? Id,
                evt.DefinitionActorId ?? state.DefinitionActorId ?? string.Empty,
                string.IsNullOrWhiteSpace(evt.ScriptId) ? state.ScriptId ?? string.Empty : evt.ScriptId,
                string.IsNullOrWhiteSpace(evt.Revision) ? state.Revision ?? string.Empty : evt.Revision,
                evt.RunId ?? string.Empty,
                evt.CommandId ?? string.Empty,
                evt.CorrelationId ?? string.Empty,
                evt.EventSequence,
                evt.StateVersion,
                evt.EventType ?? eventTypeUrl,
                evt.OccurredAtUnixTimeMs);
            var appliedState = behavior.ApplyDomainEvent(
                currentState,
                domainEvent,
                factContext);
            next.StateRoot = _codec.Pack(appliedState)?.Clone();
        }
        finally
        {
            if (behavior is IDisposable disposable)
                disposable.Dispose();
        }

        next.LastRunId = evt.RunId ?? string.Empty;
        next.LastAppliedEventVersion = evt.StateVersion;
        next.LastEventId = string.IsNullOrWhiteSpace(evt.EventType)
            ? payload.TypeUrl ?? string.Empty
            : evt.EventType;
        next.StateTypeUrl = string.IsNullOrWhiteSpace(evt.StateTypeUrl)
            ? next.StateTypeUrl
            : evt.StateTypeUrl;
        next.ReadModelTypeUrl = string.IsNullOrWhiteSpace(evt.ReadModelTypeUrl)
            ? next.ReadModelTypeUrl
            : evt.ReadModelTypeUrl;
        if (string.IsNullOrWhiteSpace(next.ScopeId) && !string.IsNullOrWhiteSpace(evt.ScopeId))
            next.ScopeId = evt.ScopeId;
        return next;
    }

    private ScriptRunOutcomeRecordedEvent BuildOutcomeRecordedEvent(
        RunScriptRequestedEvent run,
        string runId,
        string commandId,
        string correlationId,
        string scopeId,
        ScriptRunOutcomeStatus status,
        string error,
        Any? result,
        int committedFactCount,
        long stateVersion)
    {
        // Refactor (iter149/cluster-1133): Old pattern: script outcome details had no typed committed event owned by the run actor.  New principle: outcome status, error, and result are explicit proto fields.
        return new ScriptRunOutcomeRecordedEvent
        {
            ScriptRunId = runId ?? string.Empty,
            Status = status,
            Error = error ?? string.Empty,
            Result = result?.Clone(),
            ActorId = Id,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            ScriptId = State.ScriptId ?? string.Empty,
            ScriptRevision = State.Revision ?? string.Empty,
            CommandId = commandId ?? string.Empty,
            CorrelationId = correlationId ?? string.Empty,
            ScopeId = scopeId ?? string.Empty,
            CommittedFactCount = committedFactCount,
            StateVersion = stateVersion,
            OccurredAtUnixTimeMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            CompletionNotificationActorId = run.CompletionNotificationActorId ?? string.Empty,
            DeliveryId = ResolveRunOutcomeDeliveryId(
                run,
                runId ?? string.Empty,
                commandId ?? string.Empty),
            ExpiresAtUnixTimeMs = run.CompletionNotificationExpiresAtUnixMs,
        };
    }

    private static ScriptBehaviorState ApplyOutcomeRecorded(
        ScriptBehaviorState state,
        ScriptRunOutcomeRecordedEvent evt)
    {
        var next = state.Clone();
        next.LastRunId = evt.ScriptRunId ?? string.Empty;
        next.LastAppliedEventVersion = Math.Max(state.LastAppliedEventVersion + 1, evt.StateVersion);
        if (string.IsNullOrWhiteSpace(next.ScopeId) && !string.IsNullOrWhiteSpace(evt.ScopeId))
            next.ScopeId = evt.ScopeId;
        if (string.IsNullOrWhiteSpace(evt.ScriptRunId) || next.RunOutcomes.ContainsKey(evt.ScriptRunId))
            return next;

        var deliveryId = ResolveRunOutcomeDeliveryId(evt);
        next.RunOutcomes[evt.ScriptRunId] = new ScriptRunOutcomeDeliveryState
        {
            Outcome = evt.Clone(),
            DeliveryId = deliveryId,
            ExpiresAtUnixTimeMs = evt.ExpiresAtUnixTimeMs,
            Status = string.IsNullOrWhiteSpace(evt.CompletionNotificationActorId)
                ? ScriptRunOutcomeDeliveryStatus.Dispatched
                : ScriptRunOutcomeDeliveryStatus.Prepared,
        };
        if (string.IsNullOrWhiteSpace(evt.CompletionNotificationActorId))
            PruneTerminalRunOutcomes(next);
        return next;
    }

    private static ScriptBehaviorState ApplyOutcomeNotificationRetryScheduled(
        ScriptBehaviorState state,
        ScriptRunOutcomeNotificationRetryScheduledEvent evt)
    {
        if (!TryResolveRunOutcomeDelivery(state, evt.ScriptRunId, evt.DeliveryId, out var delivery) ||
            delivery == null ||
            !IsPendingRunOutcomeDelivery(delivery) ||
            evt.Attempt != delivery.Attempt + 1)
        {
            return state;
        }

        var next = AdvanceDeliveryState(
            state,
            $"{evt.ScriptRunId}:outcome-notification:retry-scheduled:{evt.Attempt}");
        delivery = next.RunOutcomes[evt.ScriptRunId];
        delivery.Status = ScriptRunOutcomeDeliveryStatus.RetryScheduled;
        delivery.Attempt = evt.Attempt;
        delivery.RetryCallbackId = evt.RetryCallbackId ?? string.Empty;
        delivery.RetryAtUnixTimeMs = evt.RetryAtUnixTimeMs;
        return next;
    }

    private static ScriptBehaviorState ApplyOutcomeNotificationDispatched(
        ScriptBehaviorState state,
        ScriptRunOutcomeNotificationDispatchedEvent evt)
    {
        if (!TryResolveRunOutcomeDelivery(state, evt.ScriptRunId, evt.DeliveryId, out var delivery) ||
            delivery == null ||
            !IsPendingRunOutcomeDelivery(delivery) ||
            evt.Attempt != delivery.Attempt)
        {
            return state;
        }

        var next = AdvanceDeliveryState(state, $"{evt.ScriptRunId}:outcome-notification:dispatched");
        delivery = next.RunOutcomes[evt.ScriptRunId];
        delivery.Status = ScriptRunOutcomeDeliveryStatus.Dispatched;
        delivery.RetryCallbackId = string.Empty;
        delivery.RetryAtUnixTimeMs = 0;
        PruneTerminalRunOutcomes(next);
        return next;
    }

    private static ScriptBehaviorState ApplyOutcomeNotificationExpired(
        ScriptBehaviorState state,
        ScriptRunOutcomeNotificationExpiredEvent evt)
    {
        if (!TryResolveRunOutcomeDelivery(state, evt.ScriptRunId, evt.DeliveryId, out var delivery) ||
            delivery == null ||
            !IsPendingRunOutcomeDelivery(delivery) ||
            evt.Attempt != delivery.Attempt)
        {
            return state;
        }

        var next = AdvanceDeliveryState(state, $"{evt.ScriptRunId}:outcome-notification:expired");
        delivery = next.RunOutcomes[evt.ScriptRunId];
        delivery.Status = ScriptRunOutcomeDeliveryStatus.Expired;
        delivery.RetryCallbackId = string.Empty;
        delivery.RetryAtUnixTimeMs = 0;
        PruneTerminalRunOutcomes(next);
        return next;
    }

    private async Task<bool> ReplayRecordedRunOutcomeAsync(
        RunScriptRequestedEvent? run,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        var runId = ResolveRunId(envelope);
        if (run == null ||
            !State.RunOutcomes.TryGetValue(runId, out var delivery) ||
            delivery?.Outcome == null)
        {
            return false;
        }

        var recorded = delivery.Outcome;
        if (!string.Equals(recorded.CommandId, ResolveCommandId(envelope), StringComparison.Ordinal) ||
            !string.Equals(recorded.CorrelationId, ResolveCorrelationId(envelope), StringComparison.Ordinal) ||
            !string.Equals(
                recorded.CompletionNotificationActorId,
                run.CompletionNotificationActorId,
                StringComparison.Ordinal) ||
            !string.Equals(
                delivery.DeliveryId,
                ResolveRunOutcomeDeliveryId(run, runId, ResolveCommandId(envelope)),
                StringComparison.Ordinal) ||
            delivery.ExpiresAtUnixTimeMs != run.CompletionNotificationExpiresAtUnixMs)
        {
            throw new InvalidOperationException(
                $"Script run '{recorded.ScriptRunId}' already completed with a different execution identity.");
        }

        await DeliverRunOutcomeAsync(runId, delivery.Clone(), ct);
        return true;
    }

    private async Task DeliverPendingRunOutcomesAsync(CancellationToken ct)
    {
        var pending = State.RunOutcomes
            .Where(static entry =>
                IsPendingRunOutcomeDelivery(entry.Value) &&
                !string.IsNullOrWhiteSpace(entry.Value.Outcome?.CompletionNotificationActorId))
            .OrderBy(static entry => entry.Value.Outcome?.StateVersion ?? long.MaxValue)
            .ThenBy(static entry => entry.Value.Outcome?.OccurredAtUnixTimeMs ?? long.MaxValue)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => (entry.Key, Delivery: entry.Value.Clone()))
            .ToList();
        Exception? firstFailure = null;
        foreach (var (runId, delivery) in pending)
        {
            try
            {
                await DeliverRunOutcomeAsync(runId, delivery, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure != null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }

    private Task DeliverRunOutcomeByIdAsync(string runId, CancellationToken ct)
    {
        return State.RunOutcomes.TryGetValue(runId, out var delivery)
            ? DeliverRunOutcomeAsync(runId, delivery.Clone(), ct)
            : Task.CompletedTask;
    }

    private async Task DeliverRunOutcomeAsync(
        string runId,
        ScriptRunOutcomeDeliveryState requestedDelivery,
        CancellationToken ct,
        int? deliveryAttempt = null)
    {
        if (!TryResolveRunOutcomeDelivery(
                State,
                runId,
                requestedDelivery.DeliveryId,
                out var currentDelivery) ||
            currentDelivery == null ||
            !IsPendingRunOutcomeDelivery(currentDelivery))
        {
            return;
        }

        var delivery = currentDelivery.Clone();
        var outcome = delivery.Outcome?.Clone();
        if (outcome == null || string.IsNullOrWhiteSpace(outcome.CompletionNotificationActorId))
            return;

        var attempt = Math.Max(delivery.Attempt, deliveryAttempt ?? 0);
        var now = _timeProvider.GetUtcNow();
        if (HasElapsedRunOutcomeDeadline(delivery, now))
        {
            await PersistDomainEventAsync(new ScriptRunOutcomeNotificationExpiredEvent
            {
                ScriptRunId = runId,
                DeliveryId = delivery.DeliveryId,
                Attempt = attempt,
                ExpiredAtUnixTimeMs = now.ToUnixTimeMilliseconds(),
            }, ct);
            return;
        }

        try
        {
            await SendToAsync(
                outcome.CompletionNotificationActorId.Trim(),
                outcome,
                ct,
                new EventEnvelopePublishOptions
                {
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        OperationId = $"script-run-terminal:{delivery.DeliveryId}",
                    },
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleRunOutcomeRetryAsync(runId, delivery, attempt, ct);
            return;
        }

        try
        {
            await PersistDomainEventAsync(new ScriptRunOutcomeNotificationDispatchedEvent
            {
                ScriptRunId = runId,
                CommandId = outcome.CommandId,
                CompletionNotificationActorId = outcome.CompletionNotificationActorId,
                DispatchedAtUnixTimeMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                DeliveryId = delivery.DeliveryId,
                Attempt = attempt,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleRunOutcomeRetryAsync(runId, delivery, attempt, ct);
            throw;
        }
    }

    private async Task ScheduleRunOutcomeRetryAsync(
        string runId,
        ScriptRunOutcomeDeliveryState failedDelivery,
        int failedAttempt,
        CancellationToken ct)
    {
        if (!TryResolveRunOutcomeDelivery(
                State,
                runId,
                failedDelivery.DeliveryId,
                out var currentDelivery) ||
            currentDelivery == null ||
            !IsPendingRunOutcomeDelivery(currentDelivery))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (HasElapsedRunOutcomeDeadline(currentDelivery, now))
        {
            await PersistDomainEventAsync(new ScriptRunOutcomeNotificationExpiredEvent
            {
                ScriptRunId = runId,
                DeliveryId = currentDelivery.DeliveryId,
                Attempt = currentDelivery.Attempt,
                ExpiredAtUnixTimeMs = now.ToUnixTimeMilliseconds(),
            }, ct);
            return;
        }

        var attempt = Math.Max(currentDelivery.Attempt, failedAttempt) + 1;
        var retryDelayMs = CalculateRunOutcomeRetryDelayMs(attempt);
        var dueTimeMs = currentDelivery.ExpiresAtUnixTimeMs > 0
            ? Math.Min(
                retryDelayMs,
                currentDelivery.ExpiresAtUnixTimeMs - now.ToUnixTimeMilliseconds())
            : retryDelayMs;
        var dueTime = TimeSpan.FromMilliseconds(Math.Max(1, dueTimeMs));
        var retryAt = now.Add(dueTime);
        var callbackId = BuildRunOutcomeRetryCallbackId(runId, currentDelivery.DeliveryId);
        var retryFired = new ScriptRunOutcomeNotificationRetryFiredEvent
        {
            ScriptRunId = runId,
            DeliveryId = currentDelivery.DeliveryId,
            Attempt = attempt,
        };
        var retryOptions = BuildRunOutcomeRetryOptions(callbackId, attempt);
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                dueTime,
                retryFired,
                retryOptions,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var canPublishImmediateRecovery =
                currentDelivery.Status == ScriptRunOutcomeDeliveryStatus.Prepared &&
                currentDelivery.Attempt == 0 &&
                attempt == 1;
            Logger.LogWarning(
                ex,
                canPublishImmediateRecovery
                    ? "Script run outcome durable retry scheduling failed; publishing one immediate recovery continuation. actor={ActorId} run={RunId} delivery={DeliveryId} attempt={Attempt}"
                    : "Script run outcome durable retry scheduling failed; preserving the outbox for activation recovery. actor={ActorId} run={RunId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                runId,
                currentDelivery.DeliveryId,
                attempt);
            if (canPublishImmediateRecovery)
                await PublishAsync(retryFired, TopologyAudience.Self, ct, options: retryOptions);
            throw;
        }

        await PersistDomainEventAsync(new ScriptRunOutcomeNotificationRetryScheduledEvent
        {
            ScriptRunId = runId,
            DeliveryId = currentDelivery.DeliveryId,
            Attempt = attempt,
            RetryCallbackId = callbackId,
            RetryAtUnixTimeMs = retryAt.ToUnixTimeMilliseconds(),
        }, ct);
    }

    private static ScriptBehaviorState AdvanceDeliveryState(
        ScriptBehaviorState state,
        string eventId)
    {
        var next = state.Clone();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = eventId;
        return next;
    }

    private static bool TryResolveRunOutcomeDelivery(
        ScriptBehaviorState state,
        string runId,
        string deliveryId,
        out ScriptRunOutcomeDeliveryState? delivery)
    {
        delivery = null;
        if (string.IsNullOrWhiteSpace(runId) ||
            string.IsNullOrWhiteSpace(deliveryId) ||
            !state.RunOutcomes.TryGetValue(runId, out var candidate) ||
            candidate?.Outcome == null ||
            !string.Equals(candidate.Outcome.ScriptRunId, runId, StringComparison.Ordinal) ||
            !string.Equals(candidate.DeliveryId, deliveryId, StringComparison.Ordinal))
        {
            return false;
        }

        delivery = candidate;
        return true;
    }

    private static bool IsPendingRunOutcomeDelivery(ScriptRunOutcomeDeliveryState delivery) =>
        delivery.Status is ScriptRunOutcomeDeliveryStatus.Prepared or
            ScriptRunOutcomeDeliveryStatus.RetryScheduled;

    private static bool IsTerminalRunOutcomeDelivery(ScriptRunOutcomeDeliveryState delivery) =>
        delivery.Status is ScriptRunOutcomeDeliveryStatus.Dispatched or
            ScriptRunOutcomeDeliveryStatus.Expired;

    private static void PruneTerminalRunOutcomes(ScriptBehaviorState state)
    {
        var terminal = state.RunOutcomes
            .Where(static entry => IsTerminalRunOutcomeDelivery(entry.Value))
            .OrderBy(static entry => entry.Value.Outcome?.StateVersion ?? long.MaxValue)
            .ThenBy(static entry => entry.Value.Outcome?.OccurredAtUnixTimeMs ?? long.MaxValue)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => entry.Key)
            .ToList();
        foreach (var runId in terminal.Take(Math.Max(0, terminal.Count - RunOutcomeTerminalHistoryLimit)))
            state.RunOutcomes.Remove(runId);
    }

    private static string BuildRunOutcomeRetryCallbackId(string runId, string deliveryId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(RunOutcomeRetryCallbackPrefix, runId, deliveryId);

    private static EventEnvelopePublishOptions BuildRunOutcomeRetryOptions(
        string callbackId,
        int attempt) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    callbackId,
                    attempt.ToString(CultureInfo.InvariantCulture)),
            },
        };

    private static long CalculateRunOutcomeRetryDelayMs(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 7);
        var delayMs = RunOutcomeRetryInitialDelayMs * (1L << exponent);
        return Math.Min(delayMs, RunOutcomeRetryMaxDelayMs);
    }

    private static bool HasElapsedRunOutcomeDeadline(
        ScriptRunOutcomeDeliveryState delivery,
        DateTimeOffset now) =>
        delivery.ExpiresAtUnixTimeMs > 0 &&
        delivery.ExpiresAtUnixTimeMs <= now.ToUnixTimeMilliseconds();

    private static string ResolveRunOutcomeDeliveryId(
        RunScriptRequestedEvent run,
        string runId,
        string commandId) =>
        !string.IsNullOrWhiteSpace(run.CompletionNotificationDeliveryId)
            ? run.CompletionNotificationDeliveryId.Trim()
            : $"{runId}:{commandId}";

    private static string ResolveRunOutcomeDeliveryId(ScriptRunOutcomeRecordedEvent outcome) =>
        !string.IsNullOrWhiteSpace(outcome.DeliveryId)
            ? outcome.DeliveryId.Trim()
            : $"{outcome.ScriptRunId}:{outcome.CommandId}";

    private bool IsSameBinding(BindScriptBehaviorRequestedEvent evt)
    {
        return string.Equals(State.DefinitionActorId, evt.DefinitionActorId, StringComparison.Ordinal) &&
               string.Equals(State.Revision, evt.Revision, StringComparison.Ordinal) &&
               string.Equals(State.SourceHash, evt.SourceHash, StringComparison.Ordinal) &&
               string.Equals(State.ScopeId ?? string.Empty, evt.ScopeId ?? string.Empty, StringComparison.Ordinal);
    }

    private static void ValidateBinding(BindScriptBehaviorRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.DefinitionActorId))
            throw new InvalidOperationException("DefinitionActorId is required.");
        if (string.IsNullOrWhiteSpace(evt.ScriptId))
            throw new InvalidOperationException("ScriptId is required.");
        if (string.IsNullOrWhiteSpace(evt.Revision))
            throw new InvalidOperationException("Revision is required.");
        if ((evt.ScriptPackage?.CsharpSources.Count ?? 0) == 0)
            throw new InvalidOperationException("ScriptPackage must contain at least one C# source.");
    }

    private void EnsureBound()
    {
        if (string.IsNullOrWhiteSpace(State.DefinitionActorId) ||
            (State.ScriptPackage?.CsharpSources.Count ?? 0) == 0)
        {
            throw new InvalidOperationException($"Script behavior actor `{Id}` is not bound.");
        }
    }

    private static ScriptPackageSpec RequireBoundPackage(ScriptPackageSpec? scriptPackage)
    {
        if ((scriptPackage?.CsharpSources.Count ?? 0) == 0)
            throw new InvalidOperationException("ScriptPackage must contain at least one C# source.");

        return scriptPackage!.Clone();
    }

    private static string ResolveRunId(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
            return envelope.Payload.Unpack<RunScriptRequestedEvent>().RunId ?? string.Empty;

        return envelope.Id ?? string.Empty;
    }

    private static RunScriptRequestedEvent? ResolveRunRequest(EventEnvelope envelope)
    {
        // Refactor (iter149/cluster-1133): Old pattern: run request identity was unpacked separately in each dispatch helper.  New principle: extract the run request once so outcome and dispatch share the same identity fields.
        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) != true)
            return null;

        return envelope.Payload.Unpack<RunScriptRequestedEvent>();
    }

    private static string ResolveCommandId(EventEnvelope envelope)
    {
        // Refactor (iter149/cluster-1133): Old pattern: command identity was implicit in dispatch plumbing only.  New principle: outcome event carries the stable command id as typed data.
        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
        {
            var run = envelope.Payload.Unpack<RunScriptRequestedEvent>();
            if (!string.IsNullOrWhiteSpace(run.CommandId))
                return run.CommandId;
        }

        return envelope.Id ?? string.Empty;
    }

    private static string ResolveCorrelationId(EventEnvelope envelope)
    {
        // Refactor (iter149/cluster-1133): Old pattern: caller correlation was only a transport concern.  New principle: outcome event records the correlation id consumed by projection-session observers.
        if (!string.IsNullOrWhiteSpace(envelope.Propagation?.CorrelationId))
            return envelope.Propagation.CorrelationId;

        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
        {
            var run = envelope.Payload.Unpack<RunScriptRequestedEvent>();
            if (!string.IsNullOrWhiteSpace(run.CorrelationId))
                return run.CorrelationId;
        }

        return ResolveRunId(envelope);
    }

    private static string ResolveScopeId(EventEnvelope envelope, string? fallbackScopeId)
    {
        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
        {
            var run = envelope.Payload.Unpack<RunScriptRequestedEvent>();
            if (!string.IsNullOrWhiteSpace(run.ScopeId))
                return run.ScopeId.Trim();
        }

        if (envelope.Payload?.Is(BindScriptBehaviorRequestedEvent.Descriptor) == true)
        {
            var bind = envelope.Payload.Unpack<BindScriptBehaviorRequestedEvent>();
            if (!string.IsNullOrWhiteSpace(bind.ScopeId))
                return bind.ScopeId.Trim();
        }

        return fallbackScopeId?.Trim() ?? string.Empty;
    }
}
