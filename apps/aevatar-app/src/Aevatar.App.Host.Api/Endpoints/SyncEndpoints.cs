using System.Text.Json;
using Aevatar.App.Application.Auth;
using Aevatar.App.Application.Contracts;
using Aevatar.App.Application.Services;
using Aevatar.App.Application.Validation;
using Aevatar.App.GAgents;
using Aevatar.App.Host.Api.Endpoints.Mappers;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Host.Api.Endpoints;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sync", async (
            HttpContext ctx,
            [FromServices] ISyncAppService syncService,
            [FromServices] IAppAuthContextAccessor authAccessor,
            IValidator<SyncRequestDto> validator) =>
        {
            var request = await ReadSyncRequestAsync(ctx);
            if (request is null)
                return Results.BadRequest(new { error = "Request body is required" });

            var validation = await validator.ValidateAsync(request, ctx.RequestAborted);
            if (!validation.IsValid)
                return Results.BadRequest(new { error = validation.Errors.First().ErrorMessage });
            if (request.Entities is null)
                return Results.BadRequest(new { error = "entities must be an object" });

            var auth = authAccessor.RequireAuthContext();

            var incomingEntities = new List<SyncEntity>();
            foreach (var (_, entitiesOfType) in request.Entities)
            foreach (var (_, entityDto) in entitiesOfType)
                incomingEntities.Add(EntityMapMapper.FromDto(entityDto));

            var result = await syncService.SyncAsync(
                request.SyncId, auth.UserId, request.ClientRevision, incomingEntities);

            return Results.Ok(new SyncResponseDto
            {
                SyncId = result.SyncId,
                ServerRevision = result.ServerRevision,
                Entities = EntityMapMapper.DeltaToDto(result.DeltaEntities),
                Accepted = result.Accepted,
                Rejected = result.Rejected.Select(r => new RejectedEntityDto
                {
                    ClientId = r.ClientId,
                    ServerRevision = r.ServerRevision,
                    Reason = r.Reason
                }).ToList()
            });
        });

        app.MapGet("/api/sync/limits", (IOptions<AppQuotaOptions> options) =>
        {
            var quota = options.Value;
            quota.Normalize();

            return Results.Ok(new
            {
                limits = new
                {
                    maxSavedPlants = quota.MaxSavedEntities,
                    maxPlantsPerWeek = quota.MaxEntitiesPerWeek,
                    maxWateringsPerDay = quota.MaxOperationsPerDay
                }
            });
        });
    }

    private static async Task<SyncRequestDto?> ReadSyncRequestAsync(HttpContext ctx)
    {
        // sendBeacon commonly uses text/plain; accept both text/plain and application/json.
        if (ctx.Request.ContentType?.Contains("text/plain", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var raw = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return JsonSerializer.Deserialize<SyncRequestDto>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true
            });
        }

        return await ctx.Request.ReadFromJsonAsync<SyncRequestDto>();
    }
}
