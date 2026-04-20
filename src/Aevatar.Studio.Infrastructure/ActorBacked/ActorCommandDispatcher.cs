using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Infrastructure.ActorBacked;

/// <summary>
/// Sends a domain event (command) to a target actor by wrapping it in an
/// <see cref="EventEnvelope"/> and invoking the grain synchronously via
/// <see cref="IActorDispatchPort"/>. Uses a <c>Direct</c> envelope route so
/// the grain's <c>HandleEnvelopeAsyncCore</c> treats it as an explicit
/// direct dispatch (matches the target actor, runs the event handler
/// pipeline, commits the event). This mirrors every other working write
/// path in the codebase — <c>GAgentService</c>,
/// <c>ServiceInvocationDispatcher</c>, <c>A2AAdapterService</c>,
/// <c>ProjectionScopeActorRuntime</c> — all use <c>CreateDirect</c>.
/// The earlier <c>TopologyPublication.Self</c> routing was only reliably
/// delivered via stream subscription, which does not fire the persistence
/// pipeline in the current InMemory setup.
/// </summary>
internal static class ActorCommandDispatcher
{
    private const string PublisherActorId = "aevatar.studio.infrastructure.actor-backed";

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
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, actor.Id),
        };

        return dispatchPort.DispatchAsync(actor.Id, envelope, ct);
    }
}
