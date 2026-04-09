using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Registry;

/// <summary>
/// Per-scope registry actor that tracks all GAgent actor IDs grouped by type.
/// Replaces the chrono-storage backed <c>ChronoStorageGAgentActorStore</c>.
///
/// Actor ID: <c>gagent-registry-{scopeId}</c> (per-scope).
///
/// After each state change, pushes the current state to the paired
/// <see cref="GAgentRegistryReadModelGAgent"/> via <c>SendToAsync</c>.
/// </summary>
public sealed class GAgentRegistryGAgent : GAgentBase<GAgentRegistryState>
{
    [EventHandler(EndpointName = "registerActor")]
    public async Task HandleActorRegistered(ActorRegisteredEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.GagentType) || string.IsNullOrWhiteSpace(evt.ActorId))
            return;

        // Idempotent: skip if already registered
        var group = State.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, evt.GagentType, StringComparison.Ordinal));
        if (group is not null && group.ActorIds.Contains(evt.ActorId))
            return;

        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    [EventHandler(EndpointName = "unregisterActor")]
    public async Task HandleActorUnregistered(ActorUnregisteredEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.GagentType) || string.IsNullOrWhiteSpace(evt.ActorId))
            return;

        // Idempotent: skip if not registered
        var group = State.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, evt.GagentType, StringComparison.Ordinal));
        if (group is null || !group.ActorIds.Contains(evt.ActorId))
            return;

        await PersistDomainEventAsync(evt);
        await PushToReadModelAsync();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await PushToReadModelAsync();
    }

    protected override GAgentRegistryState TransitionState(
        GAgentRegistryState current, IMessage evt)
    {
        return StateTransitionMatcher
            .Match(current, evt)
            .On<ActorRegisteredEvent>(ApplyRegistered)
            .On<ActorUnregisteredEvent>(ApplyUnregistered)
            .OrCurrent();
    }

    private static GAgentRegistryState ApplyRegistered(
        GAgentRegistryState state, ActorRegisteredEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, evt.GagentType, StringComparison.Ordinal));

        if (group is null)
        {
            group = new GAgentRegistryEntry { GagentType = evt.GagentType };
            next.Groups.Add(group);
        }

        if (!group.ActorIds.Contains(evt.ActorId))
            group.ActorIds.Add(evt.ActorId);

        return next;
    }

    private static GAgentRegistryState ApplyUnregistered(
        GAgentRegistryState state, ActorUnregisteredEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, evt.GagentType, StringComparison.Ordinal));

        if (group is null)
            return next;

        group.ActorIds.Remove(evt.ActorId);

        if (group.ActorIds.Count == 0)
            next.Groups.Remove(group);

        return next;
    }

    private async Task PushToReadModelAsync()
    {
        var readModelActorId = Id + "-readmodel";
        var update = new GAgentRegistryReadModelUpdateEvent { Snapshot = State.Clone() };
        await SendToAsync(readModelActorId, update);
    }
}
