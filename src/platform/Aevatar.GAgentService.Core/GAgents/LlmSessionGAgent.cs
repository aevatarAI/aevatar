using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

// Refactor (iter75/cluster-075-responses-agui-host-completion-state):
//   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
//   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
public sealed class LlmSessionGAgent : GAgentBase<LlmSessionState>
{
    private static readonly Duration DefaultTtl = Duration.FromTimeSpan(TimeSpan.FromHours(24));

    public LlmSessionGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterResponseSessionRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Record);

        var record = NormalizeRecord(command.Record.Clone());
        ValidateRecord(record);

        var existing = State.Record;
        if (existing != null && !string.IsNullOrWhiteSpace(existing.ResponseId))
        {
            EnsureExistingMatches(existing, record);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionRegisteredEvent
        {
            Record = record,
        });
        await ScheduleTtlExpiryAsync(record);
    }

    [EventHandler]
    public async Task HandleUpdateStatusAsync(UpdateResponseSessionStatusRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.ResponseId))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' has no registered response; status update rejected.");
        }

        var responseId = NormalizeRequired(command.ResponseId);
        if (!string.Equals(existing.ResponseId, responseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' is bound to response '{existing.ResponseId}' and cannot update response '{responseId}'.");
        }

        if (command.Status == LlmSessionStatus.Unspecified)
            return;

        if (existing.Status == command.Status)
            return;

        if (IsTerminal(existing.Status))
        {
            // Terminal states are authoritative — a late Completed/Failed update
            // from the original create path must not overwrite a Cancelled/Expired
            // session, otherwise /cancel reports success while the session ends up
            // Completed and forwarded tool calls stay open.
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' is {existing.Status} and cannot transition to {command.Status}.");
        }

        await PersistDomainEventAsync(new LlmSessionStatusUpdatedEvent
        {
            ResponseId = existing.ResponseId,
            Status = command.Status,
            UpdatedAt = command.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    // Refactor (iter75/cluster-075-responses-agui-host-completion-state):
    //   Old pattern: direct route forwarding bypassed the LLM tool loop and forced Host-side completion synthesis
    //   New principle: Reuse LlmSessionGAgent for forwarded Responses; Host renders response.completed from typed completion contract / readmodel
    [EventHandler]
    public async Task HandleRecordCompletionAsync(RecordResponseSessionCompletionRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Completion);

        var existing = EnsureRegisteredSession(command.ResponseId);
        var completion = NormalizeCompletion(command.Completion.Clone());
        ValidateCompletion(completion);

        if (State.Completion is { CompletedAt: not null } current)
        {
            EnsureExistingCompletionMatches(current, completion);
            return;
        }

        if (IsTerminal(existing.Status) &&
            existing.Status is not (LlmSessionStatus.Completed or LlmSessionStatus.Failed))
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' is {existing.Status} and cannot record completion.");
        }

        await PersistDomainEventAsync(new LlmSessionCompletionRecordedEvent
        {
            ResponseId = existing.ResponseId,
            Completion = completion,
        });
    }

    [EventHandler]
    public async Task HandleExpireResponseSessionAsync(ExpireResponseSessionRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        if (IsTerminal(existing.Status))
            return;

        var observedAt = command.ObservedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var expiresAt = ResolveExpiry(existing);
        if (expiresAt > observedAt)
        {
            await ScheduleTtlExpiryAsync(existing, observedAt);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionStatusUpdatedEvent
        {
            ResponseId = existing.ResponseId,
            Status = LlmSessionStatus.Expired,
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
        });
    }

    [EventHandler]
    public async Task HandleRecordForwardedToolCallAsync(RecordForwardedToolCallRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Call);

        var existing = EnsureRegisteredSession(command.ResponseId);
        if (IsTerminal(existing.Status))
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' is {existing.Status} and cannot record new forwarded tool calls.");
        }

        var call = NormalizeToolCall(command.Call.Clone());
        ValidateToolCall(call);

        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, call.CallId, StringComparison.Ordinal));
        if (existingCall != null)
        {
            EnsureExistingToolCallMatches(existingCall, call);
            return;
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolCallEmittedEvent
        {
            ResponseId = existing.ResponseId,
            Call = call,
        });
    }

    [EventHandler]
    public async Task HandleReceiveForwardedToolResultAsync(ReceiveForwardedToolResultRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        var callId = NormalizeRequired(command.CallId);
        var schemaHash = NormalizeRequired(command.SchemaHash);
        if (string.IsNullOrWhiteSpace(callId))
            throw new InvalidOperationException("call_id is required.");
        if (string.IsNullOrWhiteSpace(schemaHash))
            throw new InvalidOperationException("schema_hash is required.");

        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, callId, StringComparison.Ordinal));
        if (existingCall == null)
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' has no forwarded tool call '{callId}'.");
        }

        if (!string.Equals(existingCall.SchemaHash, schemaHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' schema hash mismatch.");
        }

        if (existingCall.Status is LlmSessionForwardedToolCallStatus.Received
            or LlmSessionForwardedToolCallStatus.Resolved)
        {
            return;
        }

        if (existingCall.Status is LlmSessionForwardedToolCallStatus.Cancelled
            or LlmSessionForwardedToolCallStatus.Expired)
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' is {existingCall.Status} and cannot receive a result.");
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolResultReceivedEvent
        {
            ResponseId = existing.ResponseId,
            CallId = callId,
            SchemaHash = schemaHash,
            Result = command.Result?.Clone(),
            ReceivedAt = command.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleResolveForwardedToolResultAsync(ResolveForwardedToolResultRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var existing = EnsureRegisteredSession(command.ResponseId);
        var callId = NormalizeRequired(command.CallId);
        var existingCall = State.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, callId, StringComparison.Ordinal));
        if (existingCall == null)
        {
            throw new InvalidOperationException(
                $"Response session '{existing.ResponseId}' has no forwarded tool call '{callId}'.");
        }

        if (existingCall.Status == LlmSessionForwardedToolCallStatus.Resolved)
            return;

        if (existingCall.Status != LlmSessionForwardedToolCallStatus.Received)
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' is {existingCall.Status} and cannot be resolved.");
        }

        await PersistDomainEventAsync(new LlmSessionForwardedToolCallResolvedEvent
        {
            ResponseId = existing.ResponseId,
            CallId = callId,
            ResolvedAt = command.ResolvedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override LlmSessionState TransitionState(LlmSessionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<LlmSessionRegisteredEvent>(ApplyRegistered)
            .On<LlmSessionStatusUpdatedEvent>(ApplyStatusUpdated)
            .On<LlmSessionCompletionRecordedEvent>(ApplyCompletionRecorded)
            .On<LlmSessionForwardedToolCallEmittedEvent>(ApplyForwardedToolCallEmitted)
            .On<LlmSessionForwardedToolResultReceivedEvent>(ApplyForwardedToolResultReceived)
            .On<LlmSessionForwardedToolCallResolvedEvent>(ApplyForwardedToolCallResolved)
            .OrCurrent();

    private static LlmSessionState ApplyRegistered(
        LlmSessionState state,
        LlmSessionRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Record = evt.Record?.Clone() ?? new LlmSessionRecord();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.ResponseId}:registered";
        return next;
    }

    private static LlmSessionState ApplyCompletionRecorded(
        LlmSessionState state,
        LlmSessionCompletionRecordedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new LlmSessionRecord();

        next.Completion = evt.Completion?.Clone() ?? new LlmSessionCompletion();
        next.Record.Status = string.IsNullOrWhiteSpace(next.Completion.FailureCode)
            ? LlmSessionStatus.Completed
            : LlmSessionStatus.Failed;
        next.Record.UpdatedAt = next.Completion.CompletedAt?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:completion";
        return next;
    }

    private static LlmSessionState ApplyStatusUpdated(
        LlmSessionState state,
        LlmSessionStatusUpdatedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new LlmSessionRecord();

        next.Record.Status = evt.Status;
        next.Record.UpdatedAt = evt.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        if (evt.Status == LlmSessionStatus.Cancelled)
        {
            next.Record.CancelledAt = next.Record.UpdatedAt.Clone();
            MarkOpenToolCalls(next, LlmSessionForwardedToolCallStatus.Cancelled);
        }
        else if (evt.Status == LlmSessionStatus.Expired)
        {
            MarkOpenToolCalls(next, LlmSessionForwardedToolCallStatus.Expired);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.ResponseId}:status:{(int)evt.Status}";
        return next;
    }

    private static LlmSessionState ApplyForwardedToolCallEmitted(
        LlmSessionState state,
        LlmSessionForwardedToolCallEmittedEvent evt)
    {
        var next = state.Clone();
        if (evt.Call != null)
            next.ForwardedToolCalls.Add(evt.Call.Clone());
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.Call?.CallId}:emitted";
        return next;
    }

    private static LlmSessionState ApplyForwardedToolResultReceived(
        LlmSessionState state,
        LlmSessionForwardedToolResultReceivedEvent evt)
    {
        var next = state.Clone();
        var call = next.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, evt.CallId, StringComparison.Ordinal));
        if (call != null)
        {
            call.Status = LlmSessionForwardedToolCallStatus.Received;
            call.Result = evt.Result?.Clone();
            call.ReceivedAt = evt.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.CallId}:received";
        return next;
    }

    private static LlmSessionState ApplyForwardedToolCallResolved(
        LlmSessionState state,
        LlmSessionForwardedToolCallResolvedEvent evt)
    {
        var next = state.Clone();
        var call = next.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, evt.CallId, StringComparison.Ordinal));
        if (call != null)
        {
            call.Status = LlmSessionForwardedToolCallStatus.Resolved;
            call.ResolvedAt = evt.ResolvedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.CallId}:resolved";
        return next;
    }

    private static LlmSessionRecord NormalizeRecord(LlmSessionRecord record)
    {
        record.ResponseId = NormalizeRequired(record.ResponseId);
        record.ScopeId = NormalizeRequired(record.ScopeId);
        record.OwnerSubject = NormalizeRequired(record.OwnerSubject);
        record.PreviousResponseId = NormalizeOptional(record.PreviousResponseId) ?? string.Empty;
        if (record.CreatedAt == null)
            record.CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (record.UpdatedAt == null)
            record.UpdatedAt = record.CreatedAt.Clone();
        if (record.Ttl == null)
            record.Ttl = DefaultTtl.Clone();
        if (record.Status == LlmSessionStatus.Unspecified)
            record.Status = LlmSessionStatus.Accepted;
        return record;
    }

    private static void ValidateRecord(LlmSessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ResponseId))
            throw new InvalidOperationException("response_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.OwnerSubject))
            throw new InvalidOperationException("owner_subject is required.");
        if (record.OriginKind == LlmSessionOriginKind.Unspecified)
            throw new InvalidOperationException("origin_kind is required.");
        if (record.Ttl == null || record.Ttl.ToTimeSpan() <= TimeSpan.Zero)
            throw new InvalidOperationException("ttl must be greater than zero.");
    }

    private LlmSessionRecord EnsureRegisteredSession(string? responseId)
    {
        var existing = State.Record;
        if (existing == null || string.IsNullOrWhiteSpace(existing.ResponseId))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' has no registered response.");
        }

        var normalizedResponseId = NormalizeRequired(responseId);
        if (!string.Equals(existing.ResponseId, normalizedResponseId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Response session actor '{Id}' is bound to response '{existing.ResponseId}' and cannot handle response '{normalizedResponseId}'.");
        }

        return existing;
    }

    private static LlmSessionForwardedToolCall NormalizeToolCall(LlmSessionForwardedToolCall call)
    {
        call.CallId = NormalizeRequired(call.CallId);
        call.ToolName = NormalizeRequired(call.ToolName);
        call.SchemaHash = NormalizeRequired(call.SchemaHash);
        if (call.Status == LlmSessionForwardedToolCallStatus.Unspecified)
            call.Status = LlmSessionForwardedToolCallStatus.Pending;
        if (call.EmittedAt == null)
            call.EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (call.Expiry == null)
            call.Expiry = Timestamp.FromDateTime(DateTime.UtcNow.Add(DefaultTtl.ToTimeSpan()));
        return call;
    }

    private static void ValidateToolCall(LlmSessionForwardedToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.CallId))
            throw new InvalidOperationException("call_id is required.");
        if (string.IsNullOrWhiteSpace(call.ToolName))
            throw new InvalidOperationException("tool_name is required.");
        if (string.IsNullOrWhiteSpace(call.SchemaHash))
            throw new InvalidOperationException("schema_hash is required.");
        if (call.Status != LlmSessionForwardedToolCallStatus.Pending)
            throw new InvalidOperationException("forwarded tool calls must start as pending.");
        if (call.Expiry == null)
            throw new InvalidOperationException("expiry is required.");
    }

    private static LlmSessionCompletion NormalizeCompletion(LlmSessionCompletion completion)
    {
        completion.OutputText ??= string.Empty;
        completion.FailureCode = NormalizeOptional(completion.FailureCode) ?? string.Empty;
        completion.FailureMessage = NormalizeOptional(completion.FailureMessage) ?? string.Empty;
        if (completion.CompletedAt == null)
            completion.CompletedAt = Timestamp.FromDateTime(DateTime.UtcNow);

        foreach (var toolCall in completion.ToolCalls)
        {
            toolCall.CallId = NormalizeRequired(toolCall.CallId);
            toolCall.ToolName = NormalizeRequired(toolCall.ToolName);
        }

        return completion;
    }

    private static void ValidateCompletion(LlmSessionCompletion completion)
    {
        if (completion.CompletedAt == null)
            throw new InvalidOperationException("completed_at is required.");
        if (!string.IsNullOrWhiteSpace(completion.FailureCode) &&
            string.IsNullOrWhiteSpace(completion.FailureMessage))
        {
            throw new InvalidOperationException("failure_message is required when failure_code is present.");
        }

        foreach (var toolCall in completion.ToolCalls)
        {
            if (string.IsNullOrWhiteSpace(toolCall.CallId))
                throw new InvalidOperationException("completion tool call_id is required.");
            if (string.IsNullOrWhiteSpace(toolCall.ToolName))
                throw new InvalidOperationException("completion tool tool_name is required.");
        }
    }

    private static void EnsureExistingMatches(
        LlmSessionRecord existing,
        LlmSessionRecord incoming)
    {
        if (!string.Equals(existing.ResponseId, incoming.ResponseId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' cannot be rebound to response '{incoming.ResponseId}'.");

        if (!string.Equals(existing.ScopeId, incoming.ScopeId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to scope '{existing.ScopeId}' and cannot rebind to scope '{incoming.ScopeId}'.");

        if (!string.Equals(existing.OwnerSubject, incoming.OwnerSubject, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to owner '{existing.OwnerSubject}' and cannot rebind to owner '{incoming.OwnerSubject}'.");

        if (existing.OriginKind != incoming.OriginKind)
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to origin '{existing.OriginKind}' and cannot rebind to origin '{incoming.OriginKind}'.");

        if (!string.Equals(
                NormalizeOptional(existing.PreviousResponseId),
                NormalizeOptional(incoming.PreviousResponseId),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to previous_response_id '{existing.PreviousResponseId}' and cannot rebind to '{incoming.PreviousResponseId}'.");
        }

        if (!DurationEquals(existing.Ttl, incoming.Ttl))
        {
            throw new InvalidOperationException(
                $"Response session actor '{existing.ResponseId}' is bound to ttl '{existing.Ttl}' and cannot rebind to '{incoming.Ttl}'.");
        }
    }

    private static void EnsureExistingToolCallMatches(
        LlmSessionForwardedToolCall existing,
        LlmSessionForwardedToolCall incoming)
    {
        if (!string.Equals(existing.ToolName, incoming.ToolName, StringComparison.Ordinal) ||
            !string.Equals(existing.SchemaHash, incoming.SchemaHash, StringComparison.Ordinal) ||
            !Equals(existing.Arguments, incoming.Arguments))
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{existing.CallId}' cannot be rebound to different tool call facts.");
        }
    }

    private static void EnsureExistingCompletionMatches(
        LlmSessionCompletion existing,
        LlmSessionCompletion incoming)
    {
        if (!string.Equals(existing.OutputText, incoming.OutputText, StringComparison.Ordinal) ||
            !string.Equals(existing.FailureCode, incoming.FailureCode, StringComparison.Ordinal) ||
            !string.Equals(existing.FailureMessage, incoming.FailureMessage, StringComparison.Ordinal) ||
            existing.ToolCalls.Count != incoming.ToolCalls.Count)
        {
            throw new InvalidOperationException("Response session completion cannot be rebound to different facts.");
        }

        for (var i = 0; i < existing.ToolCalls.Count; i++)
        {
            var existingTool = existing.ToolCalls[i];
            var incomingTool = incoming.ToolCalls[i];
            if (!string.Equals(existingTool.CallId, incomingTool.CallId, StringComparison.Ordinal) ||
                !string.Equals(existingTool.ToolName, incomingTool.ToolName, StringComparison.Ordinal) ||
                !Equals(existingTool.Result, incomingTool.Result))
            {
                throw new InvalidOperationException("Response session completion cannot be rebound to different tool call facts.");
            }
        }
    }

    private static void MarkOpenToolCalls(
        LlmSessionState state,
        LlmSessionForwardedToolCallStatus status)
    {
        foreach (var call in state.ForwardedToolCalls)
        {
            if (call.Status is LlmSessionForwardedToolCallStatus.Pending
                or LlmSessionForwardedToolCallStatus.Received)
            {
                call.Status = status;
                if (status == LlmSessionForwardedToolCallStatus.Expired)
                {
                    // Mark received timestamp so downstream snapshots know when
                    // expiry happened. The result value stays empty —
                    // adapters/readers synthesize the "tool_call_expired" surface
                    // when shaping the response back to the client.
                    call.ReceivedAt ??= state.Record?.UpdatedAt?.Clone()
                                        ?? Timestamp.FromDateTime(DateTime.UtcNow);
                }
            }
        }
    }

    private Task ScheduleTtlExpiryAsync(
        LlmSessionRecord record,
        DateTimeOffset? observedAt = null)
    {
        var expiresAt = ResolveExpiry(record);
        var now = observedAt ?? DateTimeOffset.UtcNow;
        var dueTime = expiresAt - now;
        if (dueTime <= TimeSpan.Zero)
            dueTime = TimeSpan.FromMilliseconds(1);

        return ScheduleSelfDurableTimeoutAsync(
            $"response-session:{record.ResponseId}:ttl",
            dueTime,
            new ExpireResponseSessionRequested
            {
                ResponseId = record.ResponseId,
                ObservedAt = Timestamp.FromDateTimeOffset(expiresAt),
            });
    }

    private static DateTimeOffset ResolveExpiry(LlmSessionRecord record)
    {
        var createdAt = record.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var ttl = record.Ttl?.ToTimeSpan() ?? DefaultTtl.ToTimeSpan();
        return createdAt.Add(ttl);
    }

    private static bool IsTerminal(LlmSessionStatus status) =>
        status is LlmSessionStatus.Completed
            or LlmSessionStatus.Failed
            or LlmSessionStatus.Cancelled
            or LlmSessionStatus.Expired;

    private static bool DurationEquals(Duration? left, Duration? right) =>
        left?.ToTimeSpan() == right?.ToTimeSpan();

    private static string NormalizeRequired(string? value) =>
        NormalizeOptional(value) ?? string.Empty;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
