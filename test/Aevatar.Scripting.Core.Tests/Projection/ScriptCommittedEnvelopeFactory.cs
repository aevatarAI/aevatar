using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

internal static class ScriptCommittedEnvelopeFactory
{
    public static EventEnvelope CreateCommittedEnvelope(
        ScriptDomainFactCommitted fact,
        ScriptBehaviorState state,
        string eventId,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        var timestamp = Timestamp.FromDateTimeOffset(occurredAtUtc.ToUniversalTime());
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = timestamp.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("projection-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = fact.StateVersion,
                    Timestamp = timestamp,
                    EventData = Any.Pack(fact),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    public static ScriptBehaviorState CreateState(
        string definitionActorId,
        string scriptId,
        string revision,
        IMessage stateRoot,
        long lastAppliedEventVersion,
        string? readModelTypeUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(stateRoot);

        return new ScriptBehaviorState
        {
            DefinitionActorId = definitionActorId,
            ScriptId = scriptId,
            Revision = revision,
            StateTypeUrl = Any.Pack(stateRoot).TypeUrl,
            StateRoot = Any.Pack(stateRoot),
            ReadModelTypeUrl = readModelTypeUrl ?? string.Empty,
            LastAppliedEventVersion = lastAppliedEventVersion,
            LastEventId = $"evt-{lastAppliedEventVersion}",
        };
    }
}
