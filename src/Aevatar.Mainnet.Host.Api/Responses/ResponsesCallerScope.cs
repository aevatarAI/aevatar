using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed record ResponsesCallerScope(
    string ScopeId,
    string OwnerSubject,
    LlmSessionOriginKind OriginKind);

internal interface IResponsesCallerScopeResolver
{
    Task<ResponsesCallerScope> ResolveAsync(
        string nyxIdAccessToken,
        HttpContext http,
        CancellationToken ct = default);
}

internal sealed class NyxIdResponsesCallerScopeResolver : IResponsesCallerScopeResolver
{
    private readonly INyxIdCurrentUserResolver _currentUserResolver;

    public NyxIdResponsesCallerScopeResolver(INyxIdCurrentUserResolver currentUserResolver)
    {
        _currentUserResolver = currentUserResolver ?? throw new ArgumentNullException(nameof(currentUserResolver));
    }

    public async Task<ResponsesCallerScope> ResolveAsync(
        string nyxIdAccessToken,
        HttpContext http,
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

internal sealed class ResponsesCallerScopeUnavailableException : Exception
{
    public ResponsesCallerScopeUnavailableException(string message) : base(message)
    {
    }
}
