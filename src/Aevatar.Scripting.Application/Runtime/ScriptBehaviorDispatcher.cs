using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Core.Artifacts;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Core.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Application.Runtime;

public sealed class ScriptBehaviorDispatcher : IScriptBehaviorDispatcher
{
    private readonly IScriptBehaviorArtifactResolver _artifactResolver;
    private readonly IProtobufMessageCodec _codec;

    public ScriptBehaviorDispatcher(
        IScriptBehaviorArtifactResolver artifactResolver,
        IProtobufMessageCodec codec)
    {
        _artifactResolver = artifactResolver ?? throw new ArgumentNullException(nameof(artifactResolver));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    public async Task<IReadOnlyList<ScriptDomainFactCommitted>> DispatchAsync(
        ScriptBehaviorDispatchRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Envelope);

        var payload = request.Envelope.Payload;
        if (payload == null)
            return [];

        var artifact = _artifactResolver.Resolve(new ScriptBehaviorArtifactRequest(
            request.ScriptId,
            request.Revision,
            request.SourceText,
            request.SourceHash));
        var inbound = BuildInboundMessage(request.Envelope);
        ValidateInboundContract(request, artifact.Descriptor, inbound.PayloadTypeUrl);
        var correlationId = request.Envelope.Propagation?.CorrelationId ?? ResolveRunId(request.Envelope);
        var currentState = _codec.Unpack(request.CurrentStateRoot, artifact.Descriptor.StateClrType);
        var context = new ScriptDispatchContext(
            ActorId: request.ActorId,
            ScriptId: request.ScriptId,
            Revision: request.Revision,
            RunId: ResolveRunId(request.Envelope),
            MessageType: inbound.MessageType,
            MessageId: inbound.MessageId,
            CommandId: request.Envelope.Id ?? string.Empty,
            CorrelationId: correlationId,
            CausationId: inbound.CausationId,
            DefinitionActorId: request.DefinitionActorId,
            CurrentState: currentState,
            RuntimeCapabilities: request.Capabilities);

        var behavior = artifact.CreateBehavior();
        try
        {
            var inboundMessageClrType = ResolveInboundMessageClrType(request.Envelope, artifact.Descriptor, inbound.PayloadTypeUrl);
            var typedInbound = _codec.Unpack(inbound.Payload, inboundMessageClrType)
                ?? throw new InvalidOperationException($"Failed to unpack inbound payload type `{inbound.PayloadTypeUrl}`.");
            var domainEvents = NormalizeDomainEvents(await behavior.DispatchAsync(typedInbound, context, ct));
            if (domainEvents.Count == 0)
                return [];

            ValidateDomainEventContract(request, artifact.Descriptor, domainEvents);

            var occurredAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var committed = new List<ScriptDomainFactCommitted>(domainEvents.Count);
            for (var i = 0; i < domainEvents.Count; i++)
            {
                var domainEvent = domainEvents[i];
                var sequence = i + 1L;
                var domainEventPayload = _codec.Pack(domainEvent)
                    ?? throw new InvalidOperationException("Script domain event cannot be null.");
                committed.Add(new ScriptDomainFactCommitted
                {
                    ActorId = request.ActorId,
                    DefinitionActorId = request.DefinitionActorId,
                    ScriptId = request.ScriptId,
                    Revision = request.Revision,
                    RunId = ResolveRunId(request.Envelope),
                    CommandId = request.Envelope.Id ?? string.Empty,
                    CorrelationId = correlationId,
                    EventSequence = sequence,
                    EventType = domainEventPayload.TypeUrl ?? string.Empty,
                    DomainEventPayload = domainEventPayload,
                    StateTypeUrl = artifact.Contract.StateTypeUrl ?? request.StateTypeUrl ?? string.Empty,
                    ReadModelTypeUrl = artifact.Contract.ReadModelTypeUrl ?? request.ReadModelTypeUrl ?? string.Empty,
                    StateVersion = request.CurrentStateVersion + sequence,
                    OccurredAtUnixTimeMs = occurredAtUnixTimeMs,
                });
            }

            return committed;
        }
        finally
        {
            if (behavior is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (behavior is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static ScriptInboundPayload BuildInboundMessage(EventEnvelope envelope)
    {
        var payload = envelope.Payload?.Clone() ?? Any.Pack(new Empty());
        if (payload.Is(RunScriptRequestedEvent.Descriptor))
        {
            var run = payload.Unpack<RunScriptRequestedEvent>();
            var inputPayload = run.InputPayload?.Clone() ?? Any.Pack(new Empty());
            return new ScriptInboundPayload(
                MessageType: string.IsNullOrWhiteSpace(run.RequestedEventType)
                    ? inputPayload.TypeUrl ?? string.Empty
                    : run.RequestedEventType,
                PayloadTypeUrl: inputPayload.TypeUrl ?? string.Empty,
                Payload: inputPayload,
                MessageId: run.RunId ?? envelope.Id ?? string.Empty,
                CommandId: string.IsNullOrWhiteSpace(run.CommandId) ? envelope.Id ?? string.Empty : run.CommandId,
                CorrelationId: string.IsNullOrWhiteSpace(run.CorrelationId)
                    ? envelope.Propagation?.CorrelationId ?? run.RunId ?? string.Empty
                    : run.CorrelationId,
                CausationId: envelope.Id ?? string.Empty);
        }

        return new ScriptInboundPayload(
            MessageType: payload.TypeUrl ?? string.Empty,
            PayloadTypeUrl: payload.TypeUrl ?? string.Empty,
            Payload: payload,
            MessageId: envelope.Id ?? string.Empty,
            CommandId: envelope.Id ?? string.Empty,
            CorrelationId: envelope.Propagation?.CorrelationId ?? string.Empty,
            CausationId: envelope.Id ?? string.Empty);
    }

    private static IReadOnlyList<IMessage> NormalizeDomainEvents(IReadOnlyList<IMessage>? domainEvents)
    {
        if (domainEvents == null || domainEvents.Count == 0)
            return [];

        return domainEvents
            .Where(static x => x != null)
            .ToArray()!;
    }

    private static string ResolveRunId(EventEnvelope envelope)
    {
        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
            return envelope.Payload.Unpack<RunScriptRequestedEvent>().RunId ?? string.Empty;

        return envelope.Id ?? string.Empty;
    }

    private static void ValidateInboundContract(
        ScriptBehaviorDispatchRequest request,
        ScriptBehaviorDescriptor descriptor,
        string actualPayloadTypeUrl)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (request.Envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
        {
            if (descriptor.Commands.ContainsKey(actualPayloadTypeUrl))
                return;

            throw new InvalidOperationException(
                $"Script behavior actor `{request.ActorId}` rejected command payload type `{actualPayloadTypeUrl}`. " +
                $"Declared command types: {string.Join(", ", descriptor.Commands.Keys)}.");
        }

        if (descriptor.Commands.Count == 0 && descriptor.Signals.Count == 0)
            return;

        if (descriptor.Commands.ContainsKey(actualPayloadTypeUrl) || descriptor.Signals.ContainsKey(actualPayloadTypeUrl))
            return;

        throw new InvalidOperationException(
            $"Script behavior actor `{request.ActorId}` rejected inbound payload type `{actualPayloadTypeUrl}`. " +
            $"Declared command types: {string.Join(", ", descriptor.Commands.Keys)}. " +
            $"Declared internal signal types: {string.Join(", ", descriptor.Signals.Keys)}.");
    }

    private static void ValidateDomainEventContract(
        ScriptBehaviorDispatchRequest request,
        ScriptBehaviorDescriptor descriptor,
        IReadOnlyList<IMessage> domainEvents)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(domainEvents);

        if (descriptor.DomainEvents.Count == 0)
            return;

        foreach (var domainEvent in domainEvents)
        {
            var typeUrl = ScriptMessageTypes.GetTypeUrl(domainEvent);
            if (descriptor.DomainEvents.ContainsKey(typeUrl))
                continue;

            throw new InvalidOperationException(
                $"Script behavior actor `{request.ActorId}` emitted undeclared domain event type `{typeUrl}`. " +
                $"Declared domain event types: {string.Join(", ", descriptor.DomainEvents.Keys)}.");
        }
    }

    private static System.Type ResolveInboundMessageClrType(
        EventEnvelope envelope,
        ScriptBehaviorDescriptor descriptor,
        string payloadTypeUrl)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (envelope.Payload?.Is(RunScriptRequestedEvent.Descriptor) == true)
        {
            if (descriptor.Commands.TryGetValue(payloadTypeUrl, out var command))
                return command.MessageClrType;

            throw new InvalidOperationException($"Command payload type `{payloadTypeUrl}` is not declared.");
        }

        if (descriptor.Commands.TryGetValue(payloadTypeUrl, out var commandRegistration))
            return commandRegistration.MessageClrType;
        if (descriptor.Signals.TryGetValue(payloadTypeUrl, out var signalRegistration))
            return signalRegistration.MessageClrType;

        throw new InvalidOperationException($"Inbound payload type `{payloadTypeUrl}` is not declared.");
    }

    private sealed record ScriptInboundPayload(
        string MessageType,
        string PayloadTypeUrl,
        Any Payload,
        string MessageId,
        string CommandId,
        string CorrelationId,
        string CausationId);
}
