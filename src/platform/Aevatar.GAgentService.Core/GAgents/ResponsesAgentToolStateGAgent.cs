using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.responses-agent-tool-state")]
public sealed class ResponsesAgentToolStateGAgent : GAgentBase<ResponsesAgentToolState>
{
    public ResponsesAgentToolStateGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleRegisterAsync(RegisterResponsesAgentToolStateRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Record);

        var record = NormalizeRecord(command.Record.Clone());
        ValidateRecord(record);
        if (State.Record != null && !string.IsNullOrWhiteSpace(State.Record.ScopeId))
        {
            EnsureExistingRecordMatches(State.Record, record);
            return;
        }

        await PersistDomainEventAsync(new ResponsesAgentToolStateRegisteredEvent
        {
            Record = record,
        });
    }

    [EventHandler]
    public async Task HandleApplyTodoWriteAsync(ApplyResponsesTodoWriteRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureRegistered(command.ScopeId, command.OwnerSubject);

        var observedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var incoming = command.TodoItems
            .Select(static item => item.Clone())
            .ToList();
        if (TodoItemsEqual(State.TodoItems, incoming))
            return;

        await PersistDomainEventAsync(new ResponsesTodoWriteAppliedEvent
        {
            SourceResponseId = NormalizeOptional(command.SourceResponseId) ?? string.Empty,
            Arguments = command.Arguments?.Clone(),
            ObservedAt = observedAt,
            TodoItems = { incoming },
        });
    }

    [EventHandler]
    public async Task HandleRecordWebTraceAsync(RecordResponsesWebTraceRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureRegistered();

        var observedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var trace = new ResponsesWebTrace
        {
            TraceId = NormalizeRequired(command.TraceId),
            SourceResponseId = NormalizeOptional(command.SourceResponseId) ?? string.Empty,
            ToolName = NormalizeRequired(command.ToolName),
            CacheKey = NormalizeRequired(command.CacheKey),
            Url = NormalizeOptional(command.Url) ?? string.Empty,
            Query = NormalizeOptional(command.Query) ?? string.Empty,
            CacheHit = command.CacheHit,
            // Refactor (iter161-cluster-001 #1251-first):
            //   Old pattern: typed result writes left legacy Value empty.
            //   New principle: typed remains primary and Value is retained as readmodel fallback.
            Result = command.Result?.Clone() ?? ResponsesWebResultMigration.ToLegacyValue(command.TypedResult),
            TypedResult = command.TypedResult?.Clone(),
            ObservedAt = observedAt,
        };
        ValidateWebTrace(trace);

        if (State.WebTraces.Any(x => string.Equals(x.TraceId, trace.TraceId, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(new ResponsesWebTraceRecordedEvent
        {
            Trace = trace,
        });
    }

    protected override ResponsesAgentToolState TransitionState(ResponsesAgentToolState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ResponsesAgentToolStateRegisteredEvent>(ApplyRegistered)
            .On<ResponsesTodoWriteAppliedEvent>(ApplyTodoWrite)
            .On<ResponsesWebTraceRecordedEvent>(ApplyWebTraceRecorded)
            .OrCurrent();

    private static ResponsesAgentToolState ApplyRegistered(
        ResponsesAgentToolState state,
        ResponsesAgentToolStateRegisteredEvent evt)
    {
        var next = state.Clone();
        next.Record = evt.Record?.Clone() ?? new ResponsesAgentToolStateRecord();
        Bump(next, $"{next.Record.ScopeId}:registered");
        return next;
    }

    private static ResponsesAgentToolState ApplyTodoWrite(
        ResponsesAgentToolState state,
        ResponsesTodoWriteAppliedEvent evt)
    {
        var next = state.Clone();
        next.TodoItems.Clear();
        next.TodoItems.AddRange(evt.TodoItems.Select(static x => x.Clone()));
        Touch(next, evt.ObservedAt);
        Bump(next, $"{next.Record?.ScopeId}:todo:{evt.SourceResponseId}");
        return next;
    }

    private static ResponsesAgentToolState ApplyWebTraceRecorded(
        ResponsesAgentToolState state,
        ResponsesWebTraceRecordedEvent evt)
    {
        var next = state.Clone();
        if (evt.Trace != null)
        {
            next.WebTraces.Add(evt.Trace.Clone());
            UpsertWebCache(next, evt.Trace);
        }

        Touch(next, evt.Trace?.ObservedAt);
        Bump(next, $"{next.Record?.ScopeId}:web:{evt.Trace?.TraceId}");
        return next;
    }

    private static void UpsertWebCache(ResponsesAgentToolState state, ResponsesWebTrace trace)
    {
        var existing = state.WebCacheEntries.FirstOrDefault(x =>
            string.Equals(x.ToolName, trace.ToolName, StringComparison.Ordinal) &&
            string.Equals(x.CacheKey, trace.CacheKey, StringComparison.Ordinal));
        if (existing == null)
        {
            state.WebCacheEntries.Add(new ResponsesWebCacheEntry
            {
                ToolName = trace.ToolName,
                CacheKey = trace.CacheKey,
                Url = trace.Url,
                Query = trace.Query,
                Result = trace.Result?.Clone(),
                TypedResult = trace.TypedResult?.Clone(),
                CachedAt = trace.ObservedAt.Clone(),
                LastHitAt = trace.CacheHit ? trace.ObservedAt.Clone() : null,
                HitCount = trace.CacheHit ? 1 : 0,
            });
            return;
        }

        if (trace.CacheHit)
        {
            existing.LastHitAt = trace.ObservedAt.Clone();
            existing.HitCount++;
            return;
        }

        existing.Result = trace.Result?.Clone();
        existing.TypedResult = trace.TypedResult?.Clone();
        existing.Url = trace.Url;
        existing.Query = trace.Query;
        existing.CachedAt = trace.ObservedAt.Clone();
    }

    private void EnsureRegistered(string? scopeId = null, string? ownerSubject = null)
    {
        var record = State.Record;
        if (record == null || string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException($"Responses agent tool state actor '{Id}' is not registered.");
        if (!string.IsNullOrWhiteSpace(scopeId) &&
            !string.Equals(record.ScopeId, scopeId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Responses agent tool state actor '{Id}' is bound to scope '{record.ScopeId}'.");
        }
        if (!string.IsNullOrWhiteSpace(ownerSubject) &&
            !string.Equals(record.OwnerSubject, ownerSubject.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Responses agent tool state actor '{Id}' is bound to owner '{record.OwnerSubject}'.");
        }
    }

    private static ResponsesAgentToolStateRecord NormalizeRecord(ResponsesAgentToolStateRecord record)
    {
        record.ScopeId = NormalizeRequired(record.ScopeId);
        record.OwnerSubject = NormalizeRequired(record.OwnerSubject);
        record.CreatedAt ??= Timestamp.FromDateTime(DateTime.UtcNow);
        record.UpdatedAt ??= record.CreatedAt.Clone();
        return record;
    }

    private static void ValidateRecord(ResponsesAgentToolStateRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ScopeId))
            throw new InvalidOperationException("scope_id is required.");
        if (string.IsNullOrWhiteSpace(record.OwnerSubject))
            throw new InvalidOperationException("owner_subject is required.");
    }

    private static void ValidateWebTrace(ResponsesWebTrace trace)
    {
        if (string.IsNullOrWhiteSpace(trace.TraceId))
            throw new InvalidOperationException("trace_id is required.");
        if (string.IsNullOrWhiteSpace(trace.ToolName))
            throw new InvalidOperationException("tool_name is required.");
        if (string.IsNullOrWhiteSpace(trace.CacheKey))
            throw new InvalidOperationException("cache_key is required.");
    }

    private static void EnsureExistingRecordMatches(
        ResponsesAgentToolStateRecord existing,
        ResponsesAgentToolStateRecord incoming)
    {
        if (!string.Equals(existing.ScopeId, incoming.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(existing.OwnerSubject, incoming.OwnerSubject, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Responses agent tool state actor is bound to '{existing.ScopeId}/{existing.OwnerSubject}' and cannot rebind.");
        }
    }

    private static bool TodoItemsEqual(
        IEnumerable<ResponsesTodoItem> existing,
        IReadOnlyList<ResponsesTodoItem> incoming)
    {
        var left = existing.ToArray();
        if (left.Length != incoming.Count)
            return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i].Id, incoming[i].Id, StringComparison.Ordinal) ||
                !string.Equals(left[i].Content, incoming[i].Content, StringComparison.Ordinal) ||
                !string.Equals(left[i].Status, incoming[i].Status, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void Touch(ResponsesAgentToolState state, Timestamp? timestamp)
    {
        if (state.Record == null)
            return;
        state.Record.UpdatedAt = timestamp?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow);
    }

    private static void Bump(ResponsesAgentToolState state, string eventId)
    {
        state.LastAppliedEventVersion++;
        state.LastEventId = eventId;
    }

    private static string NormalizeRequired(string? value) =>
        NormalizeOptional(value) ?? string.Empty;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
