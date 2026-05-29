namespace Aevatar.GAgentService.Abstractions.Queries;

public sealed record ResponsesAgentToolStateSnapshot(
    string ActorId,
    string ScopeId,
    string OwnerSubject,
    long StateVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ResponsesTodoItemSnapshot> Todos,
    IReadOnlyList<ResponsesWebTraceSnapshot> WebTraces,
    IReadOnlyList<ResponsesWebCacheEntrySnapshot> WebCacheEntries);

public sealed record ResponsesTodoItemSnapshot(
    string Id,
    string Content,
    string Status,
    string SourceResponseId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ResponsesWebTraceSnapshot(
    string TraceId,
    string SourceResponseId,
    string ToolName,
    string CacheKey,
    string Url,
    string Query,
    bool CacheHit,
    ResponsesWebToolResult Result,
    DateTimeOffset ObservedAt);

public sealed record ResponsesWebCacheEntrySnapshot(
    string CacheKey,
    string ToolName,
    string Url,
    string Query,
    ResponsesWebToolResult Result,
    DateTimeOffset CachedAt,
    DateTimeOffset? LastHitAt,
    long HitCount);
