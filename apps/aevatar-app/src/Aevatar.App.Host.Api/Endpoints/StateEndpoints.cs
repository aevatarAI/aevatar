using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Services;
using Aevatar.App.Host.Api.Endpoints.Mappers;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.App.Host.Api.Endpoints;

public static class StateEndpoints
{
    public static void MapStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/state", async (
            [FromServices] IAppAuthContextAccessor authAccessor,
            [FromServices] ISyncAppService syncService) =>
        {
            var auth = authAccessor.RequireAuthContext();
            var state = await syncService.GetStateAsync(auth.UserId);

            if (state.ServerRevision == 0)
            {
                return Results.Ok(new
                {
                    entities = new Dictionary<string, object>(),
                    serverRevision = 0
                });
            }

            return Results.Ok(new
            {
                entities = EntityMapMapper.ToDto(state.GroupedEntities),
                serverRevision = state.ServerRevision
            });
        });
    }
}
