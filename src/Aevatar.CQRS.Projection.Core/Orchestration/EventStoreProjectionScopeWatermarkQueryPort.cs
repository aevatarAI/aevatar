using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public sealed class EventStoreProjectionScopeWatermarkQueryPort : IProjectionScopeWatermarkQueryPort
{
    private readonly IEventStore _eventStore;

    public EventStoreProjectionScopeWatermarkQueryPort(IEventStore eventStore)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    }

    public async Task<long?> GetLastSuccessfulVersionAsync(
        ProjectionRuntimeScopeKey scopeKey,
        CancellationToken ct = default)
    {
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var events = await _eventStore.GetEventsAsync(scopeActorId, ct: ct);
        if (events.Count == 0)
            return null;

        var state = new ProjectionScopeState();
        foreach (var stateEvent in events)
            state = Apply(state, stateEvent);

        return state.Active && !state.Released ? state.LastSuccessfulVersion : null;
    }

    private static ProjectionScopeState Apply(ProjectionScopeState current, StateEvent stateEvent)
    {
        if (stateEvent.EventData?.Is(ProjectionScopeStartedEvent.Descriptor) == true)
            return ProjectionScopeStateApplier.ApplyStarted(current, stateEvent.EventData.Unpack<ProjectionScopeStartedEvent>());

        if (stateEvent.EventData?.Is(ProjectionObservationAttachmentUpdatedEvent.Descriptor) == true)
        {
            return ProjectionScopeStateApplier.ApplyAttachmentUpdated(
                current,
                stateEvent.EventData.Unpack<ProjectionObservationAttachmentUpdatedEvent>());
        }

        if (stateEvent.EventData?.Is(ProjectionScopeReleasedEvent.Descriptor) == true)
            return ProjectionScopeStateApplier.ApplyReleased(current, stateEvent.EventData.Unpack<ProjectionScopeReleasedEvent>());

        if (stateEvent.EventData?.Is(ProjectionScopeWatermarkAdvancedEvent.Descriptor) == true)
        {
            return ProjectionScopeStateApplier.ApplyWatermarkAdvanced(
                current,
                stateEvent.EventData.Unpack<ProjectionScopeWatermarkAdvancedEvent>());
        }

        if (stateEvent.EventData?.Is(ProjectionScopeDispatchFailedEvent.Descriptor) == true)
        {
            return ProjectionScopeStateApplier.ApplyDispatchFailed(
                current,
                stateEvent.EventData.Unpack<ProjectionScopeDispatchFailedEvent>());
        }

        if (stateEvent.EventData?.Is(ProjectionScopeFailureReplayedEvent.Descriptor) == true)
        {
            return ProjectionScopeStateApplier.ApplyFailureReplayed(
                current,
                stateEvent.EventData.Unpack<ProjectionScopeFailureReplayedEvent>());
        }

        return current;
    }
}
