using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core;

public sealed class ScriptBehaviorGAgent : GAgentBase<ScriptBehaviorState>
{
    private static readonly TimeSpan BindingTimeout = TimeSpan.FromSeconds(10);
    private readonly IScriptBehaviorDispatcher _dispatcher;
    private readonly IScriptBehaviorRuntimeCapabilityFactory _capabilityFactory;
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;

    public ScriptBehaviorGAgent(
        IScriptBehaviorDispatcher dispatcher,
        IScriptBehaviorRuntimeCapabilityFactory capabilityFactory,
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        InitializeId();
    }

    [AllEventHandler(AllowSelfHandling = true)]
    public async Task HandleEnvelopeAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload == null)
            return;

        if (envelope.Payload.Is(BindScriptBehaviorRequestedEvent.Descriptor))
        {
            await HandleBindRequestedAsync(envelope.Payload.Unpack<BindScriptBehaviorRequestedEvent>(), CancellationToken.None);
            return;
        }

        if (envelope.Payload.Is(ProvisionScriptBehaviorRequestedEvent.Descriptor))
        {
            await HandleProvisionRequestedAsync(
                envelope.Payload.Unpack<ProvisionScriptBehaviorRequestedEvent>(),
                CancellationToken.None);
            return;
        }

        if (envelope.Payload.Is(ScriptDefinitionSnapshotRespondedEvent.Descriptor))
        {
            await HandleDefinitionSnapshotRespondedAsync(
                envelope.Payload.Unpack<ScriptDefinitionSnapshotRespondedEvent>(),
                CancellationToken.None);
            return;
        }

        if (envelope.Payload.Is(ScriptBehaviorBindingTimeoutFiredEvent.Descriptor))
        {
            await HandleBindingTimeoutFiredAsync(
                envelope.Payload.Unpack<ScriptBehaviorBindingTimeoutFiredEvent>(),
                envelope,
                CancellationToken.None);
            return;
        }

        if (envelope.Payload.Is(QueryScriptBehaviorBindingRequestedEvent.Descriptor))
        {
            await HandleBindingQueryAsync(envelope.Payload.Unpack<QueryScriptBehaviorBindingRequestedEvent>(), CancellationToken.None);
            return;
        }

        if (envelope.Payload.Is(RunScriptRequestedEvent.Descriptor) && ShouldQueueRunRequest())
        {
            await HandleQueuedRunRequestedAsync(
                envelope.Payload.Unpack<RunScriptRequestedEvent>(),
                CancellationToken.None);
            return;
        }

        await DispatchBehaviorAsync(envelope, CancellationToken.None);
    }

    protected override ScriptBehaviorState TransitionState(ScriptBehaviorState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptBehaviorBindingRequestedEvent>(ApplyBindingRequested)
            .On<ScriptBehaviorBoundEvent>(ApplyBound)
            .On<ScriptBehaviorBindingFailedEvent>(ApplyBindingFailed)
            .On<ScriptBehaviorRunQueuedEvent>(ApplyQueuedRun)
            .On<ScriptDomainFactCommitted>(ApplyCommittedFact)
            .OrCurrent();

    private async Task HandleBindRequestedAsync(
        BindScriptBehaviorRequestedEvent evt,
        CancellationToken ct)
    {
        ValidateBinding(evt);

        if (IsSameBinding(evt))
            return;

        await PersistDomainEventAsync(new ScriptBehaviorBoundEvent
        {
            DefinitionActorId = evt.DefinitionActorId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            Revision = evt.Revision ?? string.Empty,
            SourceText = evt.SourceText ?? string.Empty,
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
        }, ct);
    }

    private async Task HandleProvisionRequestedAsync(
        ProvisionScriptBehaviorRequestedEvent evt,
        CancellationToken ct)
    {
        ValidateProvisioning(evt);

        if (IsSatisfiedBindingRequest(evt) || IsSamePendingBinding(evt))
            return;
        if (HasPendingBinding())
        {
            throw new InvalidOperationException(
                $"Script behavior actor `{Id}` is already binding definition `{State.PendingDefinitionActorId}` revision `{State.PendingRevision}`.");
        }

        var requestId = string.IsNullOrWhiteSpace(evt.RequestId)
            ? Guid.NewGuid().ToString("N")
            : evt.RequestId;
        await ScheduleSelfDurableTimeoutAsync(
            BuildBindingTimeoutCallbackId(requestId),
            BindingTimeout,
            new ScriptBehaviorBindingTimeoutFiredEvent
            {
                RequestId = requestId,
            },
            ct: ct);

        await PersistDomainEventAsync(new ScriptBehaviorBindingRequestedEvent
        {
            DefinitionActorId = evt.DefinitionActorId ?? string.Empty,
            RequestedRevision = evt.RequestedRevision ?? string.Empty,
            RequestId = requestId,
        }, ct);

        try
        {
            var definitionActorId = evt.DefinitionActorId ?? string.Empty;
            await SendToAsync(
                definitionActorId,
                new QueryScriptDefinitionSnapshotRequestedEvent
                {
                    RequestId = requestId,
                    RequestedRevision = evt.RequestedRevision ?? string.Empty,
                    ReplyActorId = Id,
                },
                ct);
        }
        catch (Exception ex)
        {
            await PersistDomainEventAsync(new ScriptBehaviorBindingFailedEvent
            {
                RequestId = requestId,
                FailureReason = "Failed to dispatch definition query. reason=" + ex.Message,
            }, ct);
        }
    }

    private async Task HandleDefinitionSnapshotRespondedAsync(
        ScriptDefinitionSnapshotRespondedEvent evt,
        CancellationToken ct)
    {
        if (!IsPendingBindingRequest(evt.RequestId))
            return;

        var queuedRuns = ClonePendingRuns();

        if (!evt.Found)
        {
            await PersistDomainEventAsync(new ScriptBehaviorBindingFailedEvent
            {
                RequestId = evt.RequestId ?? string.Empty,
                FailureReason = string.IsNullOrWhiteSpace(evt.FailureReason)
                    ? "Script definition query returned not found."
                    : evt.FailureReason,
            }, ct);
            return;
        }

        var bind = new BindScriptBehaviorRequestedEvent
        {
            DefinitionActorId = State.PendingDefinitionActorId ?? string.Empty,
            ScriptId = evt.ScriptId ?? string.Empty,
            Revision = evt.Revision ?? string.Empty,
            SourceText = evt.SourceText ?? string.Empty,
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
        };
        try
        {
            ValidateBinding(bind);
        }
        catch (Exception ex)
        {
            await PersistDomainEventAsync(new ScriptBehaviorBindingFailedEvent
            {
                RequestId = evt.RequestId ?? string.Empty,
                FailureReason = ex.Message,
            }, ct);
            return;
        }

        await PersistDomainEventAsync(new ScriptBehaviorBoundEvent
        {
            DefinitionActorId = bind.DefinitionActorId,
            ScriptId = bind.ScriptId,
            Revision = bind.Revision,
            SourceText = bind.SourceText,
            SourceHash = bind.SourceHash,
            StateTypeUrl = bind.StateTypeUrl,
            ReadModelTypeUrl = bind.ReadModelTypeUrl,
            ReadModelSchemaVersion = bind.ReadModelSchemaVersion,
            ReadModelSchemaHash = bind.ReadModelSchemaHash,
            ScriptPackage = bind.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
            ProtocolDescriptorSet = bind.ProtocolDescriptorSet,
            StateDescriptorFullName = bind.StateDescriptorFullName,
            ReadModelDescriptorFullName = bind.ReadModelDescriptorFullName,
            RuntimeSemantics = bind.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
        }, ct);

        await ReplayQueuedRunsAsync(queuedRuns, ct);
    }

    private async Task HandleBindingTimeoutFiredAsync(
        ScriptBehaviorBindingTimeoutFiredEvent evt,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        if (!IsPendingBindingRequest(evt.RequestId))
            return;

        if (!RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var callbackState) ||
            !string.Equals(callbackState.CallbackId, BuildBindingTimeoutCallbackId(evt.RequestId), StringComparison.Ordinal))
            return;

        await PersistDomainEventAsync(new ScriptBehaviorBindingFailedEvent
        {
            RequestId = evt.RequestId ?? string.Empty,
            FailureReason = $"Timed out waiting for script definition response. request_id={evt.RequestId}",
        }, ct);
    }

    private async Task HandleQueuedRunRequestedAsync(
        RunScriptRequestedEvent evt,
        CancellationToken ct)
    {
        if (HasPendingRun(evt.RunId))
            return;

        await PersistDomainEventAsync(new ScriptBehaviorRunQueuedEvent
        {
            RunRequest = evt.Clone(),
        }, ct);
    }

    private async Task HandleBindingQueryAsync(
        QueryScriptBehaviorBindingRequestedEvent evt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return;

        await EventPublisher.SendToAsync(evt.ReplyStreamId, BuildBindingResponse(evt), ct, sourceEnvelope: null);
    }

    private async Task DispatchBehaviorAsync(
        EventEnvelope envelope,
        CancellationToken ct)
    {
        EnsureBound();

        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
        {
            var run = envelope.Payload.Unpack<RunScriptRequestedEvent>();
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
        }

        var capabilities = _capabilityFactory.Create(
            new ScriptBehaviorRuntimeCapabilityContext(
                ActorId: Id,
                ScriptId: State.ScriptId ?? string.Empty,
                Revision: State.Revision ?? string.Empty,
                DefinitionActorId: State.DefinitionActorId ?? string.Empty,
                RunId: ResolveRunId(envelope),
                CorrelationId: ResolveCorrelationId(envelope)),
            publishAsync: (message, audience, token) => PublishAsync(message, audience, token),
            sendToAsync: (targetActorId, message, token) => SendToAsync(targetActorId, message, token),
            publishToSelfAsync: (message, token) => PublishAsync(message, TopologyAudience.Self, token),
            scheduleSelfSignalAsync: (callbackId, dueTime, message, token) =>
                ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, message, ct: token),
            cancelCallbackAsync: CancelDurableCallbackAsync);

        var facts = await _dispatcher.DispatchAsync(
            new ScriptBehaviorDispatchRequest(
                ActorId: Id,
                DefinitionActorId: State.DefinitionActorId ?? string.Empty,
                ScriptId: State.ScriptId ?? string.Empty,
                Revision: State.Revision ?? string.Empty,
                SourceText: State.SourceText ?? string.Empty,
                SourceHash: State.SourceHash ?? string.Empty,
                ScriptPackage: State.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
                StateTypeUrl: State.StateTypeUrl ?? string.Empty,
                ReadModelTypeUrl: State.ReadModelTypeUrl ?? string.Empty,
                CurrentStateRoot: State.StateRoot?.Clone(),
                CurrentStateVersion: State.LastAppliedEventVersion,
                Envelope: envelope,
                Capabilities: capabilities),
            ct);

        if (facts.Count == 0)
            return;

        await PersistDomainEventsAsync(facts, ct);
    }

    private static ScriptBehaviorState ApplyBound(
        ScriptBehaviorState state,
        ScriptBehaviorBoundEvent evt)
    {
        var next = state.Clone();
        next.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        next.ScriptId = evt.ScriptId ?? string.Empty;
        next.Revision = evt.Revision ?? string.Empty;
        next.SourceText = evt.SourceText ?? string.Empty;
        next.SourceHash = evt.SourceHash ?? string.Empty;
        next.StateTypeUrl = evt.StateTypeUrl ?? string.Empty;
        next.ReadModelTypeUrl = evt.ReadModelTypeUrl ?? string.Empty;
        next.ReadModelSchemaVersion = evt.ReadModelSchemaVersion ?? string.Empty;
        next.ReadModelSchemaHash = evt.ReadModelSchemaHash ?? string.Empty;
        next.ScriptPackage = evt.ScriptPackage?.Clone() ?? ScriptPackageModel.CreateSingleSourcePackage(evt.SourceText ?? string.Empty);
        next.ProtocolDescriptorSet = evt.ProtocolDescriptorSet;
        next.StateDescriptorFullName = evt.StateDescriptorFullName ?? string.Empty;
        next.ReadModelDescriptorFullName = evt.ReadModelDescriptorFullName ?? string.Empty;
        next.RuntimeSemantics = evt.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
        next.PendingDefinitionActorId = string.Empty;
        next.PendingRevision = string.Empty;
        next.PendingBindingRequestId = string.Empty;
        next.PendingRunRequests.Clear();
        next.BindingFailureReason = string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = string.Concat(evt.Revision ?? string.Empty, ":binding");
        return next;
    }

    private static ScriptBehaviorState ApplyBindingRequested(
        ScriptBehaviorState state,
        ScriptBehaviorBindingRequestedEvent evt)
    {
        var next = state.Clone();
        next.PendingDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        next.PendingRevision = evt.RequestedRevision ?? string.Empty;
        next.PendingBindingRequestId = evt.RequestId ?? string.Empty;
        next.BindingFailureReason = string.Empty;
        return next;
    }

    private static ScriptBehaviorState ApplyBindingFailed(
        ScriptBehaviorState state,
        ScriptBehaviorBindingFailedEvent evt)
    {
        var next = state.Clone();
        next.PendingDefinitionActorId = string.Empty;
        next.PendingRevision = string.Empty;
        next.PendingBindingRequestId = string.Empty;
        next.PendingRunRequests.Clear();
        next.BindingFailureReason = evt.FailureReason ?? string.Empty;
        return next;
    }

    private static ScriptBehaviorState ApplyQueuedRun(
        ScriptBehaviorState state,
        ScriptBehaviorRunQueuedEvent evt)
    {
        var next = state.Clone();
        if (evt.RunRequest != null)
            next.PendingRunRequests.Add(evt.RunRequest.Clone());
        return next;
    }

    private ScriptBehaviorState ApplyCommittedFact(
        ScriptBehaviorState state,
        ScriptDomainFactCommitted evt)
    {
        var next = state.Clone();
        var payload = evt.DomainEventPayload?.Clone() ?? Any.Pack(new Empty());
        var scriptPackage = state.ScriptPackage?.Clone() ?? ScriptPackageModel.CreateSingleSourcePackage(state.SourceText ?? string.Empty);
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
            var appliedState = behavior.ApplyDomainEvent(
                currentState,
                domainEvent,
                new Aevatar.Scripting.Abstractions.Behaviors.ScriptFactContext(
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
                    evt.OccurredAtUnixTimeMs));
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
        return next;
    }

    private bool IsSameBinding(BindScriptBehaviorRequestedEvent evt)
    {
        return string.Equals(State.DefinitionActorId, evt.DefinitionActorId, StringComparison.Ordinal) &&
               string.Equals(State.Revision, evt.Revision, StringComparison.Ordinal) &&
               string.Equals(State.SourceHash, evt.SourceHash, StringComparison.Ordinal);
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
        if ((evt.ScriptPackage?.CsharpSources.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(evt.SourceText))
            throw new InvalidOperationException("ScriptPackage must contain at least one C# source.");
    }

    private ScriptBehaviorBindingRespondedEvent BuildBindingResponse(
        QueryScriptBehaviorBindingRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var found = !string.IsNullOrWhiteSpace(State.DefinitionActorId);
        var failureReason = found
            ? string.Empty
            : HasPendingBinding()
                ? $"Script behavior actor is binding definition `{State.PendingDefinitionActorId}` revision `{State.PendingRevision}`."
                : string.IsNullOrWhiteSpace(State.BindingFailureReason)
                    ? "Script behavior actor is not bound."
                    : State.BindingFailureReason;
        return new ScriptBehaviorBindingRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = found,
            Pending = !found && HasPendingBinding(),
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            ScriptId = State.ScriptId ?? string.Empty,
            Revision = State.Revision ?? string.Empty,
            SourceHash = State.SourceHash ?? string.Empty,
            StateTypeUrl = State.StateTypeUrl ?? string.Empty,
            ReadModelTypeUrl = State.ReadModelTypeUrl ?? string.Empty,
            ReadModelSchemaVersion = State.ReadModelSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = State.ReadModelSchemaHash ?? string.Empty,
            StateDescriptorFullName = State.StateDescriptorFullName ?? string.Empty,
            ReadModelDescriptorFullName = State.ReadModelDescriptorFullName ?? string.Empty,
            RuntimeSemantics = State.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
            FailureReason = failureReason,
        };
    }

    private void EnsureBound()
    {
        if (string.IsNullOrWhiteSpace(State.DefinitionActorId) ||
            ((State.ScriptPackage?.CsharpSources.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(State.SourceText)))
        {
            var reason = HasPendingBinding()
                ? $"Script behavior actor `{Id}` is still binding definition `{State.PendingDefinitionActorId}` revision `{State.PendingRevision}`."
                : string.IsNullOrWhiteSpace(State.BindingFailureReason)
                    ? $"Script behavior actor `{Id}` is not bound."
                    : $"Script behavior actor `{Id}` is not bound. reason={State.BindingFailureReason}";
            throw new InvalidOperationException(reason);
        }
    }

    private bool ShouldQueueRunRequest() => HasPendingBinding();

    private bool HasPendingBinding() =>
        !string.IsNullOrWhiteSpace(State.PendingBindingRequestId);

    private bool IsPendingBindingRequest(string? requestId) =>
        HasPendingBinding() &&
        !string.IsNullOrWhiteSpace(requestId) &&
        string.Equals(State.PendingBindingRequestId, requestId, StringComparison.Ordinal);

    private bool IsSamePendingBinding(ProvisionScriptBehaviorRequestedEvent evt) =>
        HasPendingBinding() &&
        string.Equals(State.PendingDefinitionActorId, evt.DefinitionActorId, StringComparison.Ordinal) &&
        string.Equals(State.PendingRevision, evt.RequestedRevision ?? string.Empty, StringComparison.Ordinal);

    private bool IsSatisfiedBindingRequest(ProvisionScriptBehaviorRequestedEvent evt)
    {
        if (!string.Equals(State.DefinitionActorId, evt.DefinitionActorId, StringComparison.Ordinal))
            return false;

        return string.IsNullOrWhiteSpace(evt.RequestedRevision) ||
               string.Equals(State.Revision, evt.RequestedRevision, StringComparison.Ordinal);
    }

    private bool HasPendingRun(string? runId) =>
        !string.IsNullOrWhiteSpace(runId) &&
        State.PendingRunRequests.Any(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));

    private RunScriptRequestedEvent[] ClonePendingRuns() =>
        State.PendingRunRequests.Select(static x => x.Clone()).ToArray();

    private async Task ReplayQueuedRunsAsync(
        IReadOnlyCollection<RunScriptRequestedEvent> queuedRuns,
        CancellationToken ct)
    {
        foreach (var run in queuedRuns)
            await PublishAsync(run, TopologyAudience.Self, ct);
    }

    private static void ValidateProvisioning(ProvisionScriptBehaviorRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.DefinitionActorId))
            throw new InvalidOperationException("DefinitionActorId is required.");
    }

    private string BuildBindingTimeoutCallbackId(string? requestId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId("script-behavior-binding", Id, requestId ?? string.Empty);

    private static string ResolveRunId(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
            return envelope.Payload.Unpack<RunScriptRequestedEvent>().RunId ?? string.Empty;

        return envelope.Id ?? string.Empty;
    }

    private static string ResolveCorrelationId(EventEnvelope envelope)
    {
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
}
