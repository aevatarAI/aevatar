using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Sends a domain event (command) to a target actor by wrapping it in an
/// <see cref="EventEnvelope"/> and invoking the grain synchronously via
/// <see cref="IActorDispatchPort"/>. We deliberately route through the
/// dispatch port (direct grain call) rather than <c>IActor.HandleEventAsync</c>
/// (which publishes to the actor's stream): stream publication is
/// fire-and-forget and has proven unreliable as a write path in the current
/// InMemory setup — envelopes sometimes never reach the grain pipeline, so
/// <c>PersistDomainEventAsync</c> never runs and saves appear successful
/// but the actor state never commits. Dispatch port waits for the grain to
/// process the event, matching the pattern used by
/// <c>WorkflowRunActorPort</c> and <c>ProjectionScopeActorRuntime.DispatchAsync</c>.
/// </summary>
internal static class ActorCommandDispatcher
{
    public static Task SendAsync<TEvent>(
        IActorDispatchPort dispatchPort,
        IActor actor,
        TEvent evt,
        CancellationToken ct = default)
        where TEvent : IMessage
    {
        ArgumentNullException.ThrowIfNull(dispatchPort);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(evt);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(
                actor.Id, TopologyAudience.Self),
        };

        return dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
