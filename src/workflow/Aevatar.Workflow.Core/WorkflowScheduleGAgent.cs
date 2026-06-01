using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed class WorkflowScheduleGAgent : GAgentBase<WorkflowScheduleState>
{
    private const string NextFireCallbackId = "workflow-schedule-next-fire";
    private const int MaxFireRecordCount = 128;
    private readonly ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> _workflowRunService;

    public WorkflowScheduleGAgent(
        ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError> workflowRunService)
    {
        _workflowRunService = workflowRunService ?? throw new ArgumentNullException(nameof(workflowRunService));
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.Enabled && !string.IsNullOrWhiteSpace(State.CronExpression))
            await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, ct);
    }

    public override Task<string> GetDescriptionAsync()
    {
        var scheduleId = string.IsNullOrWhiteSpace(State.ScheduleId) ? Id : State.ScheduleId;
        var status = State.Enabled ? "enabled" : "disabled";
        return Task.FromResult($"WorkflowScheduleGAgent[{scheduleId}] {status}");
    }

    protected override WorkflowScheduleState TransitionState(WorkflowScheduleState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowScheduleConfiguredEvent>(ApplyConfigured)
            .On<WorkflowScheduleEnabledEvent>(ApplyEnabled)
            .On<WorkflowScheduleDisabledEvent>(ApplyDisabled)
            .On<WorkflowScheduleNextFireScheduledEvent>(ApplyNextFireScheduled)
            .On<WorkflowScheduleFireStartedEvent>(ApplyFireStarted)
            .On<WorkflowScheduleFireDispatchedEvent>(ApplyFireDispatched)
            .On<WorkflowScheduleFireFailedEvent>(ApplyFireFailed)
            .OrCurrent();

    [EventHandler]
    public async Task HandleConfigureAsync(WorkflowScheduleConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureValidDefinition(command.WorkflowName, command.Prompt, command.CronExpression, command.Timezone);

        var now = DateTimeOffset.UtcNow;
        var configured = new WorkflowScheduleConfiguredEvent
        {
            ScheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId)),
            DisplayName = NormalizeOptional(command.DisplayName),
            WorkflowName = NormalizeRequired(command.WorkflowName, nameof(command.WorkflowName)),
            Prompt = NormalizeRequired(command.Prompt, nameof(command.Prompt)),
            CronExpression = NormalizeRequired(command.CronExpression, nameof(command.CronExpression)),
            Timezone = WorkflowScheduleCalculator.NormalizeTimezone(command.Timezone),
            Enabled = command.Enabled,
            ConfiguredAt = Timestamp.FromDateTimeOffset(now),
            ScopeId = NormalizeOptional(command.ScopeId),
            ActorId = NormalizeOptional(command.ActorId),
        };
        foreach (var (key, value) in NormalizeHeaders(command.Headers))
            configured.Headers[key] = value;

        if (!command.Enabled)
            await CancelNextFireLeaseAsync(CancellationToken.None);

        await PersistDomainEventAsync(configured);

        if (command.Enabled)
            await EnsureNextFireScheduledAsync(now, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleEnableAsync(WorkflowScheduleEnableCommand command)
    {
        if (string.IsNullOrWhiteSpace(State.WorkflowName) ||
            string.IsNullOrWhiteSpace(State.Prompt) ||
            string.IsNullOrWhiteSpace(State.CronExpression))
        {
            Logger.LogWarning("Workflow schedule {ActorId} enable ignored because it is not configured.", Id);
            return;
        }

        await PersistDomainEventAsync(new WorkflowScheduleEnabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            EnabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDisableAsync(WorkflowScheduleDisableCommand command)
    {
        await CancelNextFireLeaseAsync(CancellationToken.None);
        await PersistDomainEventAsync(new WorkflowScheduleDisabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DisabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    [EventHandler(AllowSelfHandling = true)]
    public Task HandleFireAsync(WorkflowScheduleFireCommand command) =>
        HandleFireAsync(command, ActiveInboundEnvelope, CancellationToken.None);

    internal async Task HandleFireAsync(
        WorkflowScheduleFireCommand command,
        EventEnvelope? inboundEnvelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.Manual && !State.Enabled)
        {
            Logger.LogInformation("Workflow schedule {ActorId} ignored fire because it is disabled.", Id);
            return;
        }

        if (!command.Manual && !MatchesNextFireLease(inboundEnvelope))
        {
            Logger.LogInformation("Workflow schedule {ActorId} ignored stale fire callback.", Id);
            return;
        }

        var scheduledFireAt = ResolveScheduledFireAt(command);
        var idempotencyKey = WorkflowScheduleCalculator.BuildIdempotencyKey(ResolveScheduleId(), scheduledFireAt);
        if (State.FireRecords.ContainsKey(idempotencyKey))
        {
            Logger.LogInformation(
                "Workflow schedule {ActorId} ignored duplicate fire {IdempotencyKey}.",
                Id,
                idempotencyKey);
            if (!command.Manual)
                await EnsureNextFireScheduledAsync(scheduledFireAt, ct);
            return;
        }

        await PersistDomainEventAsync(new WorkflowScheduleFireStartedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IdempotencyKey = idempotencyKey,
            Manual = command.Manual,
        }, ct);

        try
        {
            var dispatch = await _workflowRunService.DispatchAsync(BuildWorkflowRunRequest(scheduledFireAt, idempotencyKey), ct);
            if (!dispatch.Succeeded || dispatch.Receipt == null)
            {
                await PersistFireFailedAsync(
                    scheduledFireAt,
                    idempotencyKey,
                    $"Workflow dispatch failed with start error '{dispatch.Error}'.",
                    command.Manual,
                    ct);
            }
            else
            {
                await PersistDomainEventAsync(new WorkflowScheduleFireDispatchedEvent
                {
                    ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
                    DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    IdempotencyKey = idempotencyKey,
                    RunActorId = dispatch.Receipt.ActorId,
                    CommandId = dispatch.Receipt.CommandId,
                    CorrelationId = dispatch.Receipt.CorrelationId,
                    Manual = command.Manual,
                }, ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Workflow schedule {ActorId} dispatch failed.", Id);
            await PersistFireFailedAsync(scheduledFireAt, idempotencyKey, ex.Message, command.Manual, CancellationToken.None);
        }

        if (!command.Manual)
            await EnsureNextFireScheduledAsync(scheduledFireAt, CancellationToken.None);
    }

    private async Task PersistFireFailedAsync(
        DateTimeOffset scheduledFireAt,
        string idempotencyKey,
        string error,
        bool manual,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(new WorkflowScheduleFireFailedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IdempotencyKey = idempotencyKey,
            Error = string.IsNullOrWhiteSpace(error) ? "Workflow dispatch failed." : error.Trim(),
            Manual = manual,
        }, ct);
    }

    private WorkflowChatRunRequest BuildWorkflowRunRequest(DateTimeOffset scheduledFireAtUtc, string idempotencyKey)
    {
        var headers = new Dictionary<string, string>(State.Headers, StringComparer.Ordinal)
        {
            [WorkflowRunCommandMetadataKeys.IdempotencyKey] = idempotencyKey,
            ["workflow.schedule_id"] = ResolveScheduleId(),
            ["workflow.scheduled_fire_at_utc"] = scheduledFireAtUtc.ToUniversalTime().ToString("O"),
        };

        return new WorkflowChatRunRequest(
            Prompt: State.Prompt ?? string.Empty,
            Source: string.IsNullOrWhiteSpace(State.ActorId)
                ? WorkflowChatSource.CatalogWorkflow(State.WorkflowName)
                : WorkflowChatSource.DefinitionActor(State.ActorId, State.WorkflowName),
            SessionId: idempotencyKey,
            Metadata: headers,
            ScopeId: string.IsNullOrWhiteSpace(State.ScopeId) ? null : State.ScopeId);
    }

    private async Task EnsureNextFireScheduledAsync(DateTimeOffset fromUtc, CancellationToken ct)
    {
        if (!State.Enabled || string.IsNullOrWhiteSpace(State.CronExpression))
            return;

        if (!WorkflowScheduleCalculator.TryGetNextOccurrence(
                State.CronExpression,
                State.Timezone,
                fromUtc,
                out var nextFireAtUtc,
                out var error))
        {
            Logger.LogWarning("Workflow schedule {ActorId} could not compute next fire: {Error}", Id, error);
            return;
        }

        await CancelNextFireLeaseAsync(ct);
        var dueTime = WorkflowScheduleCalculator.ComputeDueTime(nextFireAtUtc, DateTimeOffset.UtcNow);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            NextFireCallbackId,
            dueTime,
            new WorkflowScheduleFireCommand
            {
                ScheduledFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
                Manual = false,
            },
            ct: ct);

        await PersistDomainEventAsync(new WorkflowScheduleNextFireScheduledEvent
        {
            NextFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
            Lease = WorkflowScheduleRuntimeCallbackLeaseStateCodec.ToState(lease),
        }, ct);
    }

    private async Task CancelNextFireLeaseAsync(CancellationToken ct)
    {
        var lease = WorkflowScheduleRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        if (lease == null)
            return;

        await CancelDurableCallbackAsync(lease, ct);
    }

    private bool MatchesNextFireLease(EventEnvelope? envelope)
    {
        if (envelope == null)
            return false;

        var lease = WorkflowScheduleRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        return lease != null && RuntimeCallbackEnvelopeStateReader.MatchesLease(envelope, lease);
    }

    private DateTimeOffset ResolveScheduledFireAt(WorkflowScheduleFireCommand command)
    {
        if (command.ScheduledFireAt != null)
            return command.ScheduledFireAt.ToDateTimeOffset().ToUniversalTime();

        return DateTimeOffset.UtcNow;
    }

    private string ResolveScheduleId() =>
        string.IsNullOrWhiteSpace(State.ScheduleId) ? Id : State.ScheduleId;

    private static void EnsureValidDefinition(
        string workflowName,
        string prompt,
        string cronExpression,
        string timezone)
    {
        _ = NormalizeRequired(workflowName, nameof(workflowName));
        _ = NormalizeRequired(prompt, nameof(prompt));
        _ = NormalizeRequired(cronExpression, nameof(cronExpression));

        if (!WorkflowScheduleCalculator.TryGetNextOccurrence(
                cronExpression,
                timezone,
                DateTimeOffset.UtcNow,
                out _,
                out var error))
        {
            throw new ArgumentException(error ?? "Schedule is invalid.", nameof(cronExpression));
        }
    }

    private WorkflowScheduleState ApplyConfigured(
        WorkflowScheduleState current,
        WorkflowScheduleConfiguredEvent evt)
    {
        var next = current.Clone();
        var configuredAt = evt.ConfiguredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var scheduleId = NormalizeRequired(evt.ScheduleId, nameof(evt.ScheduleId));
        if (string.IsNullOrWhiteSpace(next.ScheduleId))
        {
            next.ScheduleId = scheduleId;
            next.CreatedAt = configuredAt;
        }

        next.ScheduleId = scheduleId;
        next.DisplayName = evt.DisplayName ?? string.Empty;
        next.WorkflowName = evt.WorkflowName ?? string.Empty;
        next.Prompt = evt.Prompt ?? string.Empty;
        next.CronExpression = evt.CronExpression ?? string.Empty;
        next.Timezone = WorkflowScheduleCalculator.NormalizeTimezone(evt.Timezone);
        next.Enabled = evt.Enabled;
        next.UpdatedAt = configuredAt;
        next.Headers.Clear();
        foreach (var (key, value) in NormalizeHeaders(evt.Headers))
            next.Headers[key] = value;
        next.ScopeId = evt.ScopeId ?? string.Empty;
        next.ActorId = evt.ActorId ?? string.Empty;
        if (!next.Enabled)
        {
            next.NextFireAt = null;
            next.NextFireLease = null;
        }

        return next;
    }

    private WorkflowScheduleState ApplyEnabled(WorkflowScheduleState current, WorkflowScheduleEnabledEvent evt)
    {
        var next = current.Clone();
        next.Enabled = true;
        next.UpdatedAt = evt.EnabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private WorkflowScheduleState ApplyDisabled(WorkflowScheduleState current, WorkflowScheduleDisabledEvent evt)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextFireAt = null;
        next.NextFireLease = null;
        next.UpdatedAt = evt.DisabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static WorkflowScheduleState ApplyNextFireScheduled(
        WorkflowScheduleState current,
        WorkflowScheduleNextFireScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextFireAt = evt.NextFireAt?.ToDateTimeOffset();
        next.NextFireLease = evt.Lease?.Clone();
        next.UpdatedAt = DateTimeOffset.UtcNow;
        return next;
    }

    private WorkflowScheduleState ApplyFireStarted(
        WorkflowScheduleState current,
        WorkflowScheduleFireStartedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastError = string.Empty;
        next.UpdatedAt = evt.StartedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new WorkflowScheduleFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.StartedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Manual = evt.Manual,
            Status = WorkflowScheduleFireStatusState.Started,
        });
        return next;
    }

    private WorkflowScheduleState ApplyFireDispatched(
        WorkflowScheduleState current,
        WorkflowScheduleFireDispatchedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastRunActorId = evt.RunActorId ?? string.Empty;
        next.LastCommandId = evt.CommandId ?? string.Empty;
        next.LastCorrelationId = evt.CorrelationId ?? string.Empty;
        next.LastError = string.Empty;
        next.FireCount++;
        next.UpdatedAt = evt.DispatchedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new WorkflowScheduleFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.DispatchedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            RunActorId = evt.RunActorId ?? string.Empty,
            CommandId = evt.CommandId ?? string.Empty,
            CorrelationId = evt.CorrelationId ?? string.Empty,
            Manual = evt.Manual,
            Status = WorkflowScheduleFireStatusState.Dispatched,
        });
        return next;
    }

    private WorkflowScheduleState ApplyFireFailed(
        WorkflowScheduleState current,
        WorkflowScheduleFireFailedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastError = evt.Error ?? string.Empty;
        next.FireCount++;
        next.FailureCount++;
        next.UpdatedAt = evt.FailedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new WorkflowScheduleFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.FailedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            Manual = evt.Manual,
            Status = WorkflowScheduleFireStatusState.Failed,
        });
        return next;
    }

    private static void UpsertFireRecord(
        WorkflowScheduleState state,
        string idempotencyKey,
        WorkflowScheduleFireRecordState record)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return;

        state.FireRecords[idempotencyKey] = record;
        if (state.FireRecords.Count <= MaxFireRecordCount)
            return;

        var keysToRemove = state.FireRecords
            .OrderBy(static x => x.Value.CompletedAt?.Seconds ?? 0)
            .Take(state.FireRecords.Count - MaxFireRecordCount)
            .Select(static x => x.Key)
            .ToArray();
        foreach (var key in keysToRemove)
            state.FireRecords.Remove(key);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);

        return value.Trim();
    }

    private static string NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static IReadOnlyDictionary<string, string> NormalizeHeaders(
        IEnumerable<KeyValuePair<string, string>>? source)
    {
        if (source == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeOptional(key);
            var normalizedValue = NormalizeOptional(value);
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            normalized[normalizedKey] = normalizedValue;
        }

        return normalized;
    }
}
