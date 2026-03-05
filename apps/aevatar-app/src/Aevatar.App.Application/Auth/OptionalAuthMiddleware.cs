using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Auth;

public sealed class OptionalAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAppAuthService? _authService;
    private readonly IActorAccessAppService _actors;
    private readonly IProjectionDocumentStore<AppAuthLookupReadModel, string> _authLookupStore;
    private readonly ILogger<OptionalAuthMiddleware> _logger;

    public OptionalAuthMiddleware(
        RequestDelegate next,
        IAppAuthService? authService,
        IActorAccessAppService actors,
        IProjectionDocumentStore<AppAuthLookupReadModel, string> authLookupStore,
        ILogger<OptionalAuthMiddleware> logger)
    {
        _next = next;
        _authService = authService;
        _actors = actors;
        _authLookupStore = authLookupStore;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAppAuthContextAccessor authAccessor)
    {
        var authUser = await TryAuthenticateAsync(context);
        if (authUser is not null)
        {
            try
            {
                var providerKey = $"{authUser.Provider}:{authUser.ProviderId}";
                var lookup = await _authLookupStore.GetAsync(_actors.ResolveActorId<AuthLookupGAgent>(providerKey));
                var userId = lookup?.UserId is { Length: > 0 } ? lookup.UserId : null;
                if (userId is not null)
                    authAccessor.AuthContext = new AppAuthContext(authUser, userId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Optional auth lookup failed; continuing unauthenticated");
            }
        }

        await _next(context);
    }

    private async Task<AuthUserInfo?> TryAuthenticateAsync(HttpContext context)
    {
        try
        {
            var authResult = await context.AuthenticateAsync(AppAuthSchemeProvider.AppAuthScheme);
            if (authResult.Succeeded)
                return authResult.Principal.ToAuthUserInfo();
        }
        catch (Exception)
        {
        }

        if (_authService is null)
            return null;

        var token = AppAuthSchemeProvider.TryGetBearerToken(context);
        return string.IsNullOrEmpty(token)
            ? null
            : await _authService.ValidateTokenAsync(token);
    }
}
