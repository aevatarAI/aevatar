using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Actorized ownership coordinator that serializes projection acquire/release by scope.
/// </summary>
public sealed class ProjectionOwnershipCoordinatorGAgent
    : GAgentBase<ProjectionOwnershipCoordinatorState>
{
    public const string ActorIdPrefix = "projection";

    public static string BuildActorId(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("Scope id is required.", nameof(scopeId));

        return $"{ActorIdPrefix}:{scopeId.Trim()}";
    }

    [EventHandler]
    public async Task HandleAcquireAsync(ProjectionOwnershipAcquireEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.ScopeId))
            throw new InvalidOperationException("Scope id is required to acquire projection ownership.");
        if (string.IsNullOrWhiteSpace(evt.SessionId))
            throw new InvalidOperationException("Session id is required to acquire projection ownership.");

        var normalizedLeaseTtlMs = ProjectionOwnershipCoordinatorOptions.NormalizeLeaseTtlMs(evt.LeaseTtlMs);
        var nowUtc = DateTime.UtcNow;
        var acquireEvent = new ProjectionOwnershipAcquireEvent
        {
            ScopeId = evt.ScopeId,
            SessionId = evt.SessionId,
            LeaseTtlMs = normalizedLeaseTtlMs,
            OccurredAtUtc = ResolveOccurredAtForPersist(evt.OccurredAtUtc, nowUtc),
        };

        if (State.Active)
        {
            if (!string.Equals(State.ScopeId, evt.ScopeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Projection ownership coordinator '{Id}' scope mismatch. expected='{State.ScopeId}', actual='{evt.ScopeId}'.");
            }

            if (string.Equals(State.SessionId, evt.SessionId, StringComparison.Ordinal))
            {
                // Same session acquire is treated as lease renewal.
                await PersistDomainEventAsync(acquireEvent);
                return;
            }

            if (!IsOwnershipExpired(State, DateTime.UtcNow))
            {
                throw new InvalidOperationException(
                    $"Projection ownership for scope '{State.ScopeId}' is already active (session '{State.SessionId}').");
            }
        }

        await PersistDomainEventAsync(acquireEvent);
    }

    [EventHandler]
    public async Task HandleReleaseAsync(ProjectionOwnershipReleaseEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!State.Active)
            return;

        if (!string.IsNullOrWhiteSpace(evt.ScopeId) &&
            !string.Equals(evt.ScopeId, State.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projection ownership coordinator '{Id}' scope mismatch. expected='{State.ScopeId}', actual='{evt.ScopeId}'.");
        }

        if (!string.IsNullOrWhiteSpace(evt.SessionId) &&
            !string.Equals(evt.SessionId, State.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projection ownership coordinator '{Id}' session mismatch. expected='{State.SessionId}', actual='{evt.SessionId}'.");
        }

        var releaseEvent = new ProjectionOwnershipReleaseEvent
        {
            ScopeId = evt.ScopeId,
            SessionId = evt.SessionId,
            OccurredAtUtc = ResolveOccurredAtForPersist(evt.OccurredAtUtc, DateTime.UtcNow),
        };

        await PersistDomainEventAsync(releaseEvent);
    }

    protected override ProjectionOwnershipCoordinatorState TransitionState(
        ProjectionOwnershipCoordinatorState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ProjectionOwnershipAcquireEvent>(ApplyAcquire)
            .On<ProjectionOwnershipReleaseEvent>(ApplyRelease)
            .OrCurrent();

    private static ProjectionOwnershipCoordinatorState ApplyAcquire(
        ProjectionOwnershipCoordinatorState current,
        ProjectionOwnershipAcquireEvent evt)
    {
        var next = current.Clone();
        next.ScopeId = evt.ScopeId;
        next.SessionId = evt.SessionId;
        next.Active = true;
        next.LastUpdatedAtUtc = RequireOccurredAtUtc(evt.OccurredAtUtc, nameof(ProjectionOwnershipAcquireEvent));
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
        next.LastUpdatedAtUtc = RequireOccurredAtUtc(evt.OccurredAtUtc, nameof(ProjectionOwnershipReleaseEvent));
        return next;
    }

    private static bool IsOwnershipExpired(ProjectionOwnershipCoordinatorState state, DateTime utcNow)
    {
        if (!state.Active)
            return false;

        var lastUpdatedUtc = ResolveLastUpdatedUtc(state.LastUpdatedAtUtc);
        var leaseTtlMs = ProjectionOwnershipCoordinatorOptions.NormalizeLeaseTtlMs(state.LeaseTtlMs);
        var leaseDuration = TimeSpan.FromMilliseconds(leaseTtlMs);
        return utcNow - lastUpdatedUtc >= leaseDuration;
    }

    private static DateTime ResolveLastUpdatedUtc(Timestamp? timestamp)
    {
        if (timestamp == null)
            return DateTime.UnixEpoch;

        var lastUpdatedUtc = timestamp.ToDateTime();
        return lastUpdatedUtc.Kind == DateTimeKind.Utc
            ? lastUpdatedUtc
            : DateTime.SpecifyKind(lastUpdatedUtc, DateTimeKind.Utc);
    }

    private static Timestamp ResolveOccurredAtForPersist(Timestamp? occurredAtUtc, DateTime fallbackUtcNow)
    {
        if (occurredAtUtc != null)
            return NormalizeTimestamp(occurredAtUtc);

        return Timestamp.FromDateTime(EnsureUtc(fallbackUtcNow));
    }

    private static Timestamp RequireOccurredAtUtc(Timestamp? occurredAtUtc, string eventName) =>
        occurredAtUtc == null
            ? throw new InvalidOperationException($"{eventName} must include occurred_at_utc.")
            : NormalizeTimestamp(occurredAtUtc);

    private static Timestamp NormalizeTimestamp(Timestamp timestamp) =>
        Timestamp.FromDateTime(EnsureUtc(timestamp.ToDateTime()));

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
