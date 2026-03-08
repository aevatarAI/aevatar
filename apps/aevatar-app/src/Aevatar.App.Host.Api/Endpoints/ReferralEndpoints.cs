using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Projection.Orchestration;
using Aevatar.App.Application.Services;
using Aevatar.App.GAgents;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;

namespace Aevatar.App.Host.Api.Endpoints;

public static class ReferralEndpoints
{
    public static void MapReferralEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/referral/click", async (
            ReferralClickRequest request,
            IToltAppService toltService) =>
        {
            if (string.IsNullOrWhiteSpace(request.Ref))
                return Results.BadRequest(new { error = "ref is required" });

            var result = await toltService.TrackClickAsync(
                request.Ref, request.Page ?? "", request.Device);

            return result is not null
                ? Results.Ok(new { ReferralCode = result.PartnerId })
                : Results.NotFound(new { error = "Referral code not found" });
        });

        app.MapPost("/api/referral/bind", async (
            ReferralBindRequest request,
            IAppAuthContextAccessor authAccessor,
            IToltAppService toltService,
            IActorAccessAppService actorService,
            IAppProjectionManager projectionManager) =>
        {
            var auth = authAccessor.AuthContext;
            if (auth is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.ReferralCode))
                return Results.BadRequest(new { error = "Referral code is required" });

            var result = await toltService.BindReferralAsync(
                auth.AuthUser.Email, request.ReferralCode, auth.UserId);

            if (!result.Success)
                return Results.UnprocessableEntity(new { error = result.Error });

            await projectionManager.EnsureSubscribedAsync(
                actorService.ResolveActorId<UserAffiliateGAgent>(auth.UserId));

            await actorService.SendCommandAsync<UserAffiliateGAgent>(auth.UserId,
                new UserAffiliateCreatedEvent
                {
                    UserId = auth.UserId,
                    CustomerId = result.CustomerId ?? "",
                    Platform = "tolt",
                    CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                });

            return Results.Ok(new { success = true });
        });
    }
}

public sealed record ReferralClickRequest(string? Ref, string? Page, string? Device);

public sealed record ReferralBindRequest(string? ReferralCode);
