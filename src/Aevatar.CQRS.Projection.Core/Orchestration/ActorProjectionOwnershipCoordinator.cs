using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.TypeSystem;
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
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IAgentTypeVerifier _agentTypeVerifier;
    private readonly IEventStore _eventStore;
    private readonly ProjectionOwnershipCoordinatorOptions _options;

    public ActorProjectionOwnershipCoordinator(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IAgentTypeVerifier agentTypeVerifier,
        IEventStore eventStore,
        ProjectionOwnershipCoordinatorOptions? options = null)
    {
        _runtime = runtime;
        _dispatchPort = dispatchPort;
        _agentTypeVerifier = agentTypeVerifier;
        _eventStore = eventStore;
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

    public async Task<bool> HasActiveLeaseAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ct.ThrowIfCancellationRequested();

        var coordinatorActorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId(scopeId);
        var existing = await _runtime.GetAsync(coordinatorActorId);
        if (existing != null)
            await EnsureCoordinatorActorTypeAsync(existing, coordinatorActorId, ct);

        var events = await _eventStore.GetEventsAsync(coordinatorActorId, ct: ct);
        if (events.Count == 0)
            return false;

        var state = new ProjectionOwnershipCoordinatorState();
        foreach (var stateEvent in events.OrderBy(x => x.Version))
        {
            var payload = stateEvent.EventData;
            if (payload == null || string.IsNullOrWhiteSpace(payload.TypeUrl))
                continue;

            if (payload.TryUnpack<ProjectionOwnershipAcquireEvent>(out var acquired))
            {
                state = ApplyAcquire(state, acquired);
                continue;
            }

            if (payload.TryUnpack<ProjectionOwnershipReleaseEvent>(out var released))
                state = ApplyRelease(state, released);
        }

        return state.Active &&
               string.Equals(state.ScopeId, scopeId, StringComparison.Ordinal) &&
               string.Equals(state.SessionId, sessionId, StringComparison.Ordinal) &&
               !IsOwnershipExpired(state, DateTime.UtcNow);
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
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(CoordinatorPublisherId, TopologyAudience.Self),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId,
            },
        };

    private static ProjectionOwnershipCoordinatorState ApplyAcquire(
        ProjectionOwnershipCoordinatorState current,
        ProjectionOwnershipAcquireEvent evt)
    {
        var next = current.Clone();
        next.ScopeId = evt.ScopeId;
        next.SessionId = evt.SessionId;
        next.Active = true;
        next.LastUpdatedAtUtc = NormalizeOccurredAt(evt.OccurredAtUtc);
        next.LeaseTtlMs = ProjectionOwnershipCoordinatorOptions.NormalizeLeaseTtlMs(evt.LeaseTtlMs);
        return next;
    }

    private static ProjectionOwnershipCoordinatorState ApplyRelease(
        ProjectionOwnershipCoordinatorState current,
        ProjectionOwnershipReleaseEvent evt)
    {
        if (!current.Active)
            return current;

        var next = current.Clone();
        next.Active = false;
        next.SessionId = string.Empty;
        next.LastUpdatedAtUtc = NormalizeOccurredAt(evt.OccurredAtUtc);
        return next;
    }

    private static bool IsOwnershipExpired(ProjectionOwnershipCoordinatorState state, DateTime utcNow)
    {
        if (!state.Active)
            return false;

        var lastUpdatedUtc = ResolveUtc(state.LastUpdatedAtUtc);
        var leaseTtlMs = ProjectionOwnershipCoordinatorOptions.NormalizeLeaseTtlMs(state.LeaseTtlMs);
        return utcNow - lastUpdatedUtc >= TimeSpan.FromMilliseconds(leaseTtlMs);
    }

    private static Timestamp NormalizeOccurredAt(Timestamp? occurredAtUtc) =>
        Timestamp.FromDateTime(ResolveUtc(occurredAtUtc));

    private static DateTime ResolveUtc(Timestamp? timestamp)
    {
        if (timestamp == null)
            return DateTime.UnixEpoch;

        return ResolveUtc(timestamp.ToDateTime());
    }

    private static DateTime ResolveUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
