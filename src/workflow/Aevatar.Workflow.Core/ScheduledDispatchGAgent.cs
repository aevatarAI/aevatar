using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core;

public sealed class ScheduledDispatchGAgent : GAgentBase<ScheduledDispatchState>
{
    private const string NextFireCallbackId = "scheduled-dispatch-next-fire";
    private const int MaxFireRecordCount = 128;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IWorkflowRunActorResolver? _workflowRunActorResolver;
    private readonly ICommandEnvelopeFactory<WorkflowChatRunRequest>? _workflowChatEnvelopeFactory;

    public ScheduledDispatchGAgent(
        IActorDispatchPort dispatchPort,
        IWorkflowRunActorResolver? workflowRunActorResolver = null,
        ICommandEnvelopeFactory<WorkflowChatRunRequest>? workflowChatEnvelopeFactory = null)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _workflowRunActorResolver = workflowRunActorResolver;
        _workflowChatEnvelopeFactory = workflowChatEnvelopeFactory;
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
        return Task.FromResult($"ScheduledDispatchGAgent[{scheduleId}] {status}");
    }

    protected override ScheduledDispatchState TransitionState(ScheduledDispatchState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScheduledDispatchConfiguredEvent>(ApplyConfigured)
            .On<ScheduledDispatchEnabledEvent>(ApplyEnabled)
            .On<ScheduledDispatchDisabledEvent>(ApplyDisabled)
            .On<ScheduledDispatchNextFireScheduledEvent>(ApplyNextFireScheduled)
            .On<ScheduledDispatchFireStartedEvent>(ApplyFireStarted)
            .On<ScheduledDispatchFireDispatchedEvent>(ApplyFireDispatched)
            .On<ScheduledDispatchFireFailedEvent>(ApplyFireFailed)
            .OrCurrent();

    [EventHandler]
    public async Task HandleConfigureAsync(ScheduledDispatchConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureValidDefinition(command.TargetActorId, command.TriggerEnvelope, command.CronExpression, command.Timezone);

        var now = DateTimeOffset.UtcNow;
        var configured = new ScheduledDispatchConfiguredEvent
        {
            ScheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId)),
            DisplayName = NormalizeOptional(command.DisplayName),
            TargetActorId = NormalizeRequired(command.TargetActorId, nameof(command.TargetActorId)),
            TriggerEnvelope = command.TriggerEnvelope.Clone(),
            CronExpression = NormalizeRequired(command.CronExpression, nameof(command.CronExpression)),
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(command.Timezone),
            Enabled = command.Enabled,
            ConfiguredAt = Timestamp.FromDateTimeOffset(now),
            PayloadTypeUrl = ResolvePayloadTypeUrl(command.TriggerEnvelope),
        };
        foreach (var (key, value) in NormalizeHeaders(command.Headers))
            configured.Headers[key] = value;

        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(configured);

        if (command.Enabled)
            await EnsureNextFireScheduledAsync(now, CancellationToken.None);
        else
            await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleEnableAsync(ScheduledDispatchEnableCommand command)
    {
        if (string.IsNullOrWhiteSpace(State.TargetActorId) ||
            State.TriggerEnvelope == null ||
            State.TriggerEnvelope.Payload == null ||
            string.IsNullOrWhiteSpace(State.CronExpression))
        {
            Logger.LogWarning("Scheduled dispatch {ActorId} enable ignored because it is not configured.", Id);
            return;
        }

        await PersistDomainEventAsync(new ScheduledDispatchEnabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            EnabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDisableAsync(ScheduledDispatchDisableCommand command)
    {
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new ScheduledDispatchDisabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DisabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public Task HandleFireAsync(ScheduledDispatchFireCommand command) =>
        HandleFireAsync(command, ActiveInboundEnvelope, CancellationToken.None);

    internal async Task HandleFireAsync(
        ScheduledDispatchFireCommand command,
        EventEnvelope? inboundEnvelope,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.Manual && !State.Enabled)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored fire because it is disabled.", Id);
            return;
        }

        if (!command.Manual && !MatchesNextFireLease(inboundEnvelope))
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored stale fire callback.", Id);
            return;
        }

        var scheduledFireAt = ResolveScheduledFireAt(command);
        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey(ResolveScheduleId(), scheduledFireAt);
        if (State.FireRecords.ContainsKey(idempotencyKey))
        {
            Logger.LogInformation(
                "Scheduled dispatch {ActorId} ignored duplicate fire {IdempotencyKey}.",
                Id,
                idempotencyKey);
            if (!command.Manual)
                await EnsureNextFireScheduledAsync(scheduledFireAt, ct);
            return;
        }

        await PersistDomainEventAsync(new ScheduledDispatchFireStartedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IdempotencyKey = idempotencyKey,
            Manual = command.Manual,
        }, ct);

        try
        {
            var prepared = await BuildDispatchEnvelopeAsync(scheduledFireAt, idempotencyKey, ct);
            var envelope = prepared.Envelope;
            var admission = await _dispatchPort.DispatchAsync(prepared.TargetActorId, envelope, ct);
            if (!admission.Accepted)
            {
                await PersistFireFailedAsync(
                    scheduledFireAt,
                    idempotencyKey,
                    "Scheduled dispatch was not accepted.",
                    command.Manual,
                    ct);
            }
            else
            {
                await PersistDomainEventAsync(new ScheduledDispatchFireDispatchedEvent
                {
                    ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
                    DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    IdempotencyKey = idempotencyKey,
                    TargetActorId = prepared.TargetActorId,
                    CommandId = admission.CommandId,
                    CorrelationId = admission.CorrelationId,
                    Manual = command.Manual,
                }, ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Scheduled dispatch {ActorId} dispatch failed.", Id);
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
        await PersistDomainEventAsync(new ScheduledDispatchFireFailedEvent
        {
            ScheduledFireAt = Timestamp.FromDateTimeOffset(scheduledFireAt),
            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            IdempotencyKey = idempotencyKey,
            Error = string.IsNullOrWhiteSpace(error) ? "Scheduled dispatch failed." : error.Trim(),
            Manual = manual,
        }, ct);
    }

    private async Task<ScheduledDispatchEnvelope> BuildDispatchEnvelopeAsync(
        DateTimeOffset scheduledFireAtUtc,
        string idempotencyKey,
        CancellationToken ct)
    {
        var envelope = State.TriggerEnvelope?.Clone()
            ?? throw new InvalidOperationException("Scheduled dispatch trigger envelope is not configured.");
        if (envelope.Payload == null)
            throw new InvalidOperationException("Scheduled dispatch trigger envelope payload is not configured.");

        var headers = BuildFireHeaders(scheduledFireAtUtc, idempotencyKey);
        if (envelope.Payload.TryUnpack<WorkflowScheduledDispatchStartRequest>(out var workflowStartRequest))
            return await BuildWorkflowDispatchEnvelopeAsync(workflowStartRequest, headers, idempotencyKey, ct);

        envelope.Id = idempotencyKey;
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(ResolveScheduleId(), State.TargetActorId);
        envelope.Runtime = null;
        var propagation = envelope.EnsurePropagation();
        if (string.IsNullOrWhiteSpace(propagation.CorrelationId))
            propagation.CorrelationId = idempotencyKey;

        if (envelope.Payload.TryUnpack<ChatRequestEvent>(out var chatRequest))
        {
            chatRequest.SessionId = idempotencyKey;
            chatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId] = idempotencyKey;
            foreach (var (key, value) in headers)
                chatRequest.Metadata[key] = value;
            envelope.Payload = Any.Pack(chatRequest);
        }
        else
        {
            throw new NotSupportedException(
                $"Scheduled dispatch payload type '{envelope.Payload.TypeUrl}' does not support scheduled fire headers.");
        }

        return new ScheduledDispatchEnvelope(State.TargetActorId, envelope);
    }

    private async Task<ScheduledDispatchEnvelope> BuildWorkflowDispatchEnvelopeAsync(
        WorkflowScheduledDispatchStartRequest workflowStartRequest,
        IReadOnlyDictionary<string, string> fireHeaders,
        string idempotencyKey,
        CancellationToken ct)
    {
        if (_workflowRunActorResolver == null || _workflowChatEnvelopeFactory == null)
            throw new InvalidOperationException("Workflow scheduled dispatch adapter is not configured.");

        var requestHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in workflowStartRequest.Headers)
            requestHeaders[key] = value;
        foreach (var (key, value) in fireHeaders)
            requestHeaders[key] = value;

        var request = new WorkflowChatRunRequest(
            Prompt: workflowStartRequest.Prompt,
            Source: string.IsNullOrWhiteSpace(workflowStartRequest.ActorId)
                ? WorkflowChatSource.CatalogWorkflow(workflowStartRequest.WorkflowName)
                : WorkflowChatSource.DefinitionActor(workflowStartRequest.ActorId, workflowStartRequest.WorkflowName),
            SessionId: idempotencyKey,
            Metadata: requestHeaders,
            ScopeId: string.IsNullOrWhiteSpace(workflowStartRequest.ScopeId) ? null : workflowStartRequest.ScopeId);

        var actorResolution = await _workflowRunActorResolver.ResolveOrCreateAsync(request, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
        {
            throw new WorkflowScheduleConflictException(
                ResolveScheduleId(),
                $"Workflow schedule '{ResolveScheduleId()}' target could not be prepared: {actorResolution.Error}.");
        }

        var context = new CommandContext(
            actorResolution.Target.ActorId,
            idempotencyKey,
            idempotencyKey,
            requestHeaders);
        var envelope = _workflowChatEnvelopeFactory.CreateEnvelope(request, context);
        envelope.Id = idempotencyKey;
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(ResolveScheduleId(), actorResolution.Target.ActorId);
        envelope.Runtime = null;
        var propagation = envelope.EnsurePropagation();
        propagation.CorrelationId = idempotencyKey;

        return new ScheduledDispatchEnvelope(actorResolution.Target.ActorId, envelope);
    }

    private IReadOnlyDictionary<string, string> BuildFireHeaders(
        DateTimeOffset scheduledFireAtUtc,
        string idempotencyKey) =>
        new Dictionary<string, string>(State.Headers, StringComparer.Ordinal)
        {
            [ScheduledDispatchMetadataKeys.ScheduleId] = ResolveScheduleId(),
            [ScheduledDispatchMetadataKeys.FireAtUtc] = scheduledFireAtUtc.ToUniversalTime().ToString("O"),
            [ScheduledDispatchMetadataKeys.IdempotencyKey] = idempotencyKey,
        };

    private sealed record ScheduledDispatchEnvelope(
        string TargetActorId,
        EventEnvelope Envelope);

    private async Task EnsureNextFireScheduledAsync(DateTimeOffset fromUtc, CancellationToken ct)
    {
        if (!State.Enabled || string.IsNullOrWhiteSpace(State.CronExpression))
            return;

        if (!ScheduledDispatchCalculator.TryGetNextOccurrence(
                State.CronExpression,
                State.Timezone,
                fromUtc,
                out var nextFireAtUtc,
                out var error))
        {
            Logger.LogWarning("Scheduled dispatch {ActorId} could not compute next fire: {Error}", Id, error);
            return;
        }

        await CancelNextFireLeaseAsync(ct);
        var dueTime = ScheduledDispatchCalculator.ComputeDueTime(nextFireAtUtc, DateTimeOffset.UtcNow);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            NextFireCallbackId,
            dueTime,
            new ScheduledDispatchFireCommand
            {
                ScheduledFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
                Manual = false,
            },
            ct: ct);

        await PersistDomainEventAsync(new ScheduledDispatchNextFireScheduledEvent
        {
            NextFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
            Lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToState(lease),
        }, ct);
    }

    private async Task CancelNextFireLeaseAsync(CancellationToken ct)
    {
        var lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await CancelNextFireLeaseAsync(lease, ct);
    }

    private async Task CancelNextFireLeaseAsync(RuntimeCallbackLease? lease, CancellationToken ct)
    {
        if (lease == null)
            return;

        await CancelDurableCallbackAsync(lease, ct);
    }

    private bool MatchesNextFireLease(EventEnvelope? envelope)
    {
        if (envelope == null)
            return false;

        var lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        return lease != null && RuntimeCallbackEnvelopeStateReader.MatchesLease(envelope, lease);
    }

    private DateTimeOffset ResolveScheduledFireAt(ScheduledDispatchFireCommand command)
    {
        if (command.ScheduledFireAt != null)
            return command.ScheduledFireAt.ToDateTimeOffset().ToUniversalTime();

        return DateTimeOffset.UtcNow;
    }

    private string ResolveScheduleId() =>
        string.IsNullOrWhiteSpace(State.ScheduleId) ? Id : State.ScheduleId;

    private static void EnsureValidDefinition(
        string targetActorId,
        EventEnvelope? triggerEnvelope,
        string cronExpression,
        string timezone)
    {
        _ = NormalizeRequired(targetActorId, nameof(targetActorId));
        if (triggerEnvelope == null || triggerEnvelope.Payload == null)
            throw new ArgumentException("Trigger envelope with payload is required.", nameof(triggerEnvelope));
        _ = NormalizeRequired(cronExpression, nameof(cronExpression));

        if (!ScheduledDispatchCalculator.TryGetNextOccurrence(
                cronExpression,
                timezone,
                DateTimeOffset.UtcNow,
                out _,
                out var error))
        {
            throw new ArgumentException(error ?? "Schedule is invalid.", nameof(cronExpression));
        }
    }

    private ScheduledDispatchState ApplyConfigured(
        ScheduledDispatchState current,
        ScheduledDispatchConfiguredEvent evt)
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
        next.TargetActorId = evt.TargetActorId ?? string.Empty;
        next.TriggerEnvelope = evt.TriggerEnvelope?.Clone();
        next.CronExpression = evt.CronExpression ?? string.Empty;
        next.Timezone = ScheduledDispatchCalculator.NormalizeTimezone(evt.Timezone);
        next.Enabled = evt.Enabled;
        next.UpdatedAt = configuredAt;
        next.PayloadTypeUrl = evt.PayloadTypeUrl ?? ResolvePayloadTypeUrl(evt.TriggerEnvelope);
        next.Headers.Clear();
        foreach (var (key, value) in NormalizeHeaders(evt.Headers))
            next.Headers[key] = value;
        if (!next.Enabled)
        {
            next.NextFireAt = null;
            next.NextFireLease = null;
        }

        return next;
    }

    private ScheduledDispatchState ApplyEnabled(ScheduledDispatchState current, ScheduledDispatchEnabledEvent evt)
    {
        var next = current.Clone();
        next.Enabled = true;
        next.UpdatedAt = evt.EnabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyDisabled(ScheduledDispatchState current, ScheduledDispatchDisabledEvent evt)
    {
        var next = current.Clone();
        next.Enabled = false;
        next.NextFireAt = null;
        next.NextFireLease = null;
        next.UpdatedAt = evt.DisabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyNextFireScheduled(
        ScheduledDispatchState current,
        ScheduledDispatchNextFireScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextFireAt = evt.NextFireAt?.ToDateTimeOffset();
        next.NextFireLease = evt.Lease?.Clone();
        next.UpdatedAt = DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyFireStarted(
        ScheduledDispatchState current,
        ScheduledDispatchFireStartedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastError = string.Empty;
        next.UpdatedAt = evt.StartedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.StartedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Manual = evt.Manual,
            Status = ScheduledDispatchFireStatusState.Started,
        });
        return next;
    }

    private ScheduledDispatchState ApplyFireDispatched(
        ScheduledDispatchState current,
        ScheduledDispatchFireDispatchedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastTargetActorId = evt.TargetActorId ?? string.Empty;
        next.LastAdmissionActorId = evt.TargetActorId ?? string.Empty;
        next.LastCommandId = evt.CommandId ?? string.Empty;
        next.LastCorrelationId = evt.CorrelationId ?? string.Empty;
        next.LastError = string.Empty;
        next.FireCount++;
        next.UpdatedAt = evt.DispatchedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.DispatchedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            TargetActorId = evt.TargetActorId ?? string.Empty,
            CommandId = evt.CommandId ?? string.Empty,
            CorrelationId = evt.CorrelationId ?? string.Empty,
            Manual = evt.Manual,
            Status = ScheduledDispatchFireStatusState.Dispatched,
        });
        return next;
    }

    private ScheduledDispatchState ApplyFireFailed(
        ScheduledDispatchState current,
        ScheduledDispatchFireFailedEvent evt)
    {
        var next = current.Clone();
        next.LastFireAt = evt.ScheduledFireAt?.ToDateTimeOffset();
        next.LastError = evt.Error ?? string.Empty;
        next.FireCount++;
        next.FailureCount++;
        next.UpdatedAt = evt.FailedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        UpsertFireRecord(next, evt.IdempotencyKey, new ScheduledDispatchFireRecordState
        {
            ScheduledFireAt = evt.ScheduledFireAt?.Clone(),
            CompletedAt = evt.FailedAt?.Clone(),
            IdempotencyKey = evt.IdempotencyKey ?? string.Empty,
            Error = evt.Error ?? string.Empty,
            Manual = evt.Manual,
            Status = ScheduledDispatchFireStatusState.Failed,
        });
        return next;
    }

    private static void UpsertFireRecord(
        ScheduledDispatchState state,
        string idempotencyKey,
        ScheduledDispatchFireRecordState record)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return;

        state.FireRecords[idempotencyKey] = record;
        if (state.FireRecords.Count <= MaxFireRecordCount)
            return;

        var keysToRemove = state.FireRecords
            .OrderBy(static x => ResolveTimestampSeconds(x.Value.CompletedAt))
            .ThenBy(static x => ResolveTimestampNanos(x.Value.CompletedAt))
            .ThenBy(static x => x.Key, StringComparer.Ordinal)
            .Take(state.FireRecords.Count - MaxFireRecordCount)
            .Select(static x => x.Key)
            .ToArray();
        foreach (var key in keysToRemove)
            state.FireRecords.Remove(key);
    }

    private static long ResolveTimestampSeconds(Timestamp? timestamp) =>
        timestamp?.Seconds ?? 0;

    private static int ResolveTimestampNanos(Timestamp? timestamp) =>
        timestamp?.Nanos ?? 0;

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

    private static string ResolvePayloadTypeUrl(EventEnvelope? envelope) =>
        envelope?.Payload?.TypeUrl ?? string.Empty;
}
