using Aevatar.GAgentService.Abstractions.Queries;
namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponsesAgentToolStateCommandPort
{
    Task<ResponsesTodoWriteResult> ApplyTodoWriteAsync(
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

public sealed record ResponsesWebTraceResult(
    string ActorId,
    string TraceId,
    string CacheKey,
    bool CacheHit,
    ResponsesWebToolResult Result);

public sealed record ResponsesWebTraceInput(
    string TraceId,
    string ToolName,
    string CacheKey,
    string Url,
    string Query,
    bool CacheHit,
    ResponsesWebToolResult Result);
