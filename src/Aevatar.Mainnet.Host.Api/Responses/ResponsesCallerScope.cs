using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Application.Responses;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class NyxIdResponsesCallerScopeResolver : IResponsesCallerScopeResolver
{
    private readonly INyxIdCurrentUserResolver _currentUserResolver;
    private readonly NyxIdIdentityAssertionValidator _identityAssertionValidator;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public NyxIdResponsesCallerScopeResolver(
        INyxIdCurrentUserResolver currentUserResolver,
        NyxIdIdentityAssertionValidator identityAssertionValidator,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _currentUserResolver = currentUserResolver ?? throw new ArgumentNullException(nameof(currentUserResolver));
        _identityAssertionValidator = identityAssertionValidator ??
                                      throw new ArgumentNullException(nameof(identityAssertionValidator));
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<ResponsesCallerScope> ResolveAsync(
        ResponsesCallerScopeResolutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.IsNullOrWhiteSpace(context.NyxIdIdentityToken))
        {
            if (TryGetRequestValidatedSubject(out var requestValidatedSubject))
                return ScopeForSubject(requestValidatedSubject);

            var validation = await _identityAssertionValidator.ValidateAsync(context.NyxIdIdentityToken, ct);
            if (!validation.Succeeded || string.IsNullOrWhiteSpace(validation.Subject))
            {
                throw new ResponsesCallerScopeUnavailableException(
                    $"NyxID identity assertion is invalid: {validation.ErrorCode ?? "identity_assertion_invalid"}.");
            }

            return ScopeForSubject(validation.Subject);
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

    private bool TryGetRequestValidatedSubject(out string subject)
    {
        subject = string.Empty;
        var items = _httpContextAccessor?.HttpContext?.Items;
        if (items is null ||
            !items.TryGetValue(NyxIdIdentityAssertionAuthentication.ValidatedSubjectItemKey, out var value) ||
            value is not string validatedSubject ||
            string.IsNullOrWhiteSpace(validatedSubject))
        {
            return false;
        }

        subject = validatedSubject.Trim();
        return true;
    }

    private static ResponsesCallerScope ScopeForSubject(string subject)
    {
        var normalizedSubject = subject.Trim();
        return new ResponsesCallerScope(
            ScopeId: normalizedSubject,
            OwnerSubject: normalizedSubject,
            OriginKind: LlmSessionOriginKind.ApiKey);
    }
}
