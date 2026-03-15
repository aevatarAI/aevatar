using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core;

public sealed class ScriptBehaviorGAgent : GAgentBase<ScriptBehaviorState>
{
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

        await DispatchBehaviorAsync(envelope, CancellationToken.None);
    }

    protected override ScriptBehaviorState TransitionState(ScriptBehaviorState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptBehaviorBoundEvent>(ApplyBound)
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
                ScriptPackage: ScriptPackageModel.ResolveDeclaredPackage(
                    State.ScriptPackage,
                    State.SourceText ?? string.Empty),
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
        next.ScriptPackage = evt.ScriptPackage?.Clone() ?? new ScriptPackageSpec();
        next.ProtocolDescriptorSet = evt.ProtocolDescriptorSet;
        next.StateDescriptorFullName = evt.StateDescriptorFullName ?? string.Empty;
        next.ReadModelDescriptorFullName = evt.ReadModelDescriptorFullName ?? string.Empty;
        next.RuntimeSemantics = evt.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
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
        var scriptPackage = ScriptPackageModel.ResolveDeclaredPackage(
            state.ScriptPackage,
            state.SourceText ?? string.Empty);
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

    private void EnsureBound()
    {
        if (string.IsNullOrWhiteSpace(State.DefinitionActorId) ||
            ((State.ScriptPackage?.CsharpSources.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(State.SourceText)))
        {
            throw new InvalidOperationException($"Script behavior actor `{Id}` is not bound.");
        }
    }

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
