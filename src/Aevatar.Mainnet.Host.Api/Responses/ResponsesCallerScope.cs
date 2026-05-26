using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class NyxIdResponsesCallerScopeResolver : IResponsesCallerScopeResolver
{
    private readonly INyxIdCurrentUserResolver _currentUserResolver;

    public NyxIdResponsesCallerScopeResolver(INyxIdCurrentUserResolver currentUserResolver)
    {
        _currentUserResolver = currentUserResolver ?? throw new ArgumentNullException(nameof(currentUserResolver));
    }

    public async Task<ResponsesCallerScope> ResolveAsync(
        string nyxIdAccessToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nyxIdAccessToken))
            throw new ResponsesCallerScopeUnavailableException("NyxID access token is required.");

        var userId = await _currentUserResolver.ResolveCurrentUserIdAsync(nyxIdAccessToken, ct);
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ResponsesCallerScopeUnavailableException(
                "Could not resolve current NyxID user id from the bearer token.");
        }

        var normalizedUserId = userId.Trim();
        return new ResponsesCallerScope(
            ScopeId: normalizedUserId,
            OwnerSubject: normalizedUserId,
            OriginKind: LlmSessionOriginKind.ApiKey);
    }
}
