using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
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
    public Task HandleAcquireAsync(ProjectionOwnershipAcquireEvent evt)
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

        State.ScopeId = evt.ScopeId;
        State.SessionId = evt.SessionId;
        State.Active = true;
        State.LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return Task.CompletedTask;
    }

    [EventHandler]
    public Task HandleReleaseAsync(ProjectionOwnershipReleaseEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!State.Active)
            return Task.CompletedTask;

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

        State.Active = false;
        State.SessionId = string.Empty;
        State.LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
