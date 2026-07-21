using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Scripting.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.run")]
public sealed class ServiceRunGAgent : GAgentBase<ServiceRunState>
{
    private const string TerminalNotificationRetryCallbackPrefix = "service-run-terminal-retry";
    private const int TerminalNotificationRetryInitialDelayMs = 250;
    private const int TerminalNotificationRetryMaxDelayMs = 30_000;

    public ServiceRunGAgent()
    {
        InitializeId();
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        await DeliverPendingTerminalNotificationAsync(ct);
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterServiceRunRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Record);
        ValidateRecord(command.Record);

        var existing = State.Record;
        if (existing != null && !string.IsNullOrWhiteSpace(existing.RunId))
        {
            EnsureExistingMatches(existing, command.Record);
            return;
        }

        var record = command.Record.Clone();
        if (record.CreatedAt == null)
            record.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        record.UpdatedAt = record.CreatedAt;
        if (record.Status == ServiceRunStatus.Unspecified)
            record.Status = ServiceRunStatus.Accepted;

        await PersistDomainEventAsync(new ServiceRunRegisteredEvent
        {
            Record = record,
        });
    }

    [EventHandler]
    public Task HandleUpdateStatusAsync(UpdateServiceRunStatusRequested command) =>
        ApplyStatusUpdateAsync(
            command,
            sourceTerminalAt: null,
            implementationTerminalEvidence: false);

    [EventHandler]
    public Task HandleRoleChatCompletedAsync(RoleChatSessionCompletedEvent terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var context = terminal.RunContext;
        ValidateImplementationTerminalIdentity(
            ServiceImplementationKind.Static,
            terminal.ActorId,
            context?.RunId,
            context?.CommandId,
            context?.CorrelationId,
            context?.CompletionNotificationActorId);
        var status = terminal.Outcome switch
        {
            RoleChatSessionOutcome.Completed => ServiceRunStatus.Completed,
            RoleChatSessionOutcome.Failed or RoleChatSessionOutcome.Blocked => ServiceRunStatus.Failed,
            _ => throw new InvalidOperationException("Role chat terminal outcome is required."),
        };
        EnsureTerminalStatusDoesNotConflict(status);
        var update = new UpdateServiceRunStatusRequested
        {
            RunId = context!.RunId,
            Status = status,
        };
        if (status == ServiceRunStatus.Completed)
            update.LastOutput = terminal.Content ?? string.Empty;
        else
            update.LastError = string.IsNullOrWhiteSpace(terminal.SafeMessage)
                ? terminal.FailureCode ?? string.Empty
                : terminal.SafeMessage;

        return ApplyStatusUpdateAsync(
            update,
            terminal.TerminalTime?.Clone()
                ?? throw new InvalidOperationException("Role chat terminal terminal_time is required."),
            implementationTerminalEvidence: true);
    }

    [EventHandler]
    public Task HandleScriptRunOutcomeAsync(ScriptRunOutcomeRecordedEvent terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ValidateImplementationTerminalIdentity(
            ServiceImplementationKind.Scripting,
            terminal.ActorId,
            terminal.ScriptRunId,
            terminal.CommandId,
            terminal.CorrelationId,
            terminal.CompletionNotificationActorId);
        if (terminal.StateVersion <= 0)
            throw new InvalidOperationException("Script terminal state_version is required.");

        var status = terminal.Status switch
        {
            ScriptRunOutcomeStatus.Succeeded => ServiceRunStatus.Completed,
            ScriptRunOutcomeStatus.Failed => ServiceRunStatus.Failed,
            _ => throw new InvalidOperationException("Script terminal status is required."),
        };
        EnsureTerminalStatusDoesNotConflict(status);
        var update = new UpdateServiceRunStatusRequested
        {
            RunId = terminal.ScriptRunId,
            Status = status,
        };
        if (status == ServiceRunStatus.Failed)
            update.LastError = terminal.Error ?? string.Empty;

        return ApplyStatusUpdateAsync(
            update,
            TimestampFromUnixTimeMilliseconds(
                terminal.OccurredAtUnixTimeMs,
                "Script terminal occurred_at_unix_time_ms"),
            implementationTerminalEvidence: true);
    }

    [EventHandler]
    public Task HandleTerminalNotificationRetryFiredAsync(
        ServiceRunTerminalNotificationRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        var pending = State.PendingTerminalNotification;
        if (pending == null ||
            !string.Equals(pending.DeliveryId, retry.DeliveryId, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var matchesScheduledAttempt =
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled &&
            retry.Attempt == State.TerminalNotificationAttempt;
        var matchesScheduleBeforeCommitRecovery =
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.Prepared &&
            retry.Attempt == State.TerminalNotificationAttempt + 1;
        return matchesScheduledAttempt || matchesScheduleBeforeCommitRecovery
            ? DeliverPendingTerminalNotificationAsync(failedAttempt: retry.Attempt)
            : Task.CompletedTask;
    }

    private async Task ApplyStatusUpdateAsync(
        UpdateServiceRunStatusRequested command,
        Timestamp? sourceTerminalAt,
        bool implementationTerminalEvidence)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = GetRegisteredRunForStatusUpdate(command);

        if (command.Status == ServiceRunStatus.Unspecified)
            return;

        if (IsTerminal(existing.Status) && existing.Status != command.Status)
        {
            await DeliverPendingTerminalNotificationAsync();
            return;
        }

        var outputChanged = command.LastOutput != null &&
                            !string.Equals(existing.LastOutput ?? string.Empty, command.LastOutput ?? string.Empty, StringComparison.Ordinal);
        var errorChanged = command.LastError != null &&
                           !string.Equals(existing.LastError ?? string.Empty, command.LastError ?? string.Empty, StringComparison.Ordinal);
        var shouldPrepareTerminalNotification =
            implementationTerminalEvidence &&
            IsTerminal(command.Status) &&
            HasCompletionNotificationTarget(existing.CompletionNotificationTarget) &&
            State.PendingTerminalNotification == null &&
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.Unspecified;
        if (existing.Status == command.Status && !outputChanged && !errorChanged)
        {
            if (shouldPrepareTerminalNotification)
            {
                await PersistDomainEventAsync(new ServiceRunTerminalNotificationPreparedEvent
                {
                    Notification = CreateTerminalNotification(
                        existing,
                        command,
                        sourceTerminalAt ?? existing.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow)),
                });
            }
            await DeliverPendingTerminalNotificationAsync();
            return;
        }

        var statusEvent = new ServiceRunStatusUpdatedEvent
        {
            RunId = existing.RunId,
            Status = command.Status,
            UpdatedAt = sourceTerminalAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            LastOutput = command.LastOutput,
            LastError = command.LastError,
        };
        if (shouldPrepareTerminalNotification)
        {
            await PersistDomainEventsAsync(
            [
                statusEvent,
                new ServiceRunTerminalNotificationPreparedEvent
                {
                    Notification = CreateTerminalNotification(existing, command, statusEvent.UpdatedAt),
                },
            ]);
        }
        else
        {
            await PersistDomainEventAsync(statusEvent);
        }

        await DeliverPendingTerminalNotificationAsync();
    }

    private ServiceRunRecord GetRegisteredRunForStatusUpdate(UpdateServiceRunStatusRequested command)
    {
        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.RunId))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' has no registered run; status update rejected.");
        }

        if (!string.IsNullOrWhiteSpace(command.RunId) &&
            !string.Equals(existing.RunId, command.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to run '{existing.RunId}' and cannot update run '{command.RunId}'.");
        }

        return existing;
    }

    protected override ServiceRunState TransitionState(ServiceRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceRunRegisteredEvent>(ApplyRegistered)
            .On<ServiceRunStatusUpdatedEvent>(ApplyStatusUpdated)
            .On<ServiceRunTerminalNotificationPreparedEvent>(ApplyTerminalNotificationPrepared)
            .On<ServiceRunTerminalNotificationRetryScheduledEvent>(ApplyTerminalNotificationRetryScheduled)
            .On<ServiceRunTerminalNotificationDispatchedEvent>(ApplyTerminalNotificationDispatched)
            .On<ServiceRunTerminalNotificationExpiredEvent>(ApplyTerminalNotificationExpired)
            .OrCurrent();

    private static ServiceRunState ApplyRegistered(ServiceRunState state, ServiceRunRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Record = evt.Record?.Clone() ?? new ServiceRunRecord();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.RunId}:registered";
        return next;
    }

    private static ServiceRunState ApplyStatusUpdated(ServiceRunState state, ServiceRunStatusUpdatedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new ServiceRunRecord();
        next.Record.Status = evt.Status;
        next.Record.UpdatedAt = evt.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        if (evt.LastOutput != null)
            next.Record.LastOutput = evt.LastOutput ?? string.Empty;
        if (evt.LastError != null)
            next.Record.LastError = evt.LastError ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.RunId}:status:{(int)evt.Status}";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationPrepared(
        ServiceRunState state,
        ServiceRunTerminalNotificationPreparedEvent evt)
    {
        var next = state.Clone();
        next.PendingTerminalNotification = evt.Notification?.Clone();
        next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.Prepared;
        next.TerminalNotificationAttempt = 0;
        next.TerminalNotificationRetryCallbackId = string.Empty;
        next.TerminalNotificationRetryAt = null;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:prepared";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationRetryScheduled(
        ServiceRunState state,
        ServiceRunTerminalNotificationRetryScheduledEvent evt)
    {
        var next = state.Clone();
        if (string.Equals(next.PendingTerminalNotification?.DeliveryId, evt.DeliveryId, StringComparison.Ordinal))
        {
            next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled;
            next.TerminalNotificationAttempt = evt.Attempt;
            next.TerminalNotificationRetryCallbackId = evt.CallbackId ?? string.Empty;
            next.TerminalNotificationRetryAt = evt.RetryAt?.Clone();
        }
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:retry-scheduled:{evt.Attempt}";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationDispatched(
        ServiceRunState state,
        ServiceRunTerminalNotificationDispatchedEvent evt)
    {
        var next = state.Clone();
        if (string.Equals(next.PendingTerminalNotification?.DeliveryId, evt.DeliveryId, StringComparison.Ordinal))
        {
            next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.Dispatched;
            ClearPendingTerminalNotification(next);
        }
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:dispatched";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationExpired(
        ServiceRunState state,
        ServiceRunTerminalNotificationExpiredEvent evt)
    {
        var next = state.Clone();
        if (string.Equals(next.PendingTerminalNotification?.DeliveryId, evt.DeliveryId, StringComparison.Ordinal))
        {
            next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.Expired;
            ClearPendingTerminalNotification(next);
        }
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:expired";
        return next;
    }

    private static void ClearPendingTerminalNotification(ServiceRunState state)
    {
        state.PendingTerminalNotification = null;
        state.TerminalNotificationRetryCallbackId = string.Empty;
        state.TerminalNotificationRetryAt = null;
    }

    private static bool IsTerminal(ServiceRunStatus status) =>
        status is ServiceRunStatus.Completed or ServiceRunStatus.Failed or ServiceRunStatus.Stopped;

    private void ValidateImplementationTerminalIdentity(
        ServiceImplementationKind expectedImplementationKind,
        string? sourceActorId,
        string? runId,
        string? commandId,
        string? correlationId,
        string? completionNotificationActorId)
    {
        EnsureInboundPublisherMatches(sourceActorId);

        var record = State.Record;
        if (record == null || string.IsNullOrWhiteSpace(record.RunId))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' has no registered run; terminal evidence rejected.");
        }

        if (record.ImplementationKind != expectedImplementationKind ||
            !string.Equals(record.TargetActorId, sourceActorId, StringComparison.Ordinal) ||
            !string.Equals(record.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(record.CommandId, commandId, StringComparison.Ordinal) ||
            !string.Equals(record.CorrelationId, correlationId, StringComparison.Ordinal) ||
            !string.Equals(Id, completionNotificationActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Committed implementation terminal evidence does not match registered service Run identity.");
        }
    }

    private void EnsureInboundPublisherMatches(string? sourceActorId)
    {
        if (ActiveInboundEnvelope == null)
            return;

        var publisherActorId = ActiveInboundEnvelope.Route?.PublisherActorId?.Trim() ?? string.Empty;
        if (!string.Equals(publisherActorId, sourceActorId?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Committed implementation terminal envelope publisher does not match payload source actor identity.");
        }
    }

    private void EnsureTerminalStatusDoesNotConflict(ServiceRunStatus incoming)
    {
        var current = State.Record?.Status ?? ServiceRunStatus.Unspecified;
        if (IsTerminal(current) && current != incoming)
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is already terminal as '{current}' and cannot adopt '{incoming}'.");
        }
    }

    private static Timestamp TimestampFromUnixTimeMilliseconds(long value, string fieldName)
    {
        if (value <= 0)
            throw new InvalidOperationException($"{fieldName} is required.");

        try
        {
            return Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(value));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException($"{fieldName} is invalid.", ex);
        }
    }

    private void EnsureExistingMatches(ServiceRunRecord existing, ServiceRunRecord incoming)
    {
        if (!string.Equals(existing.RunId, incoming.RunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to run '{existing.RunId}' and cannot register run '{incoming.RunId}'.");
        }
        if (!string.Equals(existing.ScopeId, incoming.ScopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to scope '{existing.ScopeId}' and cannot re-register under scope '{incoming.ScopeId}'.");
        }
        if (!string.Equals(existing.ServiceId, incoming.ServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to service '{existing.ServiceId}' and cannot re-register under service '{incoming.ServiceId}'.");
        }
        EnsureStableStringMatches("service key", existing.ServiceKey, incoming.ServiceKey);
        EnsureStableStringMatches("command", existing.CommandId, incoming.CommandId);
        EnsureStableStringMatches("correlation", existing.CorrelationId, incoming.CorrelationId);
        EnsureStableStringMatches("endpoint", existing.EndpointId, incoming.EndpointId);
        EnsureStableValueMatches("implementation kind", existing.ImplementationKind, incoming.ImplementationKind);
        EnsureStableStringMatches("revision", existing.RevisionId, incoming.RevisionId);
        EnsureStableStringMatches("deployment", existing.DeploymentId, incoming.DeploymentId);
        EnsureStableStringMatches("schedule", existing.ScheduleId, incoming.ScheduleId);
        EnsureStableValueMatches("service identity", existing.Identity, incoming.Identity);
        if (!string.Equals(existing.TargetActorId, incoming.TargetActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to target '{existing.TargetActorId}' and cannot re-register against target '{incoming.TargetActorId}'.");
        }
        if (!CompletionNotificationTargetsEqual(
                existing.CompletionNotificationTarget,
                incoming.CompletionNotificationTarget))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' cannot re-register with a different completion notification target.");
        }
    }

    private void EnsureStableStringMatches(string fieldName, string? existing, string? incoming)
    {
        if (!string.Equals(existing ?? string.Empty, incoming ?? string.Empty, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' {fieldName} identity cannot re-register with a different value.");
        }
    }

    private void EnsureStableValueMatches<T>(string fieldName, T existing, T incoming)
    {
        if (!EqualityComparer<T>.Default.Equals(existing, incoming))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' {fieldName} cannot re-register with a different value.");
        }
    }

    private static ServiceRunTerminalNotification CreateTerminalNotification(
        ServiceRunRecord existing,
        UpdateServiceRunStatusRequested command,
        Timestamp terminalAt) =>
        new()
        {
            DeliveryId = existing.CompletionNotificationTarget.DeliveryId.Trim(),
            RunId = existing.RunId,
            TargetActorId = existing.TargetActorId,
            CommandId = existing.CommandId,
            CorrelationId = existing.CorrelationId,
            Status = command.Status,
            Output = command.LastOutput ?? existing.LastOutput ?? string.Empty,
            Error = command.LastError ?? existing.LastError ?? string.Empty,
            TerminalAt = terminalAt.Clone(),
        };

    private async Task DeliverPendingTerminalNotificationAsync(
        CancellationToken ct = default,
        int? failedAttempt = null)
    {
        if (State.TerminalNotificationDeliveryStatus is
            ServiceRunTerminalNotificationDeliveryStatus.Dispatched or
            ServiceRunTerminalNotificationDeliveryStatus.Expired)
        {
            return;
        }

        var target = State.Record?.CompletionNotificationTarget?.Clone();
        var notification = State.PendingTerminalNotification?.Clone();
        if (!HasCompletionNotificationTarget(target) || notification == null)
            return;

        var now = DateTimeOffset.UtcNow;
        if (target!.ExpiresAtUnixMs <= now.ToUnixTimeMilliseconds())
        {
            await PersistDomainEventAsync(new ServiceRunTerminalNotificationExpiredEvent
            {
                DeliveryId = notification.DeliveryId,
                ExpiredAt = Timestamp.FromDateTimeOffset(now),
            }, ct);
            return;
        }

        try
        {
            await SendToAsync(
                target.ActorId.Trim(),
                notification,
                ct,
                new EventEnvelopePublishOptions
                {
                    Delivery = new EventEnvelopeDeliveryOptions
                    {
                        DeduplicationOperationId = $"service-run-terminal-{notification.DeliveryId}",
                    },
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleTerminalNotificationRetryAsync(
                target,
                notification,
                failedAttempt ?? State.TerminalNotificationAttempt,
                ct);
            return;
        }

        await PersistDomainEventAsync(new ServiceRunTerminalNotificationDispatchedEvent
        {
            DeliveryId = notification.DeliveryId,
            DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        }, ct);
    }

    private async Task ScheduleTerminalNotificationRetryAsync(
        ServiceRunCompletionNotificationTarget target,
        ServiceRunTerminalNotification notification,
        int failedAttempt,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        if (target.ExpiresAtUnixMs <= nowUnixMs)
        {
            await PersistDomainEventAsync(new ServiceRunTerminalNotificationExpiredEvent
            {
                DeliveryId = notification.DeliveryId,
                ExpiredAt = Timestamp.FromDateTimeOffset(now),
            }, ct);
            return;
        }

        var attempt = Math.Max(State.TerminalNotificationAttempt, failedAttempt) + 1;
        var retryDelayMs = CalculateTerminalNotificationRetryDelayMs(attempt);
        var remainingDeadlineMs = target.ExpiresAtUnixMs - nowUnixMs;
        var dueTime = TimeSpan.FromMilliseconds(Math.Min(retryDelayMs, remainingDeadlineMs));
        var retryAt = now.Add(dueTime);
        var callbackId = RuntimeCallbackKeyComposer.BuildCallbackId(
            TerminalNotificationRetryCallbackPrefix,
            notification.DeliveryId);

        await ScheduleSelfDurableTimeoutAsync(
            callbackId,
            dueTime,
            new ServiceRunTerminalNotificationRetryFiredEvent
            {
                DeliveryId = notification.DeliveryId,
                Attempt = attempt,
            },
            ct: ct);
        await PersistDomainEventAsync(new ServiceRunTerminalNotificationRetryScheduledEvent
        {
            DeliveryId = notification.DeliveryId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAt = Timestamp.FromDateTimeOffset(retryAt),
        }, ct);
    }

    private static long CalculateTerminalNotificationRetryDelayMs(int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 7);
        var delayMs = TerminalNotificationRetryInitialDelayMs * (1L << exponent);
        return Math.Min(delayMs, TerminalNotificationRetryMaxDelayMs);
    }

    private static bool HasCompletionNotificationTarget(ServiceRunCompletionNotificationTarget? target) =>
        target != null &&
        !string.IsNullOrWhiteSpace(target.ActorId) &&
        !string.IsNullOrWhiteSpace(target.DeliveryId);

    private static bool CompletionNotificationTargetsEqual(
        ServiceRunCompletionNotificationTarget? left,
        ServiceRunCompletionNotificationTarget? right) =>
        left == null && right == null ||
        left != null && right != null && left.Equals(right);

    private static void ValidateRecord(ServiceRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.RunId))
            throw new InvalidOperationException("run_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.ServiceId))
            throw new InvalidOperationException("service_id is required.");
        if (string.IsNullOrWhiteSpace(record.CommandId))
            throw new InvalidOperationException("command_id is required.");
    }
}
