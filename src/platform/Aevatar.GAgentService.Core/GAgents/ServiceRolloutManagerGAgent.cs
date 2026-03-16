using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ServiceRolloutManagerGAgent : GAgentBase<ServiceRolloutExecutionState>
{
    private readonly IActorDispatchPort _dispatchPort;

    public ServiceRolloutManagerGAgent(IActorDispatchPort dispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleStartAsync(StartServiceRolloutCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureIdentity(command.Identity, allowInitialize: true);
        ValidatePlan(command.Plan);
        if (HasActiveRollout())
            throw new InvalidOperationException("An active rollout already exists for this service.");

        var startedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        await PersistDomainEventAsync(new ServiceRolloutStartedEvent
        {
            Identity = command.Identity?.Clone(),
            Plan = ClonePlan(command.Plan),
            BaselineTargets = { command.BaselineTargets.Select(CloneTarget) },
            StartedAt = startedAt,
        });

        await ApplyStageAsync(command.Identity!, command.Plan, 0, command.Plan.Stages[0], CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleAdvanceAsync(AdvanceServiceRolloutCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActiveRollout(command.Identity, command.RolloutId);
        var currentStatus = State.Status;
        if (currentStatus == ServiceRolloutStatus.Paused)
            throw new InvalidOperationException("Rollout is paused.");
        if (currentStatus is ServiceRolloutStatus.Completed or ServiceRolloutStatus.RolledBack or ServiceRolloutStatus.Failed)
            throw new InvalidOperationException("Rollout is already finalized.");

        var currentPlan = State.Plan;
        var nextIndex = State.CurrentStageIndex + 1;
        if (nextIndex >= currentPlan.Stages.Count)
            throw new InvalidOperationException("No rollout stages remain.");

        await ApplyStageAsync(command.Identity!, currentPlan, nextIndex, currentPlan.Stages[nextIndex], CancellationToken.None);
    }

    [EventHandler]
    public async Task HandlePauseAsync(PauseServiceRolloutCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActiveRollout(command.Identity, command.RolloutId);
        var currentStatus = State.Status;
        if (currentStatus != ServiceRolloutStatus.InProgress)
            return;

        await PersistDomainEventAsync(new ServiceRolloutPausedEvent
        {
            Identity = command.Identity?.Clone(),
            RolloutId = command.RolloutId ?? string.Empty,
            Reason = command.Reason ?? string.Empty,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleResumeAsync(ResumeServiceRolloutCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActiveRollout(command.Identity, command.RolloutId);
        var currentStatus = State.Status;
        if (currentStatus != ServiceRolloutStatus.Paused)
            return;

        await PersistDomainEventAsync(new ServiceRolloutResumedEvent
        {
            Identity = command.Identity?.Clone(),
            RolloutId = command.RolloutId ?? string.Empty,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleRollbackAsync(RollbackServiceRolloutCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureActiveRollout(command.Identity, command.RolloutId);
        var currentStatus = State.Status;
        if (currentStatus is ServiceRolloutStatus.Completed or ServiceRolloutStatus.RolledBack)
            return;

        var rolloutId = State.RolloutId;
        var baselineTargets = State.BaselineTargets.Select(CloneTarget).ToArray();
        await DispatchServingTargetsAsync(
            command.Identity!,
            rolloutId,
            command.Reason ?? "rollback",
            baselineTargets,
            CancellationToken.None);

        await PersistDomainEventAsync(new ServiceRolloutRolledBackEvent
        {
            Identity = command.Identity?.Clone(),
            RolloutId = command.RolloutId ?? string.Empty,
            Targets = { baselineTargets },
            Reason = command.Reason ?? string.Empty,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override ServiceRolloutExecutionState TransitionState(ServiceRolloutExecutionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceRolloutStartedEvent>(ApplyStarted)
            .On<ServiceRolloutStageAdvancedEvent>(ApplyStageAdvanced)
            .On<ServiceRolloutPausedEvent>(ApplyPaused)
            .On<ServiceRolloutResumedEvent>(ApplyResumed)
            .On<ServiceRolloutCompletedEvent>(ApplyCompleted)
            .On<ServiceRolloutRolledBackEvent>(ApplyRolledBack)
            .On<ServiceRolloutFailedEvent>(ApplyFailed)
            .OrCurrent();

    private async Task ApplyStageAsync(
        ServiceIdentity identity,
        ServiceRolloutPlanSpec plan,
        int stageIndex,
        ServiceRolloutStageSpec stage,
        CancellationToken ct)
    {
        try
        {
            await DispatchServingTargetsAsync(identity, plan.RolloutId, $"stage:{stage.StageId}", stage.Targets, ct);
        }
        catch (Exception ex)
        {
            await PersistDomainEventAsync(new ServiceRolloutFailedEvent
            {
                Identity = identity.Clone(),
                RolloutId = plan.RolloutId ?? string.Empty,
                FailureReason = ex.Message,
                OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
            });
            return;
        }

        await PersistDomainEventAsync(new ServiceRolloutStageAdvancedEvent
        {
            Identity = identity.Clone(),
            RolloutId = plan.RolloutId ?? string.Empty,
            StageIndex = stageIndex,
            StageId = stage.StageId ?? string.Empty,
            Targets = { stage.Targets.Select(CloneTarget) },
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        if (stageIndex == plan.Stages.Count - 1)
        {
            await PersistDomainEventAsync(new ServiceRolloutCompletedEvent
            {
                Identity = identity.Clone(),
                RolloutId = plan.RolloutId ?? string.Empty,
                OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }
    }

    private async Task DispatchServingTargetsAsync(
        ServiceIdentity identity,
        string rolloutId,
        string reason,
        IEnumerable<ServiceServingTargetSpec> targets,
        CancellationToken ct)
    {
        var actorId = ServiceActorIds.ServingSet(identity);
        await _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                new ReplaceServiceServingTargetsCommand
                {
                    Identity = identity.Clone(),
                    Targets = { targets.Select(CloneTarget) },
                    RolloutId = rolloutId ?? string.Empty,
                    Reason = reason ?? string.Empty,
                }),
            ct);
    }

    private bool HasActiveRollout() =>
        !string.IsNullOrWhiteSpace(State.RolloutId) &&
        State.Status is ServiceRolloutStatus.InProgress or ServiceRolloutStatus.Paused;

    private void EnsureIdentity(ServiceIdentity? identity, bool allowInitialize)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Identity?.Clone();
        if (currentIdentity == null || string.IsNullOrWhiteSpace(currentIdentity.ServiceId))
        {
            if (allowInitialize)
                return;

            throw new InvalidOperationException($"Service rollout '{requested}' does not exist.");
        }

        var existing = ServiceKeys.Build(currentIdentity);
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service rollout actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private void EnsureActiveRollout(ServiceIdentity? identity, string rolloutId)
    {
        EnsureIdentity(identity, allowInitialize: false);
        if (string.IsNullOrWhiteSpace(rolloutId))
            throw new InvalidOperationException("rollout_id is required.");
        if (!string.Equals(State.RolloutId, rolloutId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Rollout '{rolloutId}' does not match active rollout '{State.RolloutId}'.");
    }

    private static void ValidatePlan(ServiceRolloutPlanSpec? plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.RolloutId))
            throw new InvalidOperationException("rollout_id is required.");
        if (plan.Stages.Count == 0)
            throw new InvalidOperationException("at least one rollout stage is required.");

        foreach (var stage in plan.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.StageId))
                throw new InvalidOperationException("stage_id is required.");
            if (stage.Targets.Count == 0)
                throw new InvalidOperationException("rollout stage targets are required.");
        }
    }

    private static ServiceRolloutExecutionState ApplyStarted(ServiceRolloutExecutionState state, ServiceRolloutStartedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? new ServiceIdentity();
        next.RolloutId = evt.Plan?.RolloutId ?? string.Empty;
        next.Plan = ClonePlan(evt.Plan);
        next.BaselineTargets.Clear();
        next.BaselineTargets.Add(evt.BaselineTargets.Select(CloneTarget));
        next.Status = ServiceRolloutStatus.InProgress;
        next.CurrentStageIndex = -1;
        next.FailureReason = string.Empty;
        next.StartedAt = evt.StartedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.UpdatedAt = evt.StartedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.Plan?.RolloutId, "started");
        return next;
    }

    private static ServiceRolloutExecutionState ApplyStageAdvanced(ServiceRolloutExecutionState state, ServiceRolloutStageAdvancedEvent evt)
    {
        var next = state.Clone();
        next.Identity = evt.Identity?.Clone() ?? state.Identity?.Clone() ?? new ServiceIdentity();
        next.CurrentStageIndex = evt.StageIndex;
        next.Status = ServiceRolloutStatus.InProgress;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RolloutId, $"stage-{evt.StageIndex}");
        return next;
    }

    private static ServiceRolloutExecutionState ApplyPaused(ServiceRolloutExecutionState state, ServiceRolloutPausedEvent evt)
    {
        var next = state.Clone();
        next.Status = ServiceRolloutStatus.Paused;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RolloutId, "paused");
        return next;
    }

    private static ServiceRolloutExecutionState ApplyResumed(ServiceRolloutExecutionState state, ServiceRolloutResumedEvent evt)
    {
        var next = state.Clone();
        next.Status = ServiceRolloutStatus.InProgress;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RolloutId, "resumed");
        return next;
    }

    private static ServiceRolloutExecutionState ApplyCompleted(ServiceRolloutExecutionState state, ServiceRolloutCompletedEvent evt)
    {
        var next = state.Clone();
        next.Status = ServiceRolloutStatus.Completed;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RolloutId, "completed");
        return next;
    }

    private static ServiceRolloutExecutionState ApplyRolledBack(ServiceRolloutExecutionState state, ServiceRolloutRolledBackEvent evt)
    {
        var next = state.Clone();
        next.Status = ServiceRolloutStatus.RolledBack;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RolloutId, "rolled-back");
        return next;
    }

    private static ServiceRolloutExecutionState ApplyFailed(ServiceRolloutExecutionState state, ServiceRolloutFailedEvent evt)
    {
        var next = state.Clone();
        next.Status = ServiceRolloutStatus.Failed;
        next.FailureReason = evt.FailureReason ?? string.Empty;
        next.UpdatedAt = evt.OccurredAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, evt.RolloutId, "failed");
        return next;
    }

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.rollout", actorId),
            Propagation = new EnvelopePropagation(),
        };
    }

    private static string BuildEventId(ServiceIdentity? identity, string? rolloutId, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{rolloutId ?? "none"}:{suffix}";
    }

    private static ServiceRolloutPlanSpec ClonePlan(ServiceRolloutPlanSpec? source)
    {
        if (source == null)
            return new ServiceRolloutPlanSpec();

        var clone = new ServiceRolloutPlanSpec
        {
            RolloutId = source.RolloutId ?? string.Empty,
            DisplayName = source.DisplayName ?? string.Empty,
        };
        clone.Stages.Add(source.Stages.Select(CloneStage));
        return clone;
    }

    private static ServiceRolloutStageSpec CloneStage(ServiceRolloutStageSpec source)
    {
        return new ServiceRolloutStageSpec
        {
            StageId = source.StageId ?? string.Empty,
            Targets = { source.Targets.Select(CloneTarget) },
        };
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
