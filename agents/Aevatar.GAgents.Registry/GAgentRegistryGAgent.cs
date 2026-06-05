using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Registry;

/// <summary>
/// Per-scope registry actor that tracks all GAgent actor IDs grouped by canonical AgentKind.
/// Replaces the chrono-storage backed <c>ChronoStorageGAgentActorStore</c>.
///
/// Actor ID: <c>gagent-registry-{scopeId}</c> (per-scope).
/// </summary>
[GAgent("gagent.registry")]
public sealed class GAgentRegistryGAgent : GAgentBase<GAgentRegistryState>, IProjectedActor
{
    public static string ProjectionKind => "gagent-registry";


    [EventHandler(EndpointName = "registerActor")]
    public async Task HandleActorRegistered(ActorRegisteredEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.AgentKind) || string.IsNullOrWhiteSpace(evt.ActorId))
            return;

        if (!IsRegisteredAgentKind(evt.AgentKind))
            return;

        var group = State.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, evt.AgentKind, StringComparison.Ordinal));
        var removedLegacyKeys = FindLegacyGroupsContainingActor(evt.AgentKind, evt.ActorId);
        if (group is not null && group.ActorIds.Contains(evt.ActorId) && removedLegacyKeys.Count == 0)
            return;

        var committed = evt.Clone();
        committed.RemovedLegacyKeys.Clear();
        committed.RemovedLegacyKeys.AddRange(removedLegacyKeys);

        await PersistDomainEventAsync(committed);
    }

    [EventHandler(EndpointName = "authorizeScopeResource")]
    public async Task HandleScopeResourceAdmissionRequested(ScopeResourceAdmissionRequested request)
    {
        if (string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.AgentKind) ||
            string.IsNullOrWhiteSpace(request.ActorId))
            throw new GAgentRegistryAdmissionNotFoundException();

        if (!IsRegisteredAgentKind(request.AgentKind))
            throw new GAgentRegistryAdmissionNotFoundException();

        var group = State.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, request.AgentKind, StringComparison.Ordinal));
        if (group is not null && group.ActorIds.Contains(request.ActorId))
            return;

        if (await TryCanonicalizeLegacyRegistrationAsync(request))
            return;

        throw new GAgentRegistryAdmissionNotFoundException();
    }

    private async Task<bool> TryCanonicalizeLegacyRegistrationAsync(ScopeResourceAdmissionRequested request)
    {
        var legacyCandidates = new List<GAgentRegistryEntry>();
        foreach (var candidate in State.Groups)
        {
            if (!candidate.ActorIds.Contains(request.ActorId))
                continue;

            if (IsRegisteredAgentKind(candidate.AgentKind))
                continue;

            legacyCandidates.Add(candidate);
        }

        if (legacyCandidates.Count != 1)
        {
            LogUnmappableLegacyRows(request, legacyCandidates);
            return false;
        }

        var legacy = legacyCandidates[0];
        var probe = Services.GetService<IActorKindProbe>();
        if (probe is null)
        {
            LogUnmappableLegacyRow(request, legacy.AgentKind);
            return false;
        }

        string? runtimeKind;
        try
        {
            runtimeKind = await probe.GetRuntimeAgentKindAsync(request.ActorId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "GAgent registry legacy row could not be canonicalized for scope {ScopeId}, actor {ActorId}, previous key {PreviousRegistryKey}",
                request.ScopeId,
                request.ActorId,
                legacy.AgentKind);
            return false;
        }

        if (!string.Equals(runtimeKind, request.AgentKind, StringComparison.Ordinal))
        {
            LogUnmappableLegacyRow(request, legacy.AgentKind);
            return false;
        }

        await PersistDomainEventAsync(new ActorRegistrationKeyCanonicalizedEvent
        {
            PreviousRegistryKey = legacy.AgentKind,
            AgentKind = request.AgentKind,
            ActorId = request.ActorId,
        });
        return true;
    }

    private void LogUnmappableLegacyRows(
        ScopeResourceAdmissionRequested request,
        IReadOnlyList<GAgentRegistryEntry> legacyCandidates)
    {
        if (legacyCandidates.Count == 0)
            throw new GAgentRegistryAdmissionNotFoundException();

        foreach (var legacy in legacyCandidates)
            LogUnmappableLegacyRow(request, legacy.AgentKind);
    }

    private void LogUnmappableLegacyRow(
        ScopeResourceAdmissionRequested request,
        string previousRegistryKey)
    {
        Logger.LogWarning(
            "GAgent registry legacy row is quarantined for scope {ScopeId}, actor {ActorId}, previous key {PreviousRegistryKey}",
            request.ScopeId,
            request.ActorId,
            previousRegistryKey);
    }

    [EventHandler(EndpointName = "unregisterActor")]
    public async Task HandleActorUnregistered(ActorUnregisteredEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.AgentKind) || string.IsNullOrWhiteSpace(evt.ActorId))
            return;

        if (!IsRegisteredAgentKind(evt.AgentKind))
            return;

        var group = State.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, evt.AgentKind, StringComparison.Ordinal));
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
            .On<ActorRegistrationKeyCanonicalizedEvent>(ApplyKeyCanonicalized)
            .OrCurrent();
    }

    private static GAgentRegistryState ApplyRegistered(
        GAgentRegistryState state, ActorRegisteredEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, evt.AgentKind, StringComparison.Ordinal));

        if (group is null)
        {
            group = new GAgentRegistryEntry { AgentKind = evt.AgentKind };
            next.Groups.Add(group);
        }

        if (!group.ActorIds.Contains(evt.ActorId))
            group.ActorIds.Add(evt.ActorId);

        foreach (var removedLegacyKey in evt.RemovedLegacyKeys.Distinct(StringComparer.Ordinal))
            RemoveActorFromGroup(next, removedLegacyKey, evt.ActorId);

        return next;
    }

    private static GAgentRegistryState ApplyUnregistered(
        GAgentRegistryState state, ActorUnregisteredEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, evt.AgentKind, StringComparison.Ordinal));

        if (group is null)
            return next;

        group.ActorIds.Remove(evt.ActorId);

        if (group.ActorIds.Count == 0)
            next.Groups.Remove(group);

        return next;
    }

    private static GAgentRegistryState ApplyKeyCanonicalized(
        GAgentRegistryState state,
        ActorRegistrationKeyCanonicalizedEvent evt)
    {
        var next = state.Clone();
        var group = next.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, evt.AgentKind, StringComparison.Ordinal));

        if (group is null)
        {
            group = new GAgentRegistryEntry { AgentKind = evt.AgentKind };
            next.Groups.Add(group);
        }

        if (!group.ActorIds.Contains(evt.ActorId))
            group.ActorIds.Add(evt.ActorId);

        RemoveActorFromGroup(next, evt.PreviousRegistryKey, evt.ActorId);
        return next;
    }

    private static void RemoveActorFromGroup(
        GAgentRegistryState state,
        string registryKey,
        string actorId)
    {
        var group = state.Groups.FirstOrDefault(g =>
            string.Equals(g.AgentKind, registryKey, StringComparison.Ordinal));
        if (group is null)
            return;

        group.ActorIds.Remove(actorId);
        if (group.ActorIds.Count == 0)
            state.Groups.Remove(group);
    }

    private bool IsRegisteredAgentKind(string? agentKind)
    {
        var normalized = agentKind?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var registry = Services.GetService<IAgentKindRegistry>();
        return registry?.TryResolve(normalized, out _) == true;
    }

    private IReadOnlyList<string> FindLegacyGroupsContainingActor(
        string agentKind,
        string actorId)
    {
        var legacyKeys = new List<string>();
        foreach (var group in State.Groups)
        {
            if (string.Equals(group.AgentKind, agentKind, StringComparison.Ordinal))
                continue;

            if (!group.ActorIds.Contains(actorId))
                continue;

            if (IsRegisteredAgentKind(group.AgentKind))
                continue;

            legacyKeys.Add(group.AgentKind);
        }

        return legacyKeys;
    }
}

public sealed class GAgentRegistryAdmissionNotFoundException : Exception
{
    public GAgentRegistryAdmissionNotFoundException()
        : base("Registry target was not found.")
    {
    }
}
