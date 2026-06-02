using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Core;
using Google.Protobuf.WellKnownTypes;

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
        if (!CommittedStateEventEnvelope.TryUnpackState<ScheduledDispatchState>(
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
        ScheduledDispatchState state)
    {
        var document = new WorkflowScheduleDocument
        {
            Id = context.RootActorId,
            ActorId = context.RootActorId,
            ScheduleId = string.IsNullOrWhiteSpace(state.ScheduleId) ? context.RootActorId : state.ScheduleId,
            DisplayName = state.DisplayName ?? string.Empty,
            WorkflowName = GetHeader(state, WorkflowScheduleAdapterHeaderKeys.WorkflowName),
            Prompt = GetHeader(state, WorkflowScheduleAdapterHeaderKeys.Prompt),
            CronExpression = state.CronExpression ?? string.Empty,
            Timezone = state.Timezone ?? string.Empty,
            Enabled = state.Enabled,
            LastRunActorId = state.LastTargetActorId ?? string.Empty,
            LastCommandId = state.LastCommandId ?? string.Empty,
            LastCorrelationId = state.LastCorrelationId ?? string.Empty,
            LastError = state.LastError ?? string.Empty,
            FireCount = state.FireCount,
            FailureCount = state.FailureCount,
            ScopeId = GetHeader(state, WorkflowScheduleAdapterHeaderKeys.ScopeId),
            TargetActorId = state.TargetActorId ?? string.Empty,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
        };
        document.CreatedAt = state.CreatedAt == default
            ? CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)
            : state.CreatedAt;
        document.UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        document.NextFireAt = state.NextFireAt;
        document.LastFireAt = state.LastFireAt;
        document.Headers = state.Headers
            .Where(static x => !WorkflowScheduleAdapterHeaderKeys.IsAdapterKey(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        document.FireRecords.Add(CreateFireRecords(state));
        return document;
    }

    private static string GetHeader(ScheduledDispatchState state, string key) =>
        state.Headers.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static WorkflowScheduleFireRecordDocument[] CreateFireRecords(ScheduledDispatchState state) =>
        state.FireRecords.Values
            .OrderByDescending(static x => ResolveTimestampSeconds(x.CompletedAt))
            .ThenByDescending(static x => ResolveTimestampNanos(x.CompletedAt))
            .ThenByDescending(static x => x.IdempotencyKey ?? string.Empty, StringComparer.Ordinal)
            .Select(static x => new WorkflowScheduleFireRecordDocument
            {
                ScheduledFireAtUtcValue = x.ScheduledFireAt?.Clone(),
                CompletedAtUtcValue = x.CompletedAt?.Clone(),
                IdempotencyKey = x.IdempotencyKey ?? string.Empty,
                RunActorId = x.TargetActorId ?? string.Empty,
                CommandId = x.CommandId ?? string.Empty,
                CorrelationId = x.CorrelationId ?? string.Empty,
                Error = x.Error ?? string.Empty,
                Manual = x.Manual,
            })
            .ToArray();

    private static long ResolveTimestampSeconds(Timestamp? timestamp) =>
        timestamp?.Seconds ?? 0;

    private static int ResolveTimestampNanos(Timestamp? timestamp) =>
        timestamp?.Nanos ?? 0;
}
