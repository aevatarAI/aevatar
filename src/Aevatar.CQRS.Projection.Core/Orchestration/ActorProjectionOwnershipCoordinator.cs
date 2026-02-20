using Aevatar.CQRS.Projection.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Resolves projection ownership coordinator actors and dispatches acquire/release events.
/// </summary>
public sealed class ActorProjectionOwnershipCoordinator : IProjectionOwnershipCoordinator
{
    private const string CoordinatorPublisherId = "projection.ownership.coordinator";
    private readonly IActorRuntime _runtime;

    public ActorProjectionOwnershipCoordinator(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task AcquireAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default)
    {
        var coordinatorActor = await ResolveCoordinatorActorAsync(scopeId, ct);
        var envelope = CreateCoordinatorEnvelope(
            new ProjectionOwnershipAcquireEvent
            {
                ScopeId = scopeId,
                SessionId = sessionId,
            },
            sessionId);
        await coordinatorActor.HandleEventAsync(envelope, ct);
    }

    public async Task ReleaseAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default)
    {
        var coordinatorActor = await ResolveCoordinatorActorAsync(scopeId, ct);
        var envelope = CreateCoordinatorEnvelope(
            new ProjectionOwnershipReleaseEvent
            {
                ScopeId = scopeId,
                SessionId = sessionId,
            },
            sessionId);
        await coordinatorActor.HandleEventAsync(envelope, ct);
    }

    private async Task<IActor> ResolveCoordinatorActorAsync(string scopeId, CancellationToken ct)
    {
        var coordinatorActorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId(scopeId);
        var existing = await _runtime.GetAsync(coordinatorActorId);
        if (existing != null)
            return EnsureCoordinatorActorType(existing, coordinatorActorId);

        try
        {
            var created = await _runtime.CreateAsync<ProjectionOwnershipCoordinatorGAgent>(coordinatorActorId, ct);
            return EnsureCoordinatorActorType(created, coordinatorActorId);
        }
        catch (InvalidOperationException)
        {
            var raced = await _runtime.GetAsync(coordinatorActorId);
            if (raced != null)
                return EnsureCoordinatorActorType(raced, coordinatorActorId);

            throw;
        }
    }

    private static IActor EnsureCoordinatorActorType(IActor actor, string actorId)
    {
        if (actor.Agent is ProjectionOwnershipCoordinatorGAgent)
            return actor;

        throw new InvalidOperationException(
            $"Actor '{actorId}' is not a projection ownership coordinator actor.");
    }

    private static EventEnvelope CreateCoordinatorEnvelope(IMessage payload, string correlationId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            PublisherId = CoordinatorPublisherId,
            Direction = EventDirection.Self,
            CorrelationId = correlationId,
        };
}
