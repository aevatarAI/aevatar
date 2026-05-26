using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GAgents.Registry;

/// <summary>
/// Per-scope registry actor that tracks all GAgent actor IDs grouped by type.
/// Replaces the chrono-storage backed <c>ChronoStorageGAgentActorStore</c>.
///
/// Actor ID: <c>gagent-registry-{scopeId}</c> (per-scope).
/// </summary>
public sealed class GAgentRegistryGAgent : GAgentBase<GAgentRegistryState>, IProjectedActor
{
    public static string ProjectionKind => "gagent-registry";


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
    }

    [EventHandler(EndpointName = "authorizeScopeResource")]
    public Task HandleScopeResourceAdmissionRequested(ScopeResourceAdmissionRequested request)
    {
        if (string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.GagentType) ||
            string.IsNullOrWhiteSpace(request.ActorId))
            throw new GAgentRegistryAdmissionNotFoundException();

        var group = State.Groups.FirstOrDefault(g =>
            string.Equals(g.GagentType, request.GagentType, StringComparison.Ordinal));
        if (group is null || !group.ActorIds.Contains(request.ActorId))
            throw new GAgentRegistryAdmissionNotFoundException();

        return Task.CompletedTask;
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
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
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

}

public sealed class GAgentRegistryAdmissionNotFoundException : Exception
{
    public GAgentRegistryAdmissionNotFoundException()
        : base("Registry target was not found.")
    {
    }
}
