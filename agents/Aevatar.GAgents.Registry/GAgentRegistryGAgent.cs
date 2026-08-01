using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
    public const int MaxRetainedUnregistrationOperations = 256;

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

    [EventHandler(EndpointName = "unregisterActorOperation")]
    public async Task HandleGAgentRegistryUnregistrationRequested(
        GAgentRegistryUnregistrationRequest request)
    {
        var normalized = NormalizeUnregistrationRequest(request);
        if (normalized is null)
            return;

        if (State.UnregistrationOperations.TryGetValue(normalized.OperationId, out var existing))
        {
            if (MatchesUnregistrationOperation(existing, normalized))
                await DispatchUnregistrationCompletionAsync(existing).ConfigureAwait(false);
            return;
        }

        if (!CanAdmitUnregistrationOperation())
        {
            throw new InvalidOperationException(
                "GAgent registry unregistration retention capacity is exhausted by pending completions.");
        }

        var group = State.Groups.FirstOrDefault(candidate =>
            string.Equals(candidate.AgentKind, normalized.AgentKind, StringComparison.Ordinal));
        var outcome = group?.ActorIds.Contains(normalized.ActorId) == true
            ? GAgentRegistryUnregistrationOutcome.CommittedRemoved
            : GAgentRegistryUnregistrationOutcome.AuthoritativeAbsent;
        var committed = new GAgentRegistryUnregistrationCommittedEvent
        {
            OperationId = normalized.OperationId,
            RegistryActorId = normalized.RegistryActorId,
            ScopeId = normalized.ScopeId,
            AgentKind = normalized.AgentKind,
            ActorId = normalized.ActorId,
            CompletionActorId = normalized.CompletionActorId,
            Outcome = outcome,
            CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await PersistDomainEventAsync(committed).ConfigureAwait(false);
        await DispatchUnregistrationCompletionAsync(
                State.UnregistrationOperations[normalized.OperationId])
            .ConfigureAwait(false);
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
            .On<GAgentRegistryUnregistrationCommittedEvent>(ApplyUnregistrationCommitted)
            .On<GAgentRegistryUnregistrationCompletionDispatchAcceptedEvent>(
                ApplyUnregistrationCompletionDispatchAccepted)
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

    private static GAgentRegistryState ApplyUnregistrationCommitted(
        GAgentRegistryState state,
        GAgentRegistryUnregistrationCommittedEvent evt)
    {
        var next = state.Clone();
        if (evt.Outcome == GAgentRegistryUnregistrationOutcome.CommittedRemoved)
            RemoveActorFromGroup(next, evt.AgentKind, evt.ActorId);

        next.UnregistrationOperations[evt.OperationId] = new GAgentRegistryUnregistrationOperation
        {
            OperationId = evt.OperationId,
            RegistryActorId = evt.RegistryActorId,
            ScopeId = evt.ScopeId,
            AgentKind = evt.AgentKind,
            ActorId = evt.ActorId,
            CompletionActorId = evt.CompletionActorId,
            Outcome = evt.Outcome,
            CompletedAt = evt.CompletedAt?.Clone(),
        };
        if (!next.UnregistrationOperationOrder.Contains(evt.OperationId))
            next.UnregistrationOperationOrder.Add(evt.OperationId);
        CompactUnregistrationOperations(next);
        return next;
    }

    private static GAgentRegistryState ApplyUnregistrationCompletionDispatchAccepted(
        GAgentRegistryState state,
        GAgentRegistryUnregistrationCompletionDispatchAcceptedEvent evt)
    {
        var next = state.Clone();
        if (!next.UnregistrationOperations.TryGetValue(evt.OperationId, out var operation))
            throw new InvalidOperationException(
                "GAgent registry completion dispatch references an unknown unregistration operation.");

        operation.CompletionDispatchAcceptedAt ??= evt.AcceptedAt?.Clone();
        CompactUnregistrationOperations(next);
        return next;
    }

    private static void CompactUnregistrationOperations(GAgentRegistryState state)
    {
        NormalizeUnregistrationOperationOrder(state);
        while (state.UnregistrationOperations.Count > MaxRetainedUnregistrationOperations)
        {
            var removableIndex = -1;
            for (var index = 0; index < state.UnregistrationOperationOrder.Count; index++)
            {
                var operationId = state.UnregistrationOperationOrder[index];
                if (state.UnregistrationOperations.TryGetValue(operationId, out var operation) &&
                    operation.CompletionDispatchAcceptedAt is not null)
                {
                    removableIndex = index;
                    break;
                }
            }

            if (removableIndex < 0)
            {
                throw new InvalidOperationException(
                    "GAgent registry unregistration retention capacity is exhausted by pending completions.");
            }

            var removableOperationId = state.UnregistrationOperationOrder[removableIndex];
            state.UnregistrationOperationOrder.RemoveAt(removableIndex);
            state.UnregistrationOperations.Remove(removableOperationId);
        }
    }

    private static void NormalizeUnregistrationOperationOrder(GAgentRegistryState state)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < state.UnregistrationOperationOrder.Count;)
        {
            var operationId = state.UnregistrationOperationOrder[index];
            if (!state.UnregistrationOperations.ContainsKey(operationId) || !seen.Add(operationId))
            {
                state.UnregistrationOperationOrder.RemoveAt(index);
                continue;
            }

            index++;
        }

        var missing = state.UnregistrationOperations
            .Where(entry => !seen.Contains(entry.Key))
            .OrderBy(entry => entry.Value.CompletedAt?.Seconds ?? long.MinValue)
            .ThenBy(entry => entry.Value.CompletedAt?.Nanos ?? int.MinValue)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key);
        state.UnregistrationOperationOrder.Add(missing);
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

    private GAgentRegistryUnregistrationRequest? NormalizeUnregistrationRequest(
        GAgentRegistryUnregistrationRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.OperationId) ||
            string.IsNullOrWhiteSpace(request.RegistryActorId) ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            string.IsNullOrWhiteSpace(request.AgentKind) ||
            string.IsNullOrWhiteSpace(request.ActorId) ||
            string.IsNullOrWhiteSpace(request.CompletionActorId))
        {
            return null;
        }

        var normalized = request.Clone();
        normalized.OperationId = normalized.OperationId.Trim();
        normalized.RegistryActorId = normalized.RegistryActorId.Trim();
        normalized.ScopeId = normalized.ScopeId.Trim();
        normalized.AgentKind = normalized.AgentKind.Trim();
        normalized.ActorId = normalized.ActorId.Trim();
        normalized.CompletionActorId = normalized.CompletionActorId.Trim();

        if (!string.Equals(Id, normalized.RegistryActorId, StringComparison.Ordinal) ||
            !string.Equals(
                normalized.RegistryActorId,
                GAgentRegistryActorIds.ForScope(normalized.ScopeId),
                StringComparison.Ordinal) ||
            !string.Equals(
                normalized.ActorId,
                normalized.CompletionActorId,
                StringComparison.Ordinal) ||
            !IsRegisteredAgentKind(normalized.AgentKind))
        {
            return null;
        }

        return normalized;
    }

    private static bool MatchesUnregistrationOperation(
        GAgentRegistryUnregistrationOperation operation,
        GAgentRegistryUnregistrationRequest request) =>
        string.Equals(operation.OperationId, request.OperationId, StringComparison.Ordinal) &&
        string.Equals(operation.RegistryActorId, request.RegistryActorId, StringComparison.Ordinal) &&
        string.Equals(operation.ScopeId, request.ScopeId, StringComparison.Ordinal) &&
        string.Equals(operation.AgentKind, request.AgentKind, StringComparison.Ordinal) &&
        string.Equals(operation.ActorId, request.ActorId, StringComparison.Ordinal) &&
        string.Equals(operation.CompletionActorId, request.CompletionActorId, StringComparison.Ordinal);

    private bool CanAdmitUnregistrationOperation() =>
        State.UnregistrationOperations.Count < MaxRetainedUnregistrationOperations ||
        State.UnregistrationOperations.Values.Any(operation =>
            operation.CompletionDispatchAcceptedAt is not null);

    private async Task DispatchUnregistrationCompletionAsync(
        GAgentRegistryUnregistrationOperation operation)
    {
        if (operation.CompletedAt is null)
            return;

        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (dispatchPort is null)
            return;

        var completion = new GAgentRegistryUnregistrationCompleted
        {
            OperationId = operation.OperationId,
            RegistryActorId = operation.RegistryActorId,
            ScopeId = operation.ScopeId,
            AgentKind = operation.AgentKind,
            ActorId = operation.ActorId,
            CompletionActorId = operation.CompletionActorId,
            Outcome = operation.Outcome,
            CompletedAt = operation.CompletedAt.Clone(),
        };
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(completion),
            Route = EnvelopeRouteSemantics.CreateDirect(Id, operation.CompletionActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = operation.OperationId,
            },
        };
        var admission = await dispatchPort.DispatchAsync(
                operation.CompletionActorId,
                envelope,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!admission.Accepted)
            throw new InvalidOperationException("GAgent registry unregistration completion dispatch was rejected.");

        if (operation.CompletionDispatchAcceptedAt is null)
        {
            await PersistDomainEventAsync(new GAgentRegistryUnregistrationCompletionDispatchAcceptedEvent
            {
                OperationId = operation.OperationId,
                AcceptedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }).ConfigureAwait(false);
        }
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
