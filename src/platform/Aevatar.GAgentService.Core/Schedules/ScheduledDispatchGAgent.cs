using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Schedules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Core.Schedules;

[GAgent("gagent.service.scheduled-dispatch")]
public sealed class ScheduledDispatchGAgent : GAgentBase<ScheduledDispatchState>
{
    private const string NextFireCallbackId = "scheduled-dispatch-next-fire";
    private const int MaxFireRecordCount = 128;
    private static readonly TimeSpan MaxNextFireCallbackHop = TimeSpan.FromDays(7);
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IScheduledServiceInvocationDispatchPort _serviceInvocationDispatchPort;

    public ScheduledDispatchGAgent(
        IActorDispatchPort dispatchPort,
        IScheduledServiceInvocationDispatchPort serviceInvocationDispatchPort)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _serviceInvocationDispatchPort = serviceInvocationDispatchPort
            ?? throw new ArgumentNullException(nameof(serviceInvocationDispatchPort));
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        if (State.Enabled && !string.IsNullOrWhiteSpace(State.CronExpression))
        {
            if (State.PendingNextFireAt != null)
            {
                var pendingNextFireAt = State.PendingNextFireAt.ToDateTimeOffset();
                var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
                await ActivateNextFireIntentAsync(pendingNextFireAt, previousLease, ct);
            }
            else
            {
                await EnsureNextFireScheduledAsync(DateTimeOffset.UtcNow, ct);
            }
        }
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
            .On<ScheduledDispatchDeletedEvent>(ApplyDeleted)
            .On<ScheduledDispatchNextFireIntentRecordedEvent>(ApplyNextFireIntentRecorded)
            .On<ScheduledDispatchNextFireScheduledEvent>(ApplyNextFireScheduled)
            .On<ScheduledDispatchFireStartedEvent>(ApplyFireStarted)
            .On<ScheduledDispatchFireDispatchedEvent>(ApplyFireDispatched)
            .On<ScheduledDispatchFireFailedEvent>(ApplyFireFailed)
            .OrCurrent();

    [EventHandler]
    public Task HandleConfigureAsync(ScheduledDispatchCreateCommand command) =>
        HandleConfigureAsync(
            command,
            command.ScheduleId,
            command.DisplayName,
            command.TargetActorId,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.Enabled,
            command.Headers,
            command.Target,
            command.ScheduleKind,
            isCreate: true);

    [EventHandler]
    public Task HandleConfigureAsync(ScheduledDispatchUpdateCommand command) =>
        HandleConfigureAsync(
            command,
            command.ScheduleId,
            command.DisplayName,
            command.TargetActorId,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.Enabled,
            command.Headers,
            command.Target,
            command.ScheduleKind,
            isCreate: false);

    [EventHandler]
    public async Task HandleEnsureAsync(ScheduledDispatchEnsureCommand command)
    {
        if (!IsConfigured())
        {
            await HandleConfigureAsync(
                command,
                command.ScheduleId,
                command.DisplayName,
                command.TargetActorId,
                command.TriggerEnvelope,
                command.CronExpression,
                command.Timezone,
                command.Enabled,
                command.Headers,
                command.Target,
                command.ScheduleKind,
                isCreate: true);
            return;
        }

        EnsureValidDefinition(
            command.TargetActorId,
            command.Target,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone);
        if (MatchesConfiguredDefinition(command))
            return;

        await HandleConfigureAsync(
            command,
            command.ScheduleId,
            command.DisplayName,
            command.TargetActorId,
            command.TriggerEnvelope,
            command.CronExpression,
            command.Timezone,
            command.Enabled,
            command.Headers,
            command.Target,
            command.ScheduleKind,
            isCreate: false);
    }

    private async Task HandleConfigureAsync(
        IMessage command,
        string scheduleId,
        string displayName,
        string? targetActorId,
        EventEnvelope triggerEnvelope,
        string cronExpression,
        string timezone,
        bool enabled,
        IEnumerable<KeyValuePair<string, string>> headers,
        ScheduledDispatchTargetState? target,
        ScheduledDispatchScheduleKindState scheduleKind,
        bool isCreate)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (State.Deleted)
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is deleted.");
        if (isCreate && IsConfigured())
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' already exists.");
        if (!isCreate && !IsConfigured())
            throw new InvalidOperationException($"Scheduled dispatch '{ResolveScheduleId()}' is not configured.");
        EnsureValidDefinition(targetActorId, target, triggerEnvelope, cronExpression, timezone);

        var now = DateTimeOffset.UtcNow;
        var configured = new ScheduledDispatchConfiguredEvent
        {
            ScheduleId = NormalizeRequired(scheduleId, nameof(scheduleId)),
            DisplayName = NormalizeOptional(displayName),
            TargetActorId = NormalizeOptional(targetActorId),
            TriggerEnvelope = triggerEnvelope.Clone(),
            CronExpression = NormalizeRequired(cronExpression, nameof(cronExpression)),
            Timezone = ScheduledDispatchCalculator.NormalizeTimezone(timezone),
            Enabled = enabled,
            ConfiguredAt = Timestamp.FromDateTimeOffset(now),
            PayloadTypeUrl = ResolvePayloadTypeUrl(triggerEnvelope),
            Target = NormalizeTarget(target),
            ScheduleKind = scheduleKind,
        };
        foreach (var (key, value) in NormalizeHeaders(headers))
            configured.Headers[key] = value;

        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(configured);

        if (enabled)
            await EnsureNextFireScheduledAsync(now, CancellationToken.None);
        else
            await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleEnableAsync(ScheduledDispatchEnableCommand command)
    {
        EnsureConfiguredForWrite("enable");

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
        EnsureConfiguredForWrite("disable");
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new ScheduledDispatchDisabledEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DisabledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleDeleteAsync(ScheduledDispatchDeleteCommand command)
    {
        EnsureConfiguredForWrite("delete");
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        await PersistDomainEventAsync(new ScheduledDispatchDeletedEvent
        {
            Reason = NormalizeOptional(command.Reason),
            DeletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
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
        if (!command.Manual && State.Deleted)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} ignored fire because it is deleted.", Id);
            return;
        }

        EnsureConfiguredForWrite(command.Manual ? "manual fire" : "fire");
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
        if (!command.Manual && ResolveCallbackFiredAt(inboundEnvelope) < scheduledFireAt)
        {
            var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
            await RecordNextFireIntentAsync(scheduledFireAt, ct);
            await ActivateNextFireIntentAsync(scheduledFireAt, previousLease, ct);
            return;
        }

        var idempotencyKey = ScheduledDispatchCalculator.BuildIdempotencyKey(ResolveScheduleId(), scheduledFireAt);
        if (HasTerminalFireRecord(idempotencyKey))
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
            var receipt = await DispatchPreparedTargetAsync(prepared, ct);
            if (!receipt.Accepted)
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
                    TargetActorId = receipt.TargetActorId,
                    CommandId = receipt.CommandId,
                    CorrelationId = receipt.CorrelationId,
                    Manual = command.Manual,
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Scheduled dispatch {ActorId} fire was canceled.", Id);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Scheduled dispatch {ActorId} dispatch failed.", Id);
            await PersistFireFailedAsync(scheduledFireAt, idempotencyKey, ex.Message, command.Manual, CancellationToken.None);
        }

        if (!command.Manual)
            await EnsureNextFireScheduledAsync(scheduledFireAt, CancellationToken.None);
    }

    private async Task<ScheduledDispatchReceipt> DispatchPreparedTargetAsync(
        ScheduledDispatchEnvelope prepared,
        CancellationToken ct)
    {
        if (prepared.TargetKind == ScheduledDispatchTargetKindState.ServiceInvocation)
        {
            if (prepared.Envelope.Payload?.TryUnpack<ServiceInvocationRequest>(out var request) != true)
                throw new InvalidOperationException("Scheduled service invocation payload is not configured.");

            var receipt = await _serviceInvocationDispatchPort.DispatchAsync(
                new ScheduledServiceInvocationDispatchRequest(
                    request,
                    ToRuntimeAuth(State.Target?.ServiceInvocation?.Auth),
                    ReadOnlyCopy(prepared.Headers ?? EmptyHeaders),
                    ProjectSenderNyxIdAccessTokenToWorkflowCallerCredential:
                        State.ScheduleKind == ScheduledDispatchScheduleKindState.Workflow),
                ct);
            return new ScheduledDispatchReceipt(
                receipt.Accepted,
                receipt.CommandId,
                receipt.TargetActorId,
                receipt.CorrelationId);
        }

        var admission = await _dispatchPort.DispatchAsync(prepared.TargetActorId, prepared.Envelope, ct);
        return new ScheduledDispatchReceipt(
            admission.Accepted,
            admission.CommandId,
            admission.ActorId,
            admission.CorrelationId);
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
        var headers = BuildFireHeaders(scheduledFireAtUtc, idempotencyKey);
        if (ResolveTargetKind() == ScheduledDispatchTargetKindState.ServiceInvocation)
            return await BuildServiceInvocationDispatchEnvelopeAsync(headers, idempotencyKey, ct);

        var envelope = State.TriggerEnvelope?.Clone()
            ?? throw new InvalidOperationException("Scheduled dispatch trigger envelope is not configured.");
        if (envelope.Payload == null)
            throw new InvalidOperationException("Scheduled dispatch trigger envelope payload is not configured.");

        envelope.Id = idempotencyKey;
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(ResolveScheduleId(), ResolveDispatchTargetActorId());
        envelope.Runtime = null;
        var propagation = envelope.EnsurePropagation();
        if (string.IsNullOrWhiteSpace(propagation.CorrelationId))
            propagation.CorrelationId = idempotencyKey;
        foreach (var (key, value) in headers)
            propagation.Baggage[key] = value;

        if (envelope.Payload.TryUnpack<ServiceInvocationRequest>(out var serviceInvocationRequest))
        {
            serviceInvocationRequest.CommandId = idempotencyKey;
            serviceInvocationRequest.CorrelationId = propagation.CorrelationId;
            envelope.Payload = Any.Pack(serviceInvocationRequest);
            return new ScheduledDispatchEnvelope(
                ResolveDispatchTargetActorId(),
                ResolveTargetKind(),
                envelope);
        }

        return new ScheduledDispatchEnvelope(
            ResolveDispatchTargetActorId(),
            ResolveTargetKind(),
            envelope);
    }

    private Task<ScheduledDispatchEnvelope> BuildServiceInvocationDispatchEnvelopeAsync(
        IReadOnlyDictionary<string, string> headers,
        string idempotencyKey,
        CancellationToken ct)
    {
        var target = State.Target?.ServiceInvocation
            ?? throw new InvalidOperationException("Scheduled service invocation target is not configured.");
        ct.ThrowIfCancellationRequested();
        var request = new ServiceInvocationRequest
        {
            Identity = target.Identity?.Clone(),
            EndpointId = target.EndpointId ?? string.Empty,
            Payload = target.Payload?.Clone()
                ?? throw new InvalidOperationException("Scheduled service invocation payload is not configured."),
            CommandId = idempotencyKey,
            CorrelationId = idempotencyKey,
            RevisionId = target.RevisionId ?? string.Empty,
        };
        if (target.Caller != null)
            request.Caller = target.Caller.Clone();

        var envelope = new EventEnvelope
        {
            Id = idempotencyKey,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(request),
            Route = EnvelopeRouteSemantics.CreateDirect(
                ResolveScheduleId(),
                ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = idempotencyKey,
            },
        };
        foreach (var (key, value) in headers)
            envelope.Propagation.Baggage[key] = value;

        return Task.FromResult(new ScheduledDispatchEnvelope(
            ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId,
            ScheduledDispatchTargetKindState.ServiceInvocation,
            envelope,
            ReadOnlyCopy(headers)));
    }

    private static ScheduledServiceInvocationAuth? ToRuntimeAuth(ScheduledServiceInvocationAuthState? auth)
    {
        if (auth?.SenderNyxId == null)
            return null;

        return new ScheduledServiceInvocationAuth(new ScheduledServiceInvocationNyxIdCredentialSource(
            new ScheduledServiceInvocationNyxIdSubjectRef(
                auth.SenderNyxId.Subject?.Platform ?? string.Empty,
                auth.SenderNyxId.Subject?.Tenant ?? string.Empty,
                auth.SenderNyxId.Subject?.ExternalUserId ?? string.Empty),
            auth.SenderNyxId.Scope ?? string.Empty));
    }

    private static IReadOnlyDictionary<string, string> ReadOnlyCopy(IReadOnlyDictionary<string, string> headers) =>
        new Dictionary<string, string>(headers, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal);

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
        ScheduledDispatchTargetKindState TargetKind,
        EventEnvelope Envelope,
        IReadOnlyDictionary<string, string>? Headers = null);

    private sealed record ScheduledDispatchReceipt(
        bool Accepted,
        string CommandId,
        string TargetActorId,
        string CorrelationId);

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

        ct.ThrowIfCancellationRequested();
        var previousLease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToRuntime(State.NextFireLease);
        var pendingNextFireAt = State.PendingNextFireAt?.ToDateTimeOffset();
        if (pendingNextFireAt != nextFireAtUtc)
            await RecordNextFireIntentAsync(nextFireAtUtc, ct);

        await ActivateNextFireIntentAsync(nextFireAtUtc, previousLease, ct);
    }

    private async Task RecordNextFireIntentAsync(DateTimeOffset nextFireAtUtc, CancellationToken ct)
    {
        await PersistDomainEventAsync(new ScheduledDispatchNextFireIntentRecordedEvent
        {
            NextFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
            RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }, ct);
    }

    private async Task ActivateNextFireIntentAsync(
        DateTimeOffset nextFireAtUtc,
        RuntimeCallbackLease? previousLease,
        CancellationToken ct)
    {
        var dueTime = ComputeNextFireCallbackDueTime(nextFireAtUtc, DateTimeOffset.UtcNow);
        var lease = await ScheduleSelfDurableTimeoutAsync(
            NextFireCallbackId,
            dueTime,
            new ScheduledDispatchFireCommand
            {
                ScheduledFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
                Manual = false,
            },
            ct: ct);

        try
        {
            await PersistDomainEventAsync(new ScheduledDispatchNextFireScheduledEvent
            {
                NextFireAt = Timestamp.FromDateTimeOffset(nextFireAtUtc),
                Lease = ScheduledDispatchRuntimeCallbackLeaseStateCodec.ToState(lease),
                ScheduledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, ct);
        }
        catch
        {
            await CancelNextFireLeaseAsync(lease, CancellationToken.None);
            throw;
        }

        await CancelNextFireLeaseAsync(previousLease, CancellationToken.None);
    }

    private static TimeSpan ComputeNextFireCallbackDueTime(
        DateTimeOffset nextFireAtUtc,
        DateTimeOffset nowUtc)
    {
        var dueTime = ScheduledDispatchCalculator.ComputeDueTime(nextFireAtUtc, nowUtc);
        return dueTime <= MaxNextFireCallbackHop ? dueTime : MaxNextFireCallbackHop;
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

    private bool HasTerminalFireRecord(string idempotencyKey)
    {
        if (!State.FireRecords.TryGetValue(idempotencyKey, out var record))
            return false;

        return record.Status is ScheduledDispatchFireStatusState.Dispatched or ScheduledDispatchFireStatusState.Failed;
    }

    private DateTimeOffset ResolveScheduledFireAt(ScheduledDispatchFireCommand command)
    {
        if (command.ScheduledFireAt != null)
            return command.ScheduledFireAt.ToDateTimeOffset().ToUniversalTime();

        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset ResolveCallbackFiredAt(EventEnvelope? envelope)
    {
        if (envelope != null &&
            RuntimeCallbackEnvelopeStateReader.TryRead(envelope, out var state) &&
            state.FiredAtUnixTimeMs > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(state.FiredAtUnixTimeMs);
        }

        return DateTimeOffset.UtcNow;
    }

    private string ResolveScheduleId() =>
        string.IsNullOrWhiteSpace(State.ScheduleId) ? Id : State.ScheduleId;

    private string ResolveDispatchTargetActorId() =>
        string.IsNullOrWhiteSpace(State.TargetActorId)
            ? ScheduledDispatchAdapterConventions.ServiceInvocationTargetActorId
            : State.TargetActorId.Trim();

    private ScheduledDispatchTargetKindState ResolveTargetKind() =>
        State.Target?.Kind == ScheduledDispatchTargetKindState.ServiceInvocation
            ? ScheduledDispatchTargetKindState.ServiceInvocation
            : ScheduledDispatchTargetKindState.Envelope;

    private bool IsConfigured() =>
        !State.Deleted &&
        !string.IsNullOrWhiteSpace(State.ScheduleId) &&
        !string.IsNullOrWhiteSpace(State.CronExpression) &&
        State.TriggerEnvelope?.Payload != null;

    private bool MatchesConfiguredDefinition(ScheduledDispatchEnsureCommand command)
    {
        var normalizedTarget = NormalizeTarget(command.Target);
        var normalizedHeaders = NormalizeHeaders(command.Headers);
        var normalizedScheduleId = NormalizeRequired(command.ScheduleId, nameof(command.ScheduleId));
        var normalizedDisplayName = NormalizeOptional(command.DisplayName);
        var normalizedTargetActorId = NormalizeOptional(command.TargetActorId);
        var normalizedCronExpression = NormalizeRequired(command.CronExpression, nameof(command.CronExpression));
        var normalizedTimezone = ScheduledDispatchCalculator.NormalizeTimezone(command.Timezone);

        return string.Equals(State.ScheduleId, normalizedScheduleId, StringComparison.Ordinal) &&
               string.Equals(State.DisplayName, normalizedDisplayName, StringComparison.Ordinal) &&
               string.Equals(State.TargetActorId, normalizedTargetActorId, StringComparison.Ordinal) &&
               string.Equals(State.CronExpression, normalizedCronExpression, StringComparison.Ordinal) &&
               string.Equals(State.Timezone, normalizedTimezone, StringComparison.Ordinal) &&
               string.Equals(State.PayloadTypeUrl, ResolvePayloadTypeUrl(command.TriggerEnvelope), StringComparison.Ordinal) &&
               State.Enabled == command.Enabled &&
               State.ScheduleKind == command.ScheduleKind &&
               DictionaryEquals(State.Headers, normalizedHeaders) &&
               EnvelopePayloadEquals(State.TriggerEnvelope, command.TriggerEnvelope) &&
               TargetEquals(NormalizeTarget(State.Target), normalizedTarget);
    }

    private void EnsureConfiguredForWrite(string operation)
    {
        if (!IsConfigured())
            throw new InvalidOperationException(
                $"Scheduled dispatch '{ResolveScheduleId()}' cannot {operation} because it is not configured.");
    }

    private static void EnsureValidDefinition(
        string? targetActorId,
        ScheduledDispatchTargetState? target,
        EventEnvelope? triggerEnvelope,
        string cronExpression,
        string timezone)
    {
        if (triggerEnvelope == null || triggerEnvelope.Payload == null)
            throw new ArgumentException("Trigger envelope with payload is required.", nameof(triggerEnvelope));
        _ = NormalizeTarget(target);
        _ = NormalizeRequired(targetActorId, nameof(targetActorId));
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

    private static ScheduledDispatchTargetState NormalizeTarget(ScheduledDispatchTargetState? target)
    {
        if (target == null)
            return new ScheduledDispatchTargetState();

        return target.Kind switch
        {
            ScheduledDispatchTargetKindState.ServiceInvocation => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.ServiceInvocation,
                ServiceInvocation = NormalizeServiceInvocationTarget(target.ServiceInvocation),
            },
            ScheduledDispatchTargetKindState.Envelope => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = NormalizeOptional(target.ActorId),
                Envelope = target.Envelope?.Clone(),
            },
            _ => new ScheduledDispatchTargetState
            {
                Kind = ScheduledDispatchTargetKindState.Envelope,
                ActorId = NormalizeOptional(target.ActorId),
                Envelope = target.Envelope?.Clone(),
            },
        };
    }

    private static ScheduledServiceInvocationTargetState NormalizeServiceInvocationTarget(
        ScheduledServiceInvocationTargetState? serviceInvocation)
    {
        if (serviceInvocation == null)
            return new ScheduledServiceInvocationTargetState();

        return new ScheduledServiceInvocationTargetState
        {
            Identity = serviceInvocation.Identity?.Clone(),
            EndpointId = NormalizeOptional(serviceInvocation.EndpointId),
            Payload = serviceInvocation.Payload?.Clone(),
            RevisionId = NormalizeOptional(serviceInvocation.RevisionId),
            Caller = serviceInvocation.Caller?.Clone(),
            Auth = NormalizeServiceInvocationAuth(serviceInvocation.Auth),
        };
    }

    private static ScheduledServiceInvocationAuthState? NormalizeServiceInvocationAuth(
        ScheduledServiceInvocationAuthState? auth)
    {
        if (auth?.SenderNyxId == null)
            return null;

        return new ScheduledServiceInvocationAuthState
        {
            SenderNyxId = new ScheduledServiceInvocationNyxIdCredentialSourceState
            {
                Subject = new ScheduledServiceInvocationNyxIdSubjectRefState
                {
                    Platform = NormalizeOptional(auth.SenderNyxId.Subject?.Platform),
                    Tenant = NormalizeOptional(auth.SenderNyxId.Subject?.Tenant),
                    ExternalUserId = NormalizeOptional(auth.SenderNyxId.Subject?.ExternalUserId),
                },
                Scope = NormalizeOptional(auth.SenderNyxId.Scope),
            },
        };
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
        next.Target = NormalizeTarget(evt.Target);
        next.ScheduleKind = evt.ScheduleKind;
        if (!next.Enabled)
        {
            next.NextFireAt = null;
            next.NextFireLease = null;
            next.PendingNextFireAt = null;
            next.PendingNextFireRequestedAt = null;
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
        next.PendingNextFireAt = null;
        next.PendingNextFireRequestedAt = null;
        next.UpdatedAt = evt.DisabledAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        return next;
    }

    private ScheduledDispatchState ApplyDeleted(ScheduledDispatchState current, ScheduledDispatchDeletedEvent evt)
    {
        var next = ApplyDisabled(current, new ScheduledDispatchDisabledEvent
        {
            Reason = evt.Reason ?? string.Empty,
            DisabledAt = evt.DeletedAt?.Clone(),
        });
        var deletedAt = evt.DeletedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        next.Deleted = true;
        next.DeletedAt = deletedAt;
        next.UpdatedAt = deletedAt;
        return next;
    }

    private static ScheduledDispatchState ApplyNextFireIntentRecorded(
        ScheduledDispatchState current,
        ScheduledDispatchNextFireIntentRecordedEvent evt)
    {
        var next = current.Clone();
        next.PendingNextFireAt = evt.NextFireAt?.Clone();
        next.PendingNextFireRequestedAt = evt.RequestedAt?.Clone();
        next.UpdatedAt =
            evt.RequestedAt?.ToDateTimeOffset() ??
            evt.NextFireAt?.ToDateTimeOffset() ??
            DateTimeOffset.UtcNow;
        return next;
    }

    private static ScheduledDispatchState ApplyNextFireScheduled(
        ScheduledDispatchState current,
        ScheduledDispatchNextFireScheduledEvent evt)
    {
        var next = current.Clone();
        next.NextFireAt = evt.NextFireAt?.ToDateTimeOffset();
        next.NextFireLease = evt.Lease?.Clone();
        next.PendingNextFireAt = null;
        next.PendingNextFireRequestedAt = null;
        next.UpdatedAt =
            evt.ScheduledAt?.ToDateTimeOffset() ??
            evt.NextFireAt?.ToDateTimeOffset() ??
            DateTimeOffset.UtcNow;
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

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EnvelopePayloadEquals(EventEnvelope? left, EventEnvelope? right)
    {
        if (left?.Payload == null || right?.Payload == null)
            return left?.Payload == null && right?.Payload == null;

        return string.Equals(left.Payload.TypeUrl, right.Payload.TypeUrl, StringComparison.Ordinal) &&
               left.Payload.Value.Equals(right.Payload.Value);
    }

    private static bool TargetEquals(ScheduledDispatchTargetState? left, ScheduledDispatchTargetState? right) =>
        Equals(left, right);

    private static string ResolvePayloadTypeUrl(EventEnvelope? envelope) =>
        envelope?.Payload?.TypeUrl ?? string.Empty;

}
