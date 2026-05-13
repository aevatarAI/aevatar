using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IResponsesAgentToolStateQueryPort
{
    Task<ResponsesAgentToolStateSnapshot?> GetAsync(
        string scopeId,
        string ownerSubject,
        CancellationToken ct = default);

    Task<ResponsesWebCacheEntrySnapshot?> GetWebCacheEntryAsync(
        string scopeId,
        string ownerSubject,
        string toolName,
        string cacheKey,
        CancellationToken ct = default);
}
