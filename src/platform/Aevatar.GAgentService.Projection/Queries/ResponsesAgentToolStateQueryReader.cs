using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ResponsesAgentToolStateQueryReader : IResponsesAgentToolStateQueryPort
{
    private readonly IProjectionDocumentReader<ResponsesAgentToolStateCurrentStateReadModel, string> _reader;

    public ResponsesAgentToolStateQueryReader(
        IProjectionDocumentReader<ResponsesAgentToolStateCurrentStateReadModel, string> reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<ResponsesAgentToolStateSnapshot?> GetAsync(
        string scopeId,
        string ownerSubject,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(ownerSubject))
            return null;

        var document = await GetDocumentAsync(scopeId, ownerSubject, ct);
        return document == null ? null : Map(document);
    }

    public async Task<ResponsesWebCacheEntrySnapshot?> GetWebCacheEntryAsync(
        string scopeId,
        string ownerSubject,
        string toolName,
        string cacheKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(cacheKey))
            return null;

        var snapshot = await GetAsync(scopeId, ownerSubject, ct);
        return snapshot?.WebCacheEntries.FirstOrDefault(entry =>
            string.Equals(entry.ToolName, toolName.Trim(), StringComparison.Ordinal) &&
            string.Equals(entry.CacheKey, cacheKey.Trim(), StringComparison.Ordinal));
    }

    private static ResponsesAgentToolStateSnapshot Map(ResponsesAgentToolStateCurrentStateReadModel document) =>
        new(
            document.ActorId,
            document.ScopeId,
            document.OwnerSubject,
            document.StateVersion,
            document.CreatedAt,
            document.UpdatedAt,
            document.Todos.Select(static todo => new ResponsesTodoItemSnapshot(
                todo.Id,
                todo.Content,
                todo.Status,
                todo.SourceResponseId,
                todo.CreatedAt,
                todo.UpdatedAt)).ToArray(),
            document.WebTraces.Select(static trace => new ResponsesWebTraceSnapshot(
                trace.TraceId,
                trace.SourceResponseId,
                trace.ToolName,
                trace.CacheKey,
                trace.Url,
                trace.Query,
                trace.CacheHit,
                ResolveWebResult(trace.TypedResult, trace.Result),
                trace.ObservedAt)).ToArray(),
            document.WebCacheEntries.Select(static entry => new ResponsesWebCacheEntrySnapshot(
                entry.CacheKey,
                entry.ToolName,
                entry.Url,
                entry.Query,
                ResolveWebResult(entry.TypedResult, entry.Result),
                entry.CachedAt,
                entry.LastHitAt,
                entry.HitCount)).ToArray());

    private static ResponsesWebToolResult ResolveWebResult(
        ResponsesWebToolResult? typedResult,
        Google.Protobuf.WellKnownTypes.Value? legacyResult)
    {
        if (typedResult != null && typedResult.ResultCase != ResponsesWebToolResult.ResultOneofCase.None)
            return typedResult.Clone();

        // Refactor (iter161-cluster-001 #1251-first):
        //   Old pattern: query snapshots exposed legacy Value directly.
        //   New principle: typed result is primary; legacy Value is converted only for old readmodels.
        return ResponsesWebResultMigration.FromLegacyValue(legacyResult);
    }

    private async Task<ResponsesAgentToolStateCurrentStateReadModel?> GetDocumentAsync(
        string scopeId,
        string ownerSubject,
        CancellationToken ct)
    {
        var actorId = ResponseAgentToolStateIds.BuildActorId(scopeId, ownerSubject);
        var document = await _reader.GetAsync(actorId, ct);
        if (document != null)
            return document;

        var legacyActorId = ResponseAgentToolStateIds.BuildLegacyActorId(scopeId, ownerSubject);
        return string.Equals(actorId, legacyActorId, StringComparison.Ordinal)
            ? null
            : await _reader.GetAsync(legacyActorId, ct);
    }
}
