using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
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
    private readonly IAgentManifestStore _manifestStore;

    public ActorProjectionOwnershipCoordinator(IActorRuntime runtime, IAgentManifestStore manifestStore)
    {
        _runtime = runtime;
        _manifestStore = manifestStore;
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
        var manifest = await _manifestStore.LoadAsync(actorId, ct);
        if (manifest == null || IsCoordinatorAgentType(manifest.AgentTypeName))
            return actor;

        throw new InvalidOperationException(
            $"Actor '{actorId}' is not a projection ownership coordinator actor.");
    }

    private static bool IsCoordinatorAgentType(string? agentTypeName)
    {
        if (string.IsNullOrWhiteSpace(agentTypeName))
            return false;

        var resolved = System.Type.GetType(agentTypeName, throwOnError: false);
        if (resolved != null)
            return typeof(ProjectionOwnershipCoordinatorGAgent).IsAssignableFrom(resolved);

        var expectedTypeName = typeof(ProjectionOwnershipCoordinatorGAgent).FullName
            ?? nameof(ProjectionOwnershipCoordinatorGAgent);
        return agentTypeName.Contains(expectedTypeName, StringComparison.Ordinal);
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
