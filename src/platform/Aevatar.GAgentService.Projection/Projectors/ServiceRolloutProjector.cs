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
    private readonly IProjectionDocumentReader<ServiceRolloutReadModel, string> _documentReader;
    private readonly IProjectionClock _clock;

    public ServiceRolloutProjector(
        IProjectionWriteDispatcher<ServiceRolloutReadModel> storeDispatcher,
        IProjectionDocumentReader<ServiceRolloutReadModel, string> documentReader,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher ?? throw new ArgumentNullException(nameof(storeDispatcher));
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(ServiceRolloutProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        if (!ServiceCommittedStateSupport.TryGetObservedPayload(
                envelope,
                _clock,
                out var payload,
                out var eventId,
                out var stateVersion,
                out var observedAt) ||
            payload == null)
        {
            return;
        }

        if (payload.Is(ServiceRolloutStartedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutStartedEvent>();
            var serviceKey = ServiceProjectionMapping.ServiceKey(evt.Identity);
            if (string.IsNullOrWhiteSpace(serviceKey))
                return;

            var startedAt = ServiceProjectionMapping.FromTimestamp(evt.StartedAt, _clock.UtcNow);
            var readModel = await _documentReader.GetAsync(serviceKey, ct)
                ?? new ServiceRolloutReadModel { Id = serviceKey };
            readModel.RolloutId = evt.Plan?.RolloutId ?? string.Empty;
            readModel.DisplayName = evt.Plan?.DisplayName ?? string.Empty;
            readModel.Status = ServiceRolloutStatus.InProgress.ToString();
            readModel.CurrentStageIndex = -1;
            readModel.FailureReason = string.Empty;
            readModel.StartedAt = startedAt;
            readModel.ActorId = context.RootActorId;
            readModel.StateVersion = ServiceCommittedStateSupport.ResolveNextStateVersion(readModel.StateVersion, stateVersion);
            readModel.LastEventId = eventId;
            readModel.UpdatedAt = observedAt;
            readModel.BaselineTargets = evt.BaselineTargets.Select(ServiceProjectionMapping.ToServingTargetReadModel).ToList();
            readModel.Stages = (evt.Plan?.Stages ?? [])
                .Select((stage, index) => new ServiceRolloutStageReadModel
                {
                    StageId = stage.StageId ?? string.Empty,
                    StageIndex = index,
                    Targets = stage.Targets.Select(ServiceProjectionMapping.ToServingTargetReadModel).ToList(),
                })
                .ToList();
            await _storeDispatcher.UpsertAsync(readModel, ct);
            return;
        }

        if (payload.Is(ServiceRolloutStageAdvancedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutStageAdvancedEvent>();
            await MutateAsync(context.RootActorId, evt.Identity, eventId, stateVersion, observedAt, ct, readModel =>
            {
                readModel.RolloutId = evt.RolloutId ?? readModel.RolloutId;
                readModel.CurrentStageIndex = evt.StageIndex;
                readModel.Status = ServiceRolloutStatus.InProgress.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
                var stage = readModel.Stages.FirstOrDefault(x => x.StageIndex == evt.StageIndex);
                if (stage == null)
                {
                    stage = new ServiceRolloutStageReadModel();
                    readModel.Stages.Add(stage);
                }

                stage.StageIndex = evt.StageIndex;
                stage.StageId = evt.StageId ?? string.Empty;
                stage.Targets = evt.Targets.Select(ServiceProjectionMapping.ToServingTargetReadModel).ToList();
                readModel.Stages = readModel.Stages.OrderBy(x => x.StageIndex).ToList();
            });
            return;
        }

        if (payload.Is(ServiceRolloutPausedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutPausedEvent>();
            await MutateAsync(context.RootActorId, evt.Identity, eventId, stateVersion, observedAt, ct, readModel =>
            {
                readModel.RolloutId = evt.RolloutId ?? readModel.RolloutId;
                readModel.Status = ServiceRolloutStatus.Paused.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
            });
            return;
        }

        if (payload.Is(ServiceRolloutResumedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutResumedEvent>();
            await MutateAsync(context.RootActorId, evt.Identity, eventId, stateVersion, observedAt, ct, readModel =>
            {
                readModel.RolloutId = evt.RolloutId ?? readModel.RolloutId;
                readModel.Status = ServiceRolloutStatus.InProgress.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
            });
            return;
        }

        if (payload.Is(ServiceRolloutCompletedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutCompletedEvent>();
            await MutateAsync(context.RootActorId, evt.Identity, eventId, stateVersion, observedAt, ct, readModel =>
            {
                readModel.RolloutId = evt.RolloutId ?? readModel.RolloutId;
                readModel.Status = ServiceRolloutStatus.Completed.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
            });
            return;
        }

        if (payload.Is(ServiceRolloutRolledBackEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutRolledBackEvent>();
            await MutateAsync(context.RootActorId, evt.Identity, eventId, stateVersion, observedAt, ct, readModel =>
            {
                readModel.RolloutId = evt.RolloutId ?? readModel.RolloutId;
                readModel.Status = ServiceRolloutStatus.RolledBack.ToString();
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
                readModel.BaselineTargets = evt.Targets.Select(ServiceProjectionMapping.ToServingTargetReadModel).ToList();
            });
            return;
        }

        if (payload.Is(ServiceRolloutFailedEvent.Descriptor))
        {
            var evt = payload.Unpack<ServiceRolloutFailedEvent>();
            await MutateAsync(context.RootActorId, evt.Identity, eventId, stateVersion, observedAt, ct, readModel =>
            {
                readModel.RolloutId = evt.RolloutId ?? readModel.RolloutId;
                readModel.Status = ServiceRolloutStatus.Failed.ToString();
                readModel.FailureReason = evt.FailureReason ?? string.Empty;
                readModel.UpdatedAt = ServiceProjectionMapping.FromTimestamp(evt.OccurredAt, _clock.UtcNow);
            });
        }
    }

    private async Task MutateAsync(
        string actorId,
        ServiceIdentity? identity,
        string eventId,
        long stateVersion,
        DateTimeOffset observedAt,
        CancellationToken ct,
        Action<ServiceRolloutReadModel> mutate)
    {
        var serviceKey = ServiceProjectionMapping.ServiceKey(identity);
        if (string.IsNullOrWhiteSpace(serviceKey))
            return;

        var readModel = await _documentReader.GetAsync(serviceKey, ct)
            ?? new ServiceRolloutReadModel { Id = serviceKey, UpdatedAt = _clock.UtcNow };
        mutate(readModel);
        readModel.ActorId = actorId;
        readModel.StateVersion = ServiceCommittedStateSupport.ResolveNextStateVersion(readModel.StateVersion, stateVersion);
        readModel.LastEventId = eventId;
        readModel.UpdatedAt = observedAt;
        await _storeDispatcher.UpsertAsync(readModel, ct);
    }
}
