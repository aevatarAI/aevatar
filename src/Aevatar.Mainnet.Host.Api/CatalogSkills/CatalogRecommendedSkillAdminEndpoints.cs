using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.GAgentService.Abstractions.CatalogSkills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Mainnet.Host.Api.CatalogSkills;

internal static class CatalogRecommendedSkillAdminEndpoints
{
    public static IEndpointRouteBuilder MapCatalogRecommendedSkillAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/nyxid/catalog").WithTags("CatalogRecommendedSkills").RequireAuthorization();
        group.MapPost("/{catalogServiceId}/recommended-skills/{skillId}:update", UpdateAsync)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status409Conflict)
            .WithEndpointAudit("catalog-recommended-skill.update", AuditSensitivityLevel.Confidential,
                "catalog", EndpointAuditTargetResolvers.FromRouteValues("catalog", "catalogServiceId", "skillId"));
        return app;
    }

    internal static async Task<IResult> UpdateAsync(HttpContext http, string catalogServiceId, string skillId,
        CatalogSkillUpdateRequest? request, [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ICatalogRecommendedSkillUpdateApplicationService application, CancellationToken ct,
        [FromServices] ILoggerFactory? loggerFactory = null)
    {
        var operationId = Guid.NewGuid().ToString("D");
        var authorization = await AuthorizeAsync(http, authorizer, ct);
        loggerFactory?.CreateLogger("CatalogRecommendedSkillAdmin").LogInformation(
            "Catalog recommended skill update authorization: OperationId={OperationId} Stage=authorize Authorized={Authorized} AdministratorId={AdministratorId} CatalogServiceId={CatalogServiceId} SkillId={SkillId}",
            operationId, authorization.Error is null, authorization.Caller?.UserId, catalogServiceId, skillId);
        if (authorization.Error is not null) return authorization.Error;
        if (request is null)
            return Error(400, "invalid_request", "validate_version", operationId, "A request body is required.");
        var outcome = await application.UpdateAsync(new CatalogRecommendedSkillUpdateRequest
        {
            OperationId = operationId,
            AdministratorId = authorization.Caller!.UserId,
            CatalogServiceId = catalogServiceId,
            SkillId = skillId,
            ExpectedVersion = request.ExpectedVersion ?? "",
            NewVersion = request.NewVersion ?? "",
        }, new CatalogRecommendedSkillUpdateExecutionCredential(authorization.BearerToken!), ct);
        return OutcomeResult(outcome);
    }

    private static async Task<AdminAuthorization> AuthorizeAsync(
        HttpContext http, IPlatformAdminAuthorizer authorizer, CancellationToken ct)
    {
        if (http.User.Identity?.IsAuthenticated != true ||
            !AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
            return new(null, null, Results.Unauthorized());
        var caller = await authorizer.ResolveCallerAsync(header.Parameter, ct);
        return caller.IsElevated && !string.IsNullOrWhiteSpace(caller.UserId) &&
               caller.GrantSource == PlatformAdminGrantSources.AllowedUserId
            ? new(caller, header.Parameter, null)
            : new(null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private sealed record AdminAuthorization(PlatformCaller? Caller, string? BearerToken, IResult? Error);

    private static IResult OutcomeResult(CatalogRecommendedSkillUpdateOutcome outcome)
    {
        var publication = outcome.Publication;
        var reference = outcome.Reference;
        var verified = publication is { IsPublic: true, ConsumerReadable: true } &&
                       reference is { Confirmed: true } && reference.Version == publication.Version &&
                       reference.ManifestDigest == publication.ManifestDigest &&
                       publication.SkillId == outcome.Request.SkillId && publication.Version == outcome.Request.NewVersion &&
                       !string.IsNullOrWhiteSpace(publication.ManifestDigest);
        if (outcome.Status == CatalogRecommendedSkillUpdateStatus.Updated && !verified)
            return Error(502, "reference_invalid", "verify_reference", outcome.Request.OperationId,
                "The result does not contain a verified publication and catalog reference.",
                publicationConfirmed: publication is not null);
        var status = outcome.Status switch
        {
            CatalogRecommendedSkillUpdateStatus.Updated => 200,
            CatalogRecommendedSkillUpdateStatus.Failed => StatusCode(outcome.Failure?.Code ?? CatalogRecommendedSkillUpdateErrorCode.DependencyUnavailable),
            CatalogRecommendedSkillUpdateStatus.Uncertain => 409,
            _ => 502,
        };
        return Results.Json(new
        {
            status = Wire(outcome.Status), operationId = outcome.Request.OperationId,
            catalogServiceId = outcome.Request.CatalogServiceId,
            skillId = publication?.SkillId ?? outcome.Request.SkillId,
            previousVersion = outcome.Request.ExpectedVersion, version = publication?.Version,
            manifestDigest = publication?.ManifestDigest, stage = Wire(outcome.Stage),
            publicationConfirmed = publication is not null, catalogReferenceUpdated = verified,
            skillsRevision = reference?.SkillsRevision,
            error = outcome.Failure is null ? null : new
            {
                code = Wire(outcome.Failure.Code), message = outcome.Failure.SafeMessage,
                downstreamStatusCode = outcome.Failure.DownstreamStatusCode == 0 ? (int?)null : outcome.Failure.DownstreamStatusCode,
            },
        }, statusCode: status);
    }

    private static IResult Error(int status, string code, string stage, string? operationId, string message,
        bool publicationConfirmed = false) => Results.Json(new
        {
            status = "failed", operationId, stage, code, message,
            publicationConfirmed, catalogReferenceUpdated = false,
        }, statusCode: status);

    private static string Wire<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static int StatusCode(CatalogRecommendedSkillUpdateErrorCode code) => code switch
    {
        CatalogRecommendedSkillUpdateErrorCode.InvalidRequest or CatalogRecommendedSkillUpdateErrorCode.InvalidVersion => 400,
        CatalogRecommendedSkillUpdateErrorCode.Forbidden => 403,
        CatalogRecommendedSkillUpdateErrorCode.TargetNotFound => 404,
        CatalogRecommendedSkillUpdateErrorCode.TargetMismatch or
            CatalogRecommendedSkillUpdateErrorCode.ExpectedVersionConflict or CatalogRecommendedSkillUpdateErrorCode.VersionOccupied or
            CatalogRecommendedSkillUpdateErrorCode.ReferenceConflict or CatalogRecommendedSkillUpdateErrorCode.PublicationUncertain => 409,
        CatalogRecommendedSkillUpdateErrorCode.DependencyUnavailable => 503,
        _ => 502,
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CatalogSkillUpdateRequest(string ExpectedVersion, string NewVersion);
