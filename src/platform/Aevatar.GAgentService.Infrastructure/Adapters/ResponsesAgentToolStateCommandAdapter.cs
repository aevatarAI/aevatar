using System.Text.Json;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Core.GAgents;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.Adapters;

public sealed class ResponsesAgentToolStateCommandAdapter : IResponsesAgentToolStateCommandPort
{
    private const string PublisherId = "gagent-service.responses-agent-tools";

    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IResponsesAgentToolStateCurrentStateProjectionPort _projectionPort;

    public ResponsesAgentToolStateCommandAdapter(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        IResponsesAgentToolStateCurrentStateProjectionPort projectionPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
    }

    public async Task<ResponsesTodoWriteResult> ApplyTodoWriteAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var actor = await EnsureActorAsync(scopeId, ownerSubject, ct);
        var observedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(new ApplyResponsesTodoWriteRequested
                {
                    ScopeId = scopeId.Trim(),
                    OwnerSubject = ownerSubject.Trim(),
                    SourceResponseId = NormalizeOptional(sourceResponseId) ?? string.Empty,
                    ArgumentsJson = NormalizeOptional(argumentsJson) ?? "{}",
                    ObservedAt = observedAt,
                }),
                $"{sourceResponseId}:todo:{Guid.NewGuid():N}"),
            ct);

        var todos = PreviewTodoItems(argumentsJson, sourceResponseId, observedAt.ToDateTimeOffset());
        return new ResponsesTodoWriteResult(actor.Id, sourceResponseId, todos);
    }

    public async Task<ResponsesTaskDispatchResult> RecordTaskAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var actor = await EnsureActorAsync(scopeId, ownerSubject, ct);
        var taskId = ResponseAgentToolStateIds.NewTaskId();
        var childActorId = $"{actor.Id}-task-{taskId["task_".Length..]}";
        var description = ExtractTaskDescription(argumentsJson);
        var resultJson = JsonSerializer.Serialize(new
        {
            task_id = taskId,
            child_actor_id = childActorId,
            status = "accepted",
            note = "Task dispatch has been recorded in Aevatar task topology state. Full sub-agent execution is owned by the GAgent topology issue.",
        });

        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(new RecordResponsesTaskRequested
                {
                    SourceResponseId = NormalizeOptional(sourceResponseId) ?? string.Empty,
                    TaskId = taskId,
                    ChildActorId = childActorId,
                    Description = description,
                    ArgumentsJson = NormalizeOptional(argumentsJson) ?? "{}",
                    ResultJson = resultJson,
                    Status = ResponsesAgentToolTaskStatus.Accepted,
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
                $"{sourceResponseId}:task:{taskId}"),
            ct);

        return new ResponsesTaskDispatchResult(actor.Id, taskId, childActorId, "accepted", resultJson);
    }

    public async Task<ResponsesWebTraceResult> RecordWebTraceAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        ResponsesWebTraceInput trace,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var actor = await EnsureActorAsync(scopeId, ownerSubject, ct);
        var traceId = string.IsNullOrWhiteSpace(trace.TraceId)
            ? ResponseAgentToolStateIds.NewWebTraceId()
            : trace.TraceId.Trim();
        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(new RecordResponsesWebTraceRequested
                {
                    SourceResponseId = NormalizeOptional(sourceResponseId) ?? string.Empty,
                    TraceId = traceId,
                    ToolName = trace.ToolName.Trim(),
                    CacheKey = trace.CacheKey.Trim(),
                    Url = NormalizeOptional(trace.Url) ?? string.Empty,
                    Query = NormalizeOptional(trace.Query) ?? string.Empty,
                    CacheHit = trace.CacheHit,
                    ResultJson = NormalizeOptional(trace.ResultJson) ?? "{}",
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
                $"{sourceResponseId}:web:{traceId}"),
            ct);

        return new ResponsesWebTraceResult(actor.Id, traceId, trace.CacheKey, trace.CacheHit, trace.ResultJson);
    }

    private async Task<IActor> EnsureActorAsync(
        string scopeId,
        string ownerSubject,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("scopeId is required.", nameof(scopeId));
        if (string.IsNullOrWhiteSpace(ownerSubject))
            throw new ArgumentException("ownerSubject is required.", nameof(ownerSubject));

        var actorId = ResponseAgentToolStateIds.BuildActorId(scopeId, ownerSubject);
        var actor = await _runtime.CreateAsync<ResponsesAgentToolStateGAgent>(actorId, ct: ct);
        await _projectionPort.EnsureProjectionAsync(actor.Id, ct);
        await _dispatchPort.DispatchAsync(
            actor.Id,
            CreateEnvelope(
                actor.Id,
                Any.Pack(new RegisterResponsesAgentToolStateRequested
                {
                    Record = new ResponsesAgentToolStateRecord
                    {
                        ScopeId = scopeId.Trim(),
                        OwnerSubject = ownerSubject.Trim(),
                        CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                        UpdatedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                    },
                }),
                $"{actor.Id}:registered"),
            ct);
        return actor;
    }

    private static EventEnvelope CreateEnvelope(
        string actorId,
        Any payload,
        string commandId) =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(commandId) ? Guid.NewGuid().ToString("N") : commandId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = payload,
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = commandId,
            },
        };

    private static IReadOnlyList<ResponsesTodoItemSnapshot> PreviewTodoItems(
        string? argumentsJson,
        string sourceResponseId,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return [];

        var result = new List<ResponsesTodoItemSnapshot>();
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
                    var item = PreviewTodoItem(todo, index, sourceResponseId, observedAt);
                    if (item != null)
                        result.Add(item);
                    index++;
                }
                return result;
            }

            var single = PreviewTodoItem(root, 0, sourceResponseId, observedAt);
            return single == null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ResponsesTodoItemSnapshot? PreviewTodoItem(
        JsonElement element,
        int index,
        string sourceResponseId,
        DateTimeOffset observedAt)
    {
        string? content;
        string? id = null;
        var status = "pending";
        if (element.ValueKind == JsonValueKind.String)
        {
            content = element.GetString();
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            content = GetString(element, "content")
                      ?? GetString(element, "task")
                      ?? GetString(element, "title")
                      ?? GetString(element, "text");
            id = GetString(element, "id");
            status = GetString(element, "status") ?? status;
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(content))
            return null;

        id = NormalizeOptional(id) ?? "todo_" + index.ToString("D4");
        return new ResponsesTodoItemSnapshot(
            id,
            content.Trim(),
            status.Trim(),
            sourceResponseId,
            observedAt,
            observedAt);
    }

    private static string ExtractTaskDescription(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return argumentsJson.Trim();
            return GetString(root, "description")
                   ?? GetString(root, "prompt")
                   ?? GetString(root, "task")
                   ?? GetString(root, "input")
                   ?? argumentsJson.Trim();
        }
        catch (JsonException)
        {
            return argumentsJson.Trim();
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
