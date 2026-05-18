using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Projection.ReadModels;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ResponsesAgentToolStateQueryReader : IResponsesAgentToolStateQueryPort
{
    private readonly IProjectionDocumentReader<ResponsesAgentToolStateCurrentStateReadModel, string> _reader;
    private readonly ResponsesAgentToolStateIdOptions _idOptions;

    public ResponsesAgentToolStateQueryReader(
        IProjectionDocumentReader<ResponsesAgentToolStateCurrentStateReadModel, string> reader,
        IOptions<ResponsesAgentToolStateIdOptions>? idOptions = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _idOptions = idOptions?.Value ?? new ResponsesAgentToolStateIdOptions();
    }

    public async Task<ResponsesAgentToolStateSnapshot?> GetAsync(
        string scopeId,
        string ownerSubject,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(ownerSubject))
            return null;

        var normalizedScopeId = scopeId.Trim();
        var normalizedOwnerSubject = ownerSubject.Trim();
        var actorId = ResponseAgentToolStateIds.BuildActorId(normalizedScopeId, normalizedOwnerSubject, _idOptions);
        var document = await _reader.GetAsync(actorId, ct);
        if (document == null && _idOptions.AevatarResponsesAgentToolReadableIds)
        {
            // Dual-read rollout: readable ids are tried first, then the legacy hash id. Remove
            // this hash fallback after the 30-day window documented in
            // docs/adr/0024-responses-agent-tool-actor-id-scheme.md.
            var legacyActorId = ResponseAgentToolStateIds.BuildLegacyActorId(normalizedScopeId, normalizedOwnerSubject);
            document = await _reader.GetAsync(legacyActorId, ct);
        }

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
            document.Tasks.Select(static task => new ResponsesTaskTraceSnapshot(
                task.TaskId,
                task.SourceResponseId,
                task.ChildActorId,
                task.Description,
                task.Status,
                ResponsesJsonValues.ToBoundaryJson(task.Arguments),
                ResponsesJsonValues.ToBoundaryJson(task.Result),
                task.CreatedAt,
                task.UpdatedAt)).ToArray(),
            document.WebTraces.Select(static trace => new ResponsesWebTraceSnapshot(
                trace.TraceId,
                trace.SourceResponseId,
                trace.ToolName,
                trace.CacheKey,
                trace.Url,
                trace.Query,
                trace.CacheHit,
                ResponsesJsonValues.ToBoundaryJson(trace.Result),
                trace.ObservedAt)).ToArray(),
            document.WebCacheEntries.Select(static entry => new ResponsesWebCacheEntrySnapshot(
                entry.CacheKey,
                entry.ToolName,
                entry.Url,
                entry.Query,
                ResponsesJsonValues.ToBoundaryJson(entry.Result),
                entry.CachedAt,
                entry.LastHitAt,
                entry.HitCount)).ToArray());

}
