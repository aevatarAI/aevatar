using System.Globalization;
using Aevatar.AI.Abstractions;
using Aevatar.ContentArtifacts.Abstractions;
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
using Microsoft.Extensions.Logging;

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
    public async Task HandleAttachResultArtifactsAsync(AttachServiceRunResultArtifactsRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var record = GetRegisteredRun(command.RunId, "result artifact attachment");
        var additions = SelectNewResultArtifacts(record.ResultArtifacts, command.ResultArtifacts);
        if (additions.Count == 0)
            return;
        if (State.LastAppliedEventVersion != command.ExpectedStateVersion)
        {
            throw new InvalidOperationException(
                $"Service run state version is {State.LastAppliedEventVersion}, not {command.ExpectedStateVersion}.");
        }

        var attached = new ServiceRunResultArtifactsAttachedEvent
        {
            RunId = record.RunId,
            AttachedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        attached.ResultArtifacts.Add(additions);
        await PersistDomainEventAsync(attached);
    }

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
            RoleChatSessionOutcome.Failed or
            RoleChatSessionOutcome.Blocked => ServiceRunStatus.Failed,
            RoleChatSessionOutcome.OutcomeUncertain => ServiceRunStatus.OutcomeUncertain,
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

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTerminalNotificationRetryFiredAsync(
        ServiceRunTerminalNotificationRetryFiredEvent retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        var pending = State.PendingTerminalNotification;
        if (pending == null ||
            !string.Equals(pending.DeliveryId, retry.DeliveryId, StringComparison.Ordinal))
        {
            return;
        }

        var matchesScheduledAttempt =
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled &&
            retry.Attempt == State.TerminalNotificationAttempt;
        var matchesScheduledNextAttemptRecovery =
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled &&
            retry.Attempt == State.TerminalNotificationAttempt + 1;
        var matchesScheduleBeforeCommitRecovery =
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.Prepared &&
            retry.Attempt == State.TerminalNotificationAttempt + 1;
        if (!matchesScheduledAttempt &&
            !matchesScheduledNextAttemptRecovery &&
            !matchesScheduleBeforeCommitRecovery)
        {
            return;
        }

        if (matchesScheduledNextAttemptRecovery || matchesScheduleBeforeCommitRecovery)
        {
            await PersistDomainEventAsync(new ServiceRunTerminalNotificationRetryScheduledEvent
            {
                DeliveryId = retry.DeliveryId,
                Attempt = retry.Attempt,
                CallbackId = BuildTerminalNotificationRetryCallbackId(retry.DeliveryId),
                RetryAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });
        }

        await DeliverPendingTerminalNotificationAsync(
            CancellationToken.None,
            retry.Attempt);
    }

    private async Task ApplyStatusUpdateAsync(
        UpdateServiceRunStatusRequested command,
        Timestamp? sourceTerminalAt,
        bool implementationTerminalEvidence)
    {
        ArgumentNullException.ThrowIfNull(command);
        var existing = GetRegisteredRunForStatusUpdate(command);
        var resultArtifactAdditions = SelectNewResultArtifacts(existing.ResultArtifacts, command.ResultArtifacts);

        if (command.Status == ServiceRunStatus.Unspecified)
            return;

        EnsureStatusTransitionAllowed(existing.Status, command.Status);

        var outputChanged = command.LastOutput != null &&
                            !string.Equals(existing.LastOutput ?? string.Empty, command.LastOutput ?? string.Empty, StringComparison.Ordinal);
        var errorChanged = command.LastError != null &&
                           !string.Equals(existing.LastError ?? string.Empty, command.LastError ?? string.Empty, StringComparison.Ordinal);
        var resultArtifactsChanged = resultArtifactAdditions.Count > 0;
        var shouldPrepareTerminalNotification =
            implementationTerminalEvidence &&
            IsNotifiableTerminalStatus(command.Status) &&
            HasCompletionNotificationTarget(existing.CompletionNotificationTarget) &&
            State.PendingTerminalNotification == null &&
            State.TerminalNotificationDeliveryStatus == ServiceRunTerminalNotificationDeliveryStatus.Unspecified;
        if (existing.Status == command.Status && !outputChanged && !errorChanged && !resultArtifactsChanged)
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
        statusEvent.ResultArtifacts.Add(resultArtifactAdditions);
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

    private ServiceRunRecord GetRegisteredRunForStatusUpdate(UpdateServiceRunStatusRequested command) =>
        GetRegisteredRun(command.RunId, "status update");

    private ServiceRunRecord GetRegisteredRun(string? runId, string operation)
    {
        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.RunId))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' has no registered run; {operation} rejected.");
        }

        if (!string.IsNullOrWhiteSpace(runId) &&
            !string.Equals(existing.RunId, runId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Service run actor '{Id}' is bound to run '{existing.RunId}' and cannot apply {operation} for run '{runId}'.");
        }

        return existing;
    }

    protected override ServiceRunState TransitionState(ServiceRunState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceRunRegisteredEvent>(ApplyRegistered)
            .On<ServiceRunStatusUpdatedEvent>(ApplyStatusUpdated)
            .On<ServiceRunResultArtifactsAttachedEvent>(ApplyResultArtifactsAttached)
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
        MergeResultArtifacts(next.Record.ResultArtifacts, evt.ResultArtifacts);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.RunId}:status:{(int)evt.Status}";
        return next;
    }

    private static ServiceRunState ApplyResultArtifactsAttached(
        ServiceRunState state,
        ServiceRunResultArtifactsAttachedEvent evt)
    {
        var next = state.Clone();
        next.Record ??= new ServiceRunRecord();
        MergeResultArtifacts(next.Record.ResultArtifacts, evt.ResultArtifacts);
        next.Record.UpdatedAt = evt.AttachedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.RunId}:result-artifacts:attached";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationPrepared(
        ServiceRunState state,
        ServiceRunTerminalNotificationPreparedEvent evt)
    {
        if (state.TerminalNotificationDeliveryStatus is
            ServiceRunTerminalNotificationDeliveryStatus.Dispatched or
            ServiceRunTerminalNotificationDeliveryStatus.Expired)
        {
            return state;
        }

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
        if (!HasMatchingPendingTerminalNotification(state, evt.DeliveryId) ||
            !IsTerminalNotificationDeliveryPending(state) ||
            evt.Attempt != state.TerminalNotificationAttempt + 1)
        {
            return state;
        }

        var next = state.Clone();
        next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled;
        next.TerminalNotificationAttempt = evt.Attempt;
        next.TerminalNotificationRetryCallbackId = evt.CallbackId ?? string.Empty;
        next.TerminalNotificationRetryAt = evt.RetryAt?.Clone();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:retry-scheduled:{evt.Attempt}";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationDispatched(
        ServiceRunState state,
        ServiceRunTerminalNotificationDispatchedEvent evt)
    {
        if (!IsEligibleTerminalNotificationTransition(
                state,
                evt.DeliveryId,
                evt.HasAttempt,
                evt.Attempt))
        {
            return state;
        }

        var next = state.Clone();
        next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.Dispatched;
        if (evt.HasAttempt)
            next.TerminalNotificationAttempt = Math.Max(next.TerminalNotificationAttempt, evt.Attempt);
        ClearPendingTerminalNotification(next);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:dispatched";
        return next;
    }

    private static ServiceRunState ApplyTerminalNotificationExpired(
        ServiceRunState state,
        ServiceRunTerminalNotificationExpiredEvent evt)
    {
        if (!IsEligibleTerminalNotificationTransition(
                state,
                evt.DeliveryId,
                evt.HasAttempt,
                evt.Attempt))
        {
            return state;
        }

        var next = state.Clone();
        next.TerminalNotificationDeliveryStatus = ServiceRunTerminalNotificationDeliveryStatus.Expired;
        if (evt.HasAttempt)
            next.TerminalNotificationAttempt = Math.Max(next.TerminalNotificationAttempt, evt.Attempt);
        ClearPendingTerminalNotification(next);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record?.RunId}:terminal-notification:expired";
        return next;
    }

    private static bool IsEligibleTerminalNotificationTransition(
        ServiceRunState state,
        string deliveryId,
        bool hasAttempt,
        int attempt) =>
        HasMatchingPendingTerminalNotification(state, deliveryId) &&
        IsTerminalNotificationDeliveryPending(state) &&
        (!hasAttempt ||
         attempt == state.TerminalNotificationAttempt ||
         attempt == state.TerminalNotificationAttempt + 1);

    private static bool HasMatchingPendingTerminalNotification(
        ServiceRunState state,
        string deliveryId) =>
        !string.IsNullOrWhiteSpace(deliveryId) &&
        string.Equals(
            state.PendingTerminalNotification?.DeliveryId,
            deliveryId,
            StringComparison.Ordinal);

    private static bool IsTerminalNotificationDeliveryPending(ServiceRunState state) =>
        state.TerminalNotificationDeliveryStatus is
            ServiceRunTerminalNotificationDeliveryStatus.Prepared or
            ServiceRunTerminalNotificationDeliveryStatus.RetryScheduled;

    private static void ClearPendingTerminalNotification(ServiceRunState state)
    {
        state.PendingTerminalNotification = null;
        state.TerminalNotificationRetryCallbackId = string.Empty;
        state.TerminalNotificationRetryAt = null;
    }

    private static bool IsTerminal(ServiceRunStatus status) =>
        status is ServiceRunStatus.Completed or
            ServiceRunStatus.Failed or
            ServiceRunStatus.Stopped or
            ServiceRunStatus.OutcomeUncertain;

    private static bool IsNotifiableTerminalStatus(ServiceRunStatus status) =>
        status is ServiceRunStatus.Completed or
            ServiceRunStatus.Failed or
            ServiceRunStatus.Stopped;

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
        EnsureStatusTransitionAllowed(current, incoming);
    }

    private void EnsureStatusTransitionAllowed(ServiceRunStatus current, ServiceRunStatus incoming)
    {
        if (current == incoming || current is ServiceRunStatus.Unspecified or ServiceRunStatus.Accepted)
            return;

        if (current == ServiceRunStatus.OutcomeUncertain &&
            incoming is ServiceRunStatus.Completed or ServiceRunStatus.Failed)
        {
            return;
        }

        if (IsTerminal(current))
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
        Timestamp terminalAt)
    {
        var notification = new ServiceRunTerminalNotification
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
        notification.ResultArtifacts.Add(existing.ResultArtifacts.Select(static artifact => artifact.Clone()));
        MergeResultArtifacts(notification.ResultArtifacts, command.ResultArtifacts);
        return notification;
    }

    private static IReadOnlyList<ContentArtifactReference> SelectNewResultArtifacts(
        IEnumerable<ContentArtifactReference> existing,
        IEnumerable<ContentArtifactReference> incoming)
    {
        var known = existing.ToDictionary(ResultArtifactKey, static artifact => artifact, StringComparer.Ordinal);
        var additions = new List<ContentArtifactReference>();
        foreach (var artifact in incoming)
        {
            ValidateResultArtifact(artifact);
            var key = ResultArtifactKey(artifact);
            if (known.TryGetValue(key, out var current))
            {
                if (!string.Equals(current.ContentHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"ContentArtifact '{artifact.ArtifactId}' revision '{artifact.RevisionId}' has a conflicting content hash.");
                }
                if (!string.Equals(current.MediaType, artifact.MediaType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"ContentArtifact '{artifact.ArtifactId}' revision '{artifact.RevisionId}' has a conflicting media type.");
                }
                continue;
            }

            var clone = artifact.Clone();
            known.Add(key, clone);
            additions.Add(clone);
        }
        return additions;
    }

    private static void MergeResultArtifacts(
        Google.Protobuf.Collections.RepeatedField<ContentArtifactReference> destination,
        IEnumerable<ContentArtifactReference> incoming)
    {
        destination.Add(SelectNewResultArtifacts(destination, incoming));
    }

    private static void ValidateResultArtifact(ContentArtifactReference artifact)
    {
        if (artifact == null || string.IsNullOrWhiteSpace(artifact.ArtifactId))
            throw new InvalidOperationException("ContentArtifact result reference artifact_id is required.");
        if (string.IsNullOrWhiteSpace(artifact.RevisionId))
            throw new InvalidOperationException("ContentArtifact result reference revision_id is required.");
        if (string.IsNullOrWhiteSpace(artifact.MediaType))
            throw new InvalidOperationException("ContentArtifact result reference media_type is required.");
        if (artifact.ContentHash?.Length != 64)
            throw new InvalidOperationException("ContentArtifact result reference content_hash must be a SHA-256 hex digest.");
        try
        {
            _ = Convert.FromHexString(artifact.ContentHash);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "ContentArtifact result reference content_hash must be a SHA-256 hex digest.", ex);
        }
    }

    private static string ResultArtifactKey(ContentArtifactReference artifact) =>
        $"{artifact.ArtifactId}\n{artifact.RevisionId}";

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

        var attempt = Math.Max(State.TerminalNotificationAttempt, failedAttempt ?? 0);
        var now = DateTimeOffset.UtcNow;
        if (target!.ExpiresAtUnixMs <= now.ToUnixTimeMilliseconds())
        {
            await PersistDomainEventAsync(new ServiceRunTerminalNotificationExpiredEvent
            {
                DeliveryId = notification.DeliveryId,
                ExpiredAt = Timestamp.FromDateTimeOffset(now),
                Attempt = attempt,
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
                        OperationId = $"service-run-terminal-{notification.DeliveryId}",
                    },
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleTerminalNotificationRetryAsync(
                target,
                notification,
                attempt,
                ct);
            return;
        }

        try
        {
            await PersistDomainEventAsync(new ServiceRunTerminalNotificationDispatchedEvent
            {
                DeliveryId = notification.DeliveryId,
                DispatchedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Attempt = attempt,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ScheduleTerminalNotificationRetryAsync(
                target,
                notification,
                attempt,
                ct);
            throw;
        }
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
                Attempt = Math.Max(State.TerminalNotificationAttempt, failedAttempt),
            }, ct);
            return;
        }

        var attempt = Math.Max(State.TerminalNotificationAttempt, failedAttempt) + 1;
        var retryDelayMs = CalculateTerminalNotificationRetryDelayMs(attempt);
        var remainingDeadlineMs = target.ExpiresAtUnixMs - nowUnixMs;
        var dueTime = TimeSpan.FromMilliseconds(Math.Min(retryDelayMs, remainingDeadlineMs));
        var retryAt = now.Add(dueTime);
        var callbackId = BuildTerminalNotificationRetryCallbackId(notification.DeliveryId);
        var retryFired = new ServiceRunTerminalNotificationRetryFiredEvent
        {
            DeliveryId = notification.DeliveryId,
            Attempt = attempt,
        };
        var retryOptions = BuildTerminalNotificationRetryOptions(callbackId, attempt);
        try
        {
            await ScheduleSelfDurableTimeoutAsync(
                callbackId,
                dueTime,
                retryFired,
                retryOptions,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var canPublishImmediateRecovery =
                State.TerminalNotificationDeliveryStatus ==
                    ServiceRunTerminalNotificationDeliveryStatus.Prepared &&
                State.TerminalNotificationAttempt == 0 &&
                attempt == 1;
            Logger.LogWarning(
                ex,
                canPublishImmediateRecovery
                    ? "Service run terminal notification durable retry scheduling failed; publishing one immediate recovery continuation. actor={ActorId} delivery={DeliveryId} attempt={Attempt}"
                    : "Service run terminal notification durable retry scheduling failed; preserving the outbox for activation recovery. actor={ActorId} delivery={DeliveryId} attempt={Attempt}",
                Id,
                notification.DeliveryId,
                attempt);
            if (canPublishImmediateRecovery)
            {
                await PublishAsync(
                    retryFired,
                    TopologyAudience.Self,
                    ct,
                    options: retryOptions);
            }
            throw;
        }
        await PersistDomainEventAsync(new ServiceRunTerminalNotificationRetryScheduledEvent
        {
            DeliveryId = notification.DeliveryId,
            Attempt = attempt,
            CallbackId = callbackId,
            RetryAt = Timestamp.FromDateTimeOffset(retryAt),
        }, ct);
    }

    private static string BuildTerminalNotificationRetryCallbackId(string deliveryId) =>
        RuntimeCallbackKeyComposer.BuildCallbackId(
            TerminalNotificationRetryCallbackPrefix,
            deliveryId);

    private static EventEnvelopePublishOptions BuildTerminalNotificationRetryOptions(
        string callbackId,
        int attempt) =>
        new()
        {
            Delivery = new EventEnvelopeDeliveryOptions
            {
                OperationId = RuntimeCallbackKeyComposer.BuildCallbackId(
                    callbackId,
                    attempt.ToString(CultureInfo.InvariantCulture)),
            },
        };

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
