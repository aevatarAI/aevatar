using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

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
        var todos = ParseTodoItems(command.ArgumentsJson, command.SourceResponseId, observedAt);
        if (TodoItemsEqual(State.TodoItems, todos))
            return;

        await PersistDomainEventAsync(new ResponsesTodoWriteAppliedEvent
        {
            SourceResponseId = NormalizeOptional(command.SourceResponseId) ?? string.Empty,
            ArgumentsJson = NormalizeOptional(command.ArgumentsJson) ?? "{}",
            ObservedAt = observedAt,
            TodoItems = { todos },
        });
    }

    [EventHandler]
    public async Task HandleRecordTaskAsync(RecordResponsesTaskRequested command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureRegistered();

        var observedAt = command.ObservedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        var task = new ResponsesTaskTrace
        {
            TaskId = NormalizeRequired(command.TaskId),
            SourceResponseId = NormalizeOptional(command.SourceResponseId) ?? string.Empty,
            ChildActorId = NormalizeRequired(command.ChildActorId),
            Description = NormalizeOptional(command.Description) ?? string.Empty,
            ArgumentsJson = NormalizeOptional(command.ArgumentsJson) ?? "{}",
            ResultJson = NormalizeOptional(command.ResultJson) ?? "{}",
            Status = command.Status == ResponsesAgentToolTaskStatus.Unspecified
                ? ResponsesAgentToolTaskStatus.Accepted
                : command.Status,
            CreatedAt = observedAt,
            UpdatedAt = observedAt,
        };
        ValidateTask(task);

        if (State.TaskTraces.Any(x => string.Equals(x.TaskId, task.TaskId, StringComparison.Ordinal)))
            return;

        await PersistDomainEventAsync(new ResponsesTaskRecordedEvent
        {
            Task = task,
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
            ResultJson = NormalizeOptional(command.ResultJson) ?? "{}",
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
            .On<ResponsesTaskRecordedEvent>(ApplyTaskRecorded)
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

    private static ResponsesAgentToolState ApplyTaskRecorded(
        ResponsesAgentToolState state,
        ResponsesTaskRecordedEvent evt)
    {
        var next = state.Clone();
        if (evt.Task != null)
            next.TaskTraces.Add(evt.Task.Clone());
        Touch(next, evt.Task?.UpdatedAt);
        Bump(next, $"{next.Record?.ScopeId}:task:{evt.Task?.TaskId}");
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
                ResultJson = trace.ResultJson,
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

        existing.ResultJson = trace.ResultJson;
        existing.Url = trace.Url;
        existing.Query = trace.Query;
        existing.CachedAt = trace.ObservedAt.Clone();
    }

    private static List<ResponsesTodoItem> ParseTodoItems(
        string? argumentsJson,
        string? sourceResponseId,
        Timestamp observedAt)
    {
        var items = new List<ResponsesTodoItem>();
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return items;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("todos", out var todos) &&
                todos.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var todo in todos.EnumerateArray())
                {
                    var item = ParseTodoItem(todo, index, sourceResponseId, observedAt);
                    if (item != null)
                        items.Add(item);
                    index++;
                }
                return items;
            }

            var single = ParseTodoItem(root, 0, sourceResponseId, observedAt);
            if (single != null)
                items.Add(single);
        }
        catch (JsonException)
        {
            var content = NormalizeOptional(argumentsJson);
            if (content != null)
                items.Add(CreateTodoItem(null, content, "pending", 0, sourceResponseId, observedAt));
        }

        return items;
    }

    private static ResponsesTodoItem? ParseTodoItem(
        JsonElement element,
        int index,
        string? sourceResponseId,
        Timestamp observedAt)
    {
        if (element.ValueKind == JsonValueKind.String)
            return CreateTodoItem(null, element.GetString(), "pending", index, sourceResponseId, observedAt);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var content = GetString(element, "content")
                      ?? GetString(element, "task")
                      ?? GetString(element, "title")
                      ?? GetString(element, "text");
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var id = GetString(element, "id");
        var status = GetString(element, "status") ?? "pending";
        return CreateTodoItem(id, content, status, index, sourceResponseId, observedAt);
    }

    private static ResponsesTodoItem CreateTodoItem(
        string? id,
        string? content,
        string status,
        int index,
        string? sourceResponseId,
        Timestamp observedAt)
    {
        var normalizedContent = NormalizeRequired(content);
        var normalizedStatus = NormalizeOptional(status) ?? "pending";
        var itemId = NormalizeOptional(id) ?? BuildStableTodoId(normalizedContent, index);
        return new ResponsesTodoItem
        {
            Id = itemId,
            Content = normalizedContent,
            Status = normalizedStatus,
            SourceResponseId = NormalizeOptional(sourceResponseId) ?? string.Empty,
            CreatedAt = observedAt.Clone(),
            UpdatedAt = observedAt.Clone(),
        };
    }

    private static string BuildStableTodoId(string content, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{index}\n{content}"));
        return "todo_" + Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
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

    private static void ValidateTask(ResponsesTaskTrace task)
    {
        if (string.IsNullOrWhiteSpace(task.TaskId))
            throw new InvalidOperationException("task_id is required.");
        if (string.IsNullOrWhiteSpace(task.ChildActorId))
            throw new InvalidOperationException("child_actor_id is required.");
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
