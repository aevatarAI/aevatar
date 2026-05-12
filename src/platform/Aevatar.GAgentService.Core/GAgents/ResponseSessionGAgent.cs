using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

public sealed class ResponseSessionGAgent : GAgentBase<ResponseSessionState>
{
    private static readonly Duration DefaultTtl = Duration.FromTimeSpan(TimeSpan.FromHours(24));

    public ResponseSessionGAgent()
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

        await PersistDomainEventAsync(new ResponseSessionRegisteredEvent
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

        if (command.Status == ResponseSessionStatus.Unspecified)
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

        await PersistDomainEventAsync(new ResponseSessionStatusUpdatedEvent
        {
            ResponseId = existing.ResponseId,
            Status = command.Status,
            UpdatedAt = command.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
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

        await PersistDomainEventAsync(new ResponseSessionStatusUpdatedEvent
        {
            ResponseId = existing.ResponseId,
            Status = ResponseSessionStatus.Expired,
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

        await PersistDomainEventAsync(new ResponseSessionForwardedToolCallEmittedEvent
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

        if (existingCall.Status is ResponseSessionForwardedToolCallStatus.Received
            or ResponseSessionForwardedToolCallStatus.Resolved)
        {
            return;
        }

        if (existingCall.Status is ResponseSessionForwardedToolCallStatus.Cancelled
            or ResponseSessionForwardedToolCallStatus.Expired)
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' is {existingCall.Status} and cannot receive a result.");
        }

        await PersistDomainEventAsync(new ResponseSessionForwardedToolResultReceivedEvent
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

        if (existingCall.Status == ResponseSessionForwardedToolCallStatus.Resolved)
            return;

        if (existingCall.Status != ResponseSessionForwardedToolCallStatus.Received)
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{callId}' is {existingCall.Status} and cannot be resolved.");
        }

        await PersistDomainEventAsync(new ResponseSessionForwardedToolCallResolvedEvent
        {
            ResponseId = existing.ResponseId,
            CallId = callId,
            ResolvedAt = command.ResolvedAt ?? Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override ResponseSessionState TransitionState(ResponseSessionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ResponseSessionRegisteredEvent>(ApplyRegistered)
            .On<ResponseSessionStatusUpdatedEvent>(ApplyStatusUpdated)
            .On<ResponseSessionForwardedToolCallEmittedEvent>(ApplyForwardedToolCallEmitted)
            .On<ResponseSessionForwardedToolResultReceivedEvent>(ApplyForwardedToolResultReceived)
            .On<ResponseSessionForwardedToolCallResolvedEvent>(ApplyForwardedToolCallResolved)
            .OrCurrent();

    private static ResponseSessionState ApplyRegistered(
        ResponseSessionState state,
        ResponseSessionRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Record = evt.Record?.Clone() ?? new ResponseSessionRecord();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.ResponseId}:registered";
        return next;
    }

    private static ResponseSessionState ApplyStatusUpdated(
        ResponseSessionState state,
        ResponseSessionStatusUpdatedEvent evt)
    {
        var next = state.Clone();
        if (next.Record == null)
            next.Record = new ResponseSessionRecord();

        next.Record.Status = evt.Status;
        next.Record.UpdatedAt = evt.UpdatedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        if (evt.Status == ResponseSessionStatus.Cancelled)
        {
            next.Record.CancelledAt = next.Record.UpdatedAt.Clone();
            MarkOpenToolCalls(next, ResponseSessionForwardedToolCallStatus.Cancelled);
        }
        else if (evt.Status == ResponseSessionStatus.Expired)
        {
            MarkOpenToolCalls(next, ResponseSessionForwardedToolCallStatus.Expired);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{next.Record.ResponseId}:status:{(int)evt.Status}";
        return next;
    }

    private static ResponseSessionState ApplyForwardedToolCallEmitted(
        ResponseSessionState state,
        ResponseSessionForwardedToolCallEmittedEvent evt)
    {
        var next = state.Clone();
        if (evt.Call != null)
            next.ForwardedToolCalls.Add(evt.Call.Clone());
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.Call?.CallId}:emitted";
        return next;
    }

    private static ResponseSessionState ApplyForwardedToolResultReceived(
        ResponseSessionState state,
        ResponseSessionForwardedToolResultReceivedEvent evt)
    {
        var next = state.Clone();
        var call = next.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, evt.CallId, StringComparison.Ordinal));
        if (call != null)
        {
            call.Status = ResponseSessionForwardedToolCallStatus.Received;
            call.Result = evt.Result?.Clone();
            call.ReceivedAt = evt.ReceivedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.CallId}:received";
        return next;
    }

    private static ResponseSessionState ApplyForwardedToolCallResolved(
        ResponseSessionState state,
        ResponseSessionForwardedToolCallResolvedEvent evt)
    {
        var next = state.Clone();
        var call = next.ForwardedToolCalls
            .FirstOrDefault(x => string.Equals(x.CallId, evt.CallId, StringComparison.Ordinal));
        if (call != null)
        {
            call.Status = ResponseSessionForwardedToolCallStatus.Resolved;
            call.ResolvedAt = evt.ResolvedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        }

        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = $"{evt.ResponseId}:tool:{evt.CallId}:resolved";
        return next;
    }

    private static ResponseSessionRecord NormalizeRecord(ResponseSessionRecord record)
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
        if (record.Status == ResponseSessionStatus.Unspecified)
            record.Status = ResponseSessionStatus.Accepted;
        return record;
    }

    private static void ValidateRecord(ResponseSessionRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ResponseId))
            throw new InvalidOperationException("response_id is required.");
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.OwnerSubject))
            throw new InvalidOperationException("owner_subject is required.");
        if (record.OriginKind == ResponseSessionOriginKind.Unspecified)
            throw new InvalidOperationException("origin_kind is required.");
        if (record.Ttl == null || record.Ttl.ToTimeSpan() <= TimeSpan.Zero)
            throw new InvalidOperationException("ttl must be greater than zero.");
    }

    private ResponseSessionRecord EnsureRegisteredSession(string? responseId)
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

    private static ResponseSessionForwardedToolCall NormalizeToolCall(ResponseSessionForwardedToolCall call)
    {
        call.CallId = NormalizeRequired(call.CallId);
        call.ToolName = NormalizeRequired(call.ToolName);
        call.SchemaHash = NormalizeRequired(call.SchemaHash);
        if (call.Status == ResponseSessionForwardedToolCallStatus.Unspecified)
            call.Status = ResponseSessionForwardedToolCallStatus.Pending;
        if (call.EmittedAt == null)
            call.EmittedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        if (call.Expiry == null)
            call.Expiry = Timestamp.FromDateTime(DateTime.UtcNow.Add(DefaultTtl.ToTimeSpan()));
        return call;
    }

    private static void ValidateToolCall(ResponseSessionForwardedToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.CallId))
            throw new InvalidOperationException("call_id is required.");
        if (string.IsNullOrWhiteSpace(call.ToolName))
            throw new InvalidOperationException("tool_name is required.");
        if (string.IsNullOrWhiteSpace(call.SchemaHash))
            throw new InvalidOperationException("schema_hash is required.");
        if (call.Status != ResponseSessionForwardedToolCallStatus.Pending)
            throw new InvalidOperationException("forwarded tool calls must start as pending.");
        if (call.Expiry == null)
            throw new InvalidOperationException("expiry is required.");
    }

    private static void EnsureExistingMatches(
        ResponseSessionRecord existing,
        ResponseSessionRecord incoming)
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
        ResponseSessionForwardedToolCall existing,
        ResponseSessionForwardedToolCall incoming)
    {
        if (!string.Equals(existing.ToolName, incoming.ToolName, StringComparison.Ordinal) ||
            !string.Equals(existing.SchemaHash, incoming.SchemaHash, StringComparison.Ordinal) ||
            !Equals(existing.Arguments, incoming.Arguments))
        {
            throw new InvalidOperationException(
                $"Forwarded tool call '{existing.CallId}' cannot be rebound to different tool call facts.");
        }
    }

    private static void MarkOpenToolCalls(
        ResponseSessionState state,
        ResponseSessionForwardedToolCallStatus status)
    {
        foreach (var call in state.ForwardedToolCalls)
        {
            if (call.Status is ResponseSessionForwardedToolCallStatus.Pending
                or ResponseSessionForwardedToolCallStatus.Received)
            {
                call.Status = status;
                if (status == ResponseSessionForwardedToolCallStatus.Expired)
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
        ResponseSessionRecord record,
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

    private static DateTimeOffset ResolveExpiry(ResponseSessionRecord record)
    {
        var createdAt = record.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        var ttl = record.Ttl?.ToTimeSpan() ?? DefaultTtl.ToTimeSpan();
        return createdAt.Add(ttl);
    }

    private static bool IsTerminal(ResponseSessionStatus status) =>
        status is ResponseSessionStatus.Completed
            or ResponseSessionStatus.Failed
            or ResponseSessionStatus.Cancelled
            or ResponseSessionStatus.Expired;

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
