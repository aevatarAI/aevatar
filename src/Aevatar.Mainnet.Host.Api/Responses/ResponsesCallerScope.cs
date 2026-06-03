using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class NyxIdResponsesCallerScopeResolver : IResponsesCallerScopeResolver
{
    private readonly INyxIdCurrentUserResolver _currentUserResolver;
    private readonly NyxIdIdentityAssertionValidator _identityAssertionValidator;

    public NyxIdResponsesCallerScopeResolver(
        INyxIdCurrentUserResolver currentUserResolver,
        NyxIdIdentityAssertionValidator identityAssertionValidator)
    {
        _currentUserResolver = currentUserResolver ?? throw new ArgumentNullException(nameof(currentUserResolver));
        _identityAssertionValidator = identityAssertionValidator ??
                                      throw new ArgumentNullException(nameof(identityAssertionValidator));
    }

    public async Task<ResponsesCallerScope> ResolveAsync(
        ResponsesCallerScopeResolutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrWhiteSpace(context.NyxIdIdentityToken))
        {
            var validation = await _identityAssertionValidator.ValidateAsync(context.NyxIdIdentityToken, ct);
            if (!validation.Succeeded || string.IsNullOrWhiteSpace(validation.Subject))
            {
                throw new ResponsesCallerScopeUnavailableException(
                    $"NyxID identity assertion is invalid: {validation.ErrorCode ?? "identity_assertion_invalid"}.");
            }

            var normalizedSubject = validation.Subject.Trim();
            return new ResponsesCallerScope(
                ScopeId: normalizedSubject,
                OwnerSubject: normalizedSubject,
                OriginKind: LlmSessionOriginKind.ApiKey);
        }

        var nyxIdAccessToken = context.InboundBearerToken;
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
