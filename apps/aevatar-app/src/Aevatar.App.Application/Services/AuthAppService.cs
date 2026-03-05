using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.App.GAgents;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.App.Application.Services;

public sealed class AuthAppService : IAuthAppService
{
    private readonly IActorAccessAppService _actors;
    private readonly IProjectionDocumentStore<AppAuthLookupReadModel, string> _authLookupStore;
    private readonly IAppProjectionManager _projectionManager;

    public AuthAppService(
        IActorAccessAppService actors,
        IProjectionDocumentStore<AppAuthLookupReadModel, string> authLookupStore,
        IAppProjectionManager projectionManager)
    {
        _actors = actors;
        _authLookupStore = authLookupStore;
        _projectionManager = projectionManager;
    }

    public async Task<TrialRegisterResult> RegisterTrialAsync(string email, string trialTokenSecret)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var emailKey = $"email:{normalizedEmail}";

        var lookupKey = _actors.ResolveActorId<AuthLookupGAgent>(emailKey);
        var lookup = await _authLookupStore.GetAsync(lookupKey);
        var existingUserId = lookup?.UserId;

        if (existingUserId is { Length: > 0 })
        {
            var existingTrialId = $"trial_{existingUserId}";
            var existingToken = GenerateTrialToken(existingUserId, normalizedEmail, trialTokenSecret);
            return TrialRegisterResult.Existing(existingToken, existingTrialId);
        }

        var userId = Guid.NewGuid().ToString("N");
        var trialId = $"trial_{userId}";

        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<UserAccountGAgent>(userId));
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<UserProfileGAgent>(userId));
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<SyncEntityGAgent>(userId));

        await _actors.SendCommandAsync<UserAccountGAgent>(userId,
            new UserRegisteredEvent
            {
                UserId = userId,
                AuthProvider = "trial",
                AuthProviderId = trialId,
                Email = normalizedEmail,
                EmailVerified = false
            });

        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<AuthLookupGAgent>(emailKey));
        await _actors.SendCommandAsync<AuthLookupGAgent>(emailKey,
            new AuthLookupSetEvent { LookupKey = emailKey, UserId = userId });

        var providerKey = $"trial:{trialId}";
        await _projectionManager.EnsureSubscribedAsync(_actors.ResolveActorId<AuthLookupGAgent>(providerKey));
        await _actors.SendCommandAsync<AuthLookupGAgent>(providerKey,
            new AuthLookupSetEvent { LookupKey = providerKey, UserId = userId });

        var token = GenerateTrialToken(userId, normalizedEmail, trialTokenSecret);
        return TrialRegisterResult.Created(token, trialId);
    }

    private static string GenerateTrialToken(string userId, string email, string secret)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("email", email),
            new Claim("type", "trial"),
            new Claim("app", "aevatar-app")
        };
        var token = new JwtSecurityToken(claims: claims, signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed record TrialRegisterResult(string Token, string TrialId, bool IsExisting)
{
    public static TrialRegisterResult Created(string token, string trialId) =>
        new(token, trialId, IsExisting: false);

    public static TrialRegisterResult Existing(string token, string trialId) =>
        new(token, trialId, IsExisting: true);
}
