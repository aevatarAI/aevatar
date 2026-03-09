using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
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
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly ProjectionOwnershipCoordinatorOptions _options;

    public ActorProjectionOwnershipCoordinator(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier agentTypeVerifier,
        ProjectionOwnershipCoordinatorOptions? options = null)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _agentTypeVerifier = agentTypeVerifier;
        _options = options ?? new ProjectionOwnershipCoordinatorOptions();
    }

    public async Task AcquireAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default)
    {
        var coordinatorActor = await ResolveCoordinatorActorAsync(scopeId, ct);
        var occurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        var envelope = CreateCoordinatorEnvelope(
            new ProjectionOwnershipAcquireEvent
            {
                ScopeId = scopeId,
                SessionId = sessionId,
                LeaseTtlMs = _options.ResolveLeaseTtlMs(),
                OccurredAtUtc = occurredAtUtc,
            },
            sessionId);
        await _dispatchPort.DispatchAsync(coordinatorActor.Id, envelope, ct);
    }

    public async Task ReleaseAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default)
    {
        var coordinatorActor = await ResolveCoordinatorActorAsync(scopeId, ct);
        var occurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        var envelope = CreateCoordinatorEnvelope(
            new ProjectionOwnershipReleaseEvent
            {
                ScopeId = scopeId,
                SessionId = sessionId,
                OccurredAtUtc = occurredAtUtc,
            },
            sessionId);
        await _dispatchPort.DispatchAsync(coordinatorActor.Id, envelope, ct);
    }

    private async Task<IActor> ResolveCoordinatorActorAsync(string scopeId, CancellationToken ct)
    {
        var coordinatorActorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId(scopeId);
        var existing = await _runtime.GetAsync(coordinatorActorId);
        if (existing != null)
            return await EnsureCoordinatorActorTypeAsync(existing, coordinatorActorId, ct);

        try
        {
            var created = await _runtime.CreateAsync<ProjectionOwnershipCoordinatorGAgent>(coordinatorActorId, ct);
            return await EnsureCoordinatorActorTypeAsync(created, coordinatorActorId, ct);
        }
        catch (InvalidOperationException)
        {
            var raced = await _runtime.GetAsync(coordinatorActorId);
            if (raced != null)
                return await EnsureCoordinatorActorTypeAsync(raced, coordinatorActorId, ct);

            throw;
        }
    }

    private async Task<IActor> EnsureCoordinatorActorTypeAsync(IActor actor, string actorId, CancellationToken ct)
    {
        if (await _agentTypeVerifier.IsExpectedAsync(actorId, typeof(ProjectionOwnershipCoordinatorGAgent), ct))
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
