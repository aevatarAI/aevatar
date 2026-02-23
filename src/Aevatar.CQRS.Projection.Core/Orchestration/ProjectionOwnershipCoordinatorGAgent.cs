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

        if (State.Active)
        {
            throw new InvalidOperationException(
                $"Projection ownership for scope '{State.ScopeId}' is already active (session '{State.SessionId}').");
        }

        await PersistDomainEventAsync(evt);
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

        await PersistDomainEventAsync(evt);
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
        next.LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }

    private static ProjectionOwnershipCoordinatorState ApplyRelease(
        ProjectionOwnershipCoordinatorState current,
        ProjectionOwnershipReleaseEvent evt)
    {
        _ = evt;
        if (!current.Active)
            return current;

        var next = current.Clone();
        next.Active = false;
        next.SessionId = string.Empty;
        next.LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return next;
    }
}
