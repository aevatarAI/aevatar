using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowScheduleCurrentStateProjector
    : ICurrentStateProjectionMaterializer<WorkflowExecutionMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<WorkflowScheduleDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowScheduleCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowScheduleDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<WorkflowScheduleState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = CreateDocument(context, envelope, stateEvent, state);

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    private WorkflowScheduleDocument CreateDocument(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        StateEvent stateEvent,
        WorkflowScheduleState state)
    {
        var document = new WorkflowScheduleDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            ScheduleId = string.IsNullOrWhiteSpace(state.ScheduleId) ? context.RootActorId : state.ScheduleId,
            DisplayName = state.DisplayName ?? string.Empty,
            WorkflowName = state.WorkflowName ?? string.Empty,
            Prompt = state.Prompt ?? string.Empty,
            CronExpression = state.CronExpression ?? string.Empty,
            Timezone = state.Timezone ?? string.Empty,
            Enabled = state.Enabled,
            LastRunActorId = state.LastRunActorId ?? string.Empty,
            LastCommandId = state.LastCommandId ?? string.Empty,
            LastCorrelationId = state.LastCorrelationId ?? string.Empty,
            LastError = state.LastError ?? string.Empty,
            FireCount = state.FireCount,
            FailureCount = state.FailureCount,
            ScopeId = state.ScopeId ?? string.Empty,
            TargetActorId = state.ActorId ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
        };
        document.CreatedAt = state.CreatedAt == default
            ? CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)
            : state.CreatedAt;
        document.UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        document.NextFireAt = state.NextFireAt;
        document.LastFireAt = state.LastFireAt;
        document.Headers = state.Headers.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        document.FireRecords.Add(CreateFireRecords(state));
        return document;
    }

    private static WorkflowScheduleFireRecordDocument[] CreateFireRecords(WorkflowScheduleState state) =>
        state.FireRecords.Values
            .OrderByDescending(static x => x.CompletedAt?.Seconds ?? 0)
            .ThenByDescending(static x => x.CompletedAt?.Nanos ?? 0)
            .Select(static x => new WorkflowScheduleFireRecordDocument
            {
                ScheduledFireAtUtcValue = x.ScheduledFireAt?.Clone(),
                CompletedAtUtcValue = x.CompletedAt?.Clone(),
                IdempotencyKey = x.IdempotencyKey ?? string.Empty,
                RunActorId = x.RunActorId ?? string.Empty,
                CommandId = x.CommandId ?? string.Empty,
                CorrelationId = x.CorrelationId ?? string.Empty,
                Error = x.Error ?? string.Empty,
                Manual = x.Manual,
            })
            .ToArray();
}
