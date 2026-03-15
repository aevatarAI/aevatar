using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ServiceServingSetManagerGAgent : GAgentBase<ServiceServingSetState>
{
    public ServiceServingSetManagerGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleReplaceAsync(ReplaceServiceServingTargetsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        ValidateTargets(command.Targets);

        await PersistDomainEventAsync(new ServiceServingSetUpdatedEvent
        {
            Identity = command.Identity?.Clone(),
            Generation = State.Generation + 1,
            Targets = { command.Targets.Select(CloneTarget) },
            RolloutId = command.RolloutId ?? string.Empty,
            Reason = command.Reason ?? string.Empty,
            UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override ServiceServingSetState TransitionState(ServiceServingSetState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceServingSetUpdatedEvent>(ApplyUpdated)
            .OrCurrent();

    private static ServiceServingSetState ApplyUpdated(ServiceServingSetState state, ServiceServingSetUpdatedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? new ServiceIdentity();
        next.Generation = evt.Generation;
        next.ActiveRolloutId = evt.RolloutId ?? string.Empty;
        next.Targets.Clear();
        next.Targets.Add(evt.Targets.Select(CloneTarget));
        next.UpdatedAt = evt.UpdatedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.Generation);
        return next;
    }

    private void EnsureIdentity(ServiceIdentity? identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service serving set '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service serving actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static void ValidateTargets(IEnumerable<ServiceServingTargetSpec> targets)
    {
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.DeploymentId))
                throw new InvalidOperationException("deployment_id is required.");
            if (string.IsNullOrWhiteSpace(target.RevisionId))
                throw new InvalidOperationException("revision_id is required.");
            if (string.IsNullOrWhiteSpace(target.PrimaryActorId))
                throw new InvalidOperationException("primary_actor_id is required.");
            if (target.AllocationWeight < 0)
                throw new InvalidOperationException("allocation_weight must be non-negative.");
        }
    }

    private static string BuildEventId(ServiceIdentity? identity, long generation)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:serving:{generation}";
    }

    private static ServiceServingTargetSpec CloneTarget(ServiceServingTargetSpec source) =>
        new()
        {
            DeploymentId = source.DeploymentId ?? string.Empty,
            RevisionId = source.RevisionId ?? string.Empty,
            PrimaryActorId = source.PrimaryActorId ?? string.Empty,
            AllocationWeight = source.AllocationWeight,
            ServingState = source.ServingState,
            EnabledEndpointIds = { source.EnabledEndpointIds },
        };
}
