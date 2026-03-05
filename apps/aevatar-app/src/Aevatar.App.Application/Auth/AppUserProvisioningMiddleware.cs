using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.App.Application.Auth;

public sealed record AppAuthContext(
    AuthUserInfo AuthUser,
    string UserId);

public sealed class AppUserProvisioningMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAppAuthService? _authService;
    private readonly IActorAccessAppService _actors;
    private readonly IProjectionDocumentStore<AppAuthLookupReadModel, string> _authLookupStore;
    private readonly IProjectionDocumentStore<AppUserAccountReadModel, string> _accountStore;
    private readonly IAppProjectionManager _projectionManager;
    private readonly ILogger<AppUserProvisioningMiddleware> _logger;

    public AppUserProvisioningMiddleware(
        RequestDelegate next,
        IAppAuthService? authService,
        IActorAccessAppService actors,
        IProjectionDocumentStore<AppAuthLookupReadModel, string> authLookupStore,
        IProjectionDocumentStore<AppUserAccountReadModel, string> accountStore,
        IAppProjectionManager projectionManager,
        ILogger<AppUserProvisioningMiddleware> logger)
    {
        _next = next;
        _authService = authService;
        _actors = actors;
        _authLookupStore = authLookupStore;
        _accountStore = accountStore;
        _projectionManager = projectionManager;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IAppAuthContextAccessor authAccessor)
    {
        var authUser = await TryAuthenticateAsync(context);
        if (authUser is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token" });
            return;
        }

        var (userId, isNew) = await FindOrCreateUserAsync(authUser);
        if (isNew)
            _logger.LogInformation("First login for user: {Email}", authUser.Email);

        authAccessor.AuthContext = new AppAuthContext(authUser, userId);

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

    private async Task<(string UserId, bool IsNew)> FindOrCreateUserAsync(AuthUserInfo authUser)
    {
        var providerKey = $"{authUser.Provider}:{authUser.ProviderId}";
        var providerLookup = await _authLookupStore.GetAsync(_actors.ResolveActorId<AuthLookupGAgent>(providerKey));
        var existingByProvider = providerLookup?.UserId is { Length: > 0 } ? providerLookup.UserId : null;

        if (existingByProvider is not null)
        {
            await EnsureUserProjectionsAsync(existingByProvider);

            await _actors.SendCommandAsync<UserAccountGAgent>(existingByProvider,
                new UserLoginUpdatedEvent { Email = authUser.Email, EmailVerified = authUser.EmailVerified });

            return (existingByProvider, false);
        }

        var emailKey = $"email:{authUser.Email}";
        var emailLookup = await _authLookupStore.GetAsync(_actors.ResolveActorId<AuthLookupGAgent>(emailKey));
        var existingByEmail = emailLookup?.UserId is { Length: > 0 } ? emailLookup.UserId : null;

        if (existingByEmail is not null)
        {
            _logger.LogInformation("Linking {Email} to existing user (provider: {Provider})",
                authUser.Email, authUser.Provider);

            await EnsureUserProjectionsAsync(existingByEmail);
            await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<AuthLookupGAgent>(providerKey));

            await _actors.SendCommandAsync<UserAccountGAgent>(existingByEmail,
                new UserProviderLinkedEvent
                {
                    AuthProvider = authUser.Provider,
                    AuthProviderId = authUser.ProviderId,
                    EmailVerified = authUser.EmailVerified
                });

            await _actors.SendCommandAsync<AuthLookupGAgent>(providerKey,
                new AuthLookupSetEvent { LookupKey = providerKey, UserId = existingByEmail });

            return (existingByEmail, false);
        }

        var userId = Guid.NewGuid().ToString();

        await EnsureUserProjectionsAsync(userId);
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<AuthLookupGAgent>(providerKey));
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<AuthLookupGAgent>(emailKey));

        await _actors.SendCommandAsync<UserAccountGAgent>(userId,
            new UserRegisteredEvent
            {
                UserId = userId,
                AuthProvider = authUser.Provider,
                AuthProviderId = authUser.ProviderId,
                Email = authUser.Email,
                EmailVerified = authUser.EmailVerified
            });

        await _actors.SendCommandAsync<AuthLookupGAgent>(providerKey,
            new AuthLookupSetEvent { LookupKey = providerKey, UserId = userId });

        await _actors.SendCommandAsync<AuthLookupGAgent>(emailKey,
            new AuthLookupSetEvent { LookupKey = emailKey, UserId = userId });

        var now = DateTimeOffset.UtcNow;
        await _accountStore.UpsertAsync(new AppUserAccountReadModel
        {
            Id = _actors.ResolveActorId<UserAccountGAgent>(userId),
            UserId = userId,
            AuthProvider = authUser.Provider,
            AuthProviderId = authUser.ProviderId,
            Email = authUser.Email,
            EmailVerified = authUser.EmailVerified,
            CreatedAt = now,
            LastLoginAt = now,
        });

        var providerActorId = _actors.ResolveActorId<AuthLookupGAgent>(providerKey);
        await _authLookupStore.UpsertAsync(new AppAuthLookupReadModel
        {
            Id = providerActorId,
            LookupKey = providerKey,
            UserId = userId,
        });
        var emailActorId = _actors.ResolveActorId<AuthLookupGAgent>(emailKey);
        await _authLookupStore.UpsertAsync(new AppAuthLookupReadModel
        {
            Id = emailActorId,
            LookupKey = emailKey,
            UserId = userId,
        });

        _logger.LogInformation("Created new user: {Email} ({Provider})", authUser.Email, authUser.Provider);
        return (userId, true);
    }

    private async Task EnsureUserProjectionsAsync(string userId)
    {
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<UserAccountGAgent>(userId));
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<UserProfileGAgent>(userId));
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<SyncEntityGAgent>(userId));
    }
}
