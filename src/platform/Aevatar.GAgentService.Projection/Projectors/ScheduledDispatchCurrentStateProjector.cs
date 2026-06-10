using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Projectors;

public sealed class ScheduledDispatchCurrentStateProjector
    : ICurrentStateProjectionMaterializer<ScheduledDispatchProjectionContext>
{
    private readonly IProjectionWriteDispatcher<ScheduledDispatchDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ScheduledDispatchCurrentStateProjector(
        IProjectionWriteDispatcher<ScheduledDispatchDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ScheduledDispatchProjectionContext context,
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

    private ScheduledDispatchDocument CreateDocument(
        ScheduledDispatchProjectionContext context,
        EventEnvelope envelope,
        StateEvent stateEvent,
        ScheduledDispatchState state)
    {
        var target = state.Target ?? new ScheduledDispatchTargetState();
        var serviceIdentity = target.ServiceInvocation?.Identity;
        var scheduleId = string.IsNullOrWhiteSpace(state.ScheduleId) ? context.RootActorId : state.ScheduleId;
        var document = new ScheduledDispatchDocument
        {
            Id = scheduleId,
            ActorId = context.RootActorId,
            ScheduleActorId = context.RootActorId,
            ScheduleId = scheduleId,
            DisplayName = state.DisplayName ?? string.Empty,
            TargetKind = ToApplicationTargetKind(target.Kind).ToString(),
            ScheduleKind = ToApplicationScheduleKind(state.ScheduleKind).ToString(),
            PayloadTypeUrl = state.PayloadTypeUrl ?? string.Empty,
            CronExpression = state.CronExpression ?? string.Empty,
            Timezone = state.Timezone ?? string.Empty,
            Enabled = state.Enabled,
            LastTargetActorId = state.LastTargetActorId ?? string.Empty,
            LastCommandId = state.LastCommandId ?? string.Empty,
            LastCorrelationId = state.LastCorrelationId ?? string.Empty,
            LastError = state.LastError ?? string.Empty,
            FireCount = state.FireCount,
            FailureCount = state.FailureCount,
            ServiceKey = serviceIdentity == null ? string.Empty : ServiceKeys.Build(serviceIdentity),
            ServiceId = serviceIdentity?.ServiceId ?? string.Empty,
            ServiceEndpointId = target.ServiceInvocation?.EndpointId ?? string.Empty,
            TargetActorId = state.TargetActorId ?? string.Empty,
            Deleted = state.Deleted,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
        };
        document.CreatedAt = state.CreatedAt == default
            ? CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow)
            : state.CreatedAt;
        document.UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        document.NextFireAt = state.NextFireAt;
        document.LastFireAt = state.LastFireAt;
        document.DeletedAt = state.DeletedAt;
        document.Headers = state.Headers
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        document.FireRecords.Add(CreateFireRecords(state));
        return document;
    }

    private static ScheduledDispatchFireRecordDocument[] CreateFireRecords(ScheduledDispatchState state) =>
        state.FireRecords.Values
            .OrderByDescending(static x => ResolveTimestampSeconds(x.CompletedAt))
            .ThenByDescending(static x => ResolveTimestampNanos(x.CompletedAt))
            .ThenByDescending(static x => x.IdempotencyKey ?? string.Empty, StringComparer.Ordinal)
            .Select(static x => new ScheduledDispatchFireRecordDocument
            {
                ScheduledFireAtUtcValue = x.ScheduledFireAt?.Clone(),
                CompletedAtUtcValue = x.CompletedAt?.Clone(),
                IdempotencyKey = x.IdempotencyKey ?? string.Empty,
                TargetActorId = x.TargetActorId ?? string.Empty,
                CommandId = x.CommandId ?? string.Empty,
                CorrelationId = x.CorrelationId ?? string.Empty,
                Error = x.Error ?? string.Empty,
                Manual = x.Manual,
            })
            .ToArray();

    private static ScheduledDispatchTargetKind ToApplicationTargetKind(ScheduledDispatchTargetKindState stateKind) =>
        stateKind switch
        {
            ScheduledDispatchTargetKindState.ServiceInvocation => ScheduledDispatchTargetKind.ServiceInvocation,
            _ => ScheduledDispatchTargetKind.Envelope,
        };

    private static ScheduledDispatchScheduleKind ToApplicationScheduleKind(ScheduledDispatchScheduleKindState stateKind) =>
        stateKind switch
        {
            ScheduledDispatchScheduleKindState.Workflow => ScheduledDispatchScheduleKind.Workflow,
            _ => ScheduledDispatchScheduleKind.Generic,
        };

    private static long ResolveTimestampSeconds(Timestamp? timestamp) =>
        timestamp?.Seconds ?? 0;

    private static int ResolveTimestampNanos(Timestamp? timestamp) =>
        timestamp?.Nanos ?? 0;
}
