using Aevatar.GAgentService.Abstractions.Queries;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponsesAgentToolStateCommandPort
{
    Task<ResponsesTodoWriteResult> ApplyTodoWriteAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        string argumentsJson,
        CancellationToken ct = default);

    Task<ResponsesTaskDispatchResult> RecordTaskAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        string argumentsJson,
        CancellationToken ct = default);

    Task<ResponsesWebTraceResult> RecordWebTraceAsync(
        string scopeId,
        string ownerSubject,
        string sourceResponseId,
        ResponsesWebTraceInput trace,
        CancellationToken ct = default);
}

public sealed record ResponsesTodoWriteResult(
    string ActorId,
    string SourceResponseId,
    IReadOnlyList<ResponsesTodoItemSnapshot> Todos);

public sealed record ResponsesTaskDispatchResult(
    string ActorId,
    string TaskId,
    string ChildActorId,
    string Status,
    string ResultJson);

public sealed record ResponsesWebTraceResult(
    string ActorId,
    string TraceId,
    string CacheKey,
    bool CacheHit,
    Value Result);

public sealed record ResponsesWebTraceInput(
    string TraceId,
    string ToolName,
    string CacheKey,
    string Url,
    string Query,
    bool CacheHit,
    Value Result);
