using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal sealed class ProjectionScopeCommittedStateRedactionHook : ICommittedStatePublicationHook
{
    public Task BeforePublishAsync(CommittedStatePublicationContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (!IsProjectionScopeActor(context.ActorType) ||
            context.Published.StateRoot?.Is(ProjectionScopeState.Descriptor) != true)
        {
            return Task.CompletedTask;
        }

        RedactStateRoot(context);
        RedactStateEvent(context);
        return Task.CompletedTask;
    }

    private static bool IsProjectionScopeActor(System.Type actorType)
    {
        for (System.Type? current = actorType; current != null; current = current.BaseType)
        {
            if (current.IsGenericType &&
                current.GetGenericTypeDefinition() == typeof(ProjectionScopeGAgentBase<>))
            {
                return true;
            }
        }

        return false;
    }

    private static void RedactStateRoot(CommittedStatePublicationContext context)
    {
        var state = context.Published.StateRoot.Unpack<ProjectionScopeState>();
        state.FailureSummary = ProjectionScopeFailureLog.BuildSummary(state.Failures);

        var compatibilityCount = state.Failures.Count;
        var compatibilityRetryExhaustedCount = Math.Min(
            state.FailureSummary.RetryExhaustedFailureCount,
            compatibilityCount);
        var oldest = state.FailureSummary.OldestUnresolvedFailureAtUtc?.Clone();

        state.Failures.Clear();
        for (var index = 0; index < compatibilityCount; index++)
        {
            state.Failures.Add(new ProjectionScopeFailure
            {
                RetryExhausted = index < compatibilityRetryExhaustedCount,
                OccurredAtUtc = index == 0 ? oldest : null,
            });
        }

        ProjectionFailureRetentionPolicy.Trim(state.RetainedFailureDiagnostics);
        if (state.InFlightObservation != null)
            state.InFlightObservation.Envelope = null;
        context.Published.StateRoot = Any.Pack(state);
    }

    private static void RedactStateEvent(CommittedStatePublicationContext context)
    {
        if (context.Published.StateEvent is not { EventData: { } eventData } stateEvent)
            return;

        if (eventData.Is(ProjectionScopeDispatchFailedEvent.Descriptor))
        {
            var failed = eventData.Unpack<ProjectionScopeDispatchFailedEvent>();
            if (failed.Envelope == null && string.IsNullOrEmpty(failed.Reason))
                return;

            failed.Envelope = null;
            failed.Reason = string.Empty;
            stateEvent.EventData = Any.Pack(failed);
            return;
        }

        if (eventData.Is(ProjectionScopeObservationStagedEvent.Descriptor))
        {
            var staged = eventData.Unpack<ProjectionScopeObservationStagedEvent>();
            if (staged.Observation?.Envelope == null)
                return;

            staged.Observation.Envelope = null;
            stateEvent.EventData = Any.Pack(staged);
            return;
        }

        if (!eventData.Is(ProjectionScopeFailureReplayedEvent.Descriptor))
            return;

        var replayed = eventData.Unpack<ProjectionScopeFailureReplayedEvent>();
        if (string.IsNullOrEmpty(replayed.Reason))
            return;

        replayed.Reason = string.Empty;
        stateEvent.EventData = Any.Pack(replayed);
    }
}
