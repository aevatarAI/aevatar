using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Projection.Orchestration;

/// <summary>
/// Actorized projection coordinator that serializes projection start/stop ownership per root actor.
/// </summary>
public sealed class WorkflowExecutionProjectionCoordinatorGAgent
    : GAgentBase<WorkflowExecutionProjectionCoordinatorState>
{
    public const string ActorIdPrefix = "projection";

    public static string BuildActorId(string rootActorId)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            throw new ArgumentException("Root actor id is required.", nameof(rootActorId));

        return $"{ActorIdPrefix}:{rootActorId.Trim()}";
    }

    [EventHandler]
    public Task HandleAcquireAsync(WorkflowExecutionProjectionAcquireEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RootActorId))
            throw new InvalidOperationException("Root actor id is required to acquire projection ownership.");
        if (string.IsNullOrWhiteSpace(evt.CommandId))
            throw new InvalidOperationException("Command id is required to acquire projection ownership.");

        if (State.Active)
        {
            throw new InvalidOperationException(
                $"Projection for actor '{State.RootActorId}' is already active (command '{State.CommandId}').");
        }

        State.RootActorId = evt.RootActorId;
        State.CommandId = evt.CommandId;
        State.Active = true;
        State.LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return Task.CompletedTask;
    }

    [EventHandler]
    public Task HandleReleaseAsync(WorkflowExecutionProjectionReleaseEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!State.Active)
            return Task.CompletedTask;

        if (!string.IsNullOrWhiteSpace(evt.RootActorId) &&
            !string.Equals(evt.RootActorId, State.RootActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projection coordinator '{Id}' root actor mismatch. expected='{State.RootActorId}', actual='{evt.RootActorId}'.");
        }

        if (!string.IsNullOrWhiteSpace(evt.CommandId) &&
            !string.Equals(evt.CommandId, State.CommandId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projection coordinator '{Id}' command mismatch. expected='{State.CommandId}', actual='{evt.CommandId}'.");
        }

        State.Active = false;
        State.CommandId = string.Empty;
        State.LastUpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
