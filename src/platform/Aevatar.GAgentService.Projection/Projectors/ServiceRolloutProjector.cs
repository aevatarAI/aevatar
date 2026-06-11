using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Internal;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ServiceRolloutProjector
    : IProjectionArtifactMaterializer<ServiceRolloutProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ServiceRolloutReadModel> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public ServiceRolloutProjector(
        IProjectionWriteDispatcher<ServiceRolloutReadModel> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    //   Old pattern: Service artifact projectors injected document reader and incrementally mutated prior readmodel state.
    //   New principle: 服务投影器仅做 state-root overwrite; catalog definition-only, deployment/serving facts come from their readmodels.
    //   No new actor, envelope kind, projection phase, layer, or docs/canon change.
    public async ValueTask ProjectAsync(ServiceRolloutProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!ServiceCommittedStateSupport.TryGetObservedState<ServiceRolloutExecutionState>(
                envelope,
                _clock,
                out var state,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            state?.Identity == null)
        {
            return;
        }

        var serviceKey = ServiceProjectionMapping.ServiceKey(state.Identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var readModel = new ServiceRolloutReadModel
        {
            Id = serviceKey,
            ActorId = context.RootActorId,
            StateVersion = stateVersion,
            LastEventId = eventId,
            RolloutId = state.RolloutId ?? state.Plan?.RolloutId ?? string.Empty,
            DisplayName = state.Plan?.DisplayName ?? string.Empty,
            Status = state.Status.ToString(),
            CurrentStageIndex = state.CurrentStageIndex,
            FailureReason = state.FailureReason ?? string.Empty,
            StartedAt = state.StartedAt?.ToDateTimeOffset(),
            UpdatedAt = observedAt,
            BaselineTargets = state.BaselineTargets
                .Select(ServiceProjectionMapping.ToServingTargetReadModel)
                .ToList(),
            Stages = (state.Plan?.Stages ?? [])
                .Select((stage, index) => new ServiceRolloutStageReadModel
                {
                    StageId = stage.StageId ?? string.Empty,
                    StageIndex = index,
                    Targets = stage.Targets.Select(ServiceProjectionMapping.ToServingTargetReadModel).ToList(),
                })
                .OrderBy(x => x.StageIndex)
                .ToList(),
        };
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }
}
