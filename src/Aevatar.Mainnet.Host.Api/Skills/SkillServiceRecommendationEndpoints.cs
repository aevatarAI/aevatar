using Aevatar.AI.Abstractions.Skills;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Skills;

internal static class SkillServiceRecommendationEndpoints
{
    public static IEndpointRouteBuilder MapSkillServiceRecommendations(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/skills/service-recommendations", GetRecommendations)
            .WithTags("Skills")
            .WithName("GetSkillServiceRecommendations")
            .WithSummary("Discover advisory services for a caller-visible Skill without changing authorization.")
            .RequireAuthorization();
        return app;
    }

    internal static async Task<IResult> GetRecommendations(
        HttpContext http,
        [FromServices] ISkillServiceRecommendationService service,
        string skillName,
        CancellationToken ct = default)
    {
        var authorization = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization[prefix.Length..]))
            return Results.Unauthorized();
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await service.RecommendAsync(authorization[prefix.Length..].Trim(), skillName, ct);
            return Results.Json(new SkillServiceRecommendationsResponse(
                result.SkillName,
                result.Suggestions.Select(item => new SkillServiceSuggestionResponse(
                    item.Slug, item.Label,
                    item.Evidence switch
                    {
                        SkillServiceEvidence.Linked => "linked",
                        SkillServiceEvidence.Catalog => "catalog",
                        SkillServiceEvidence.Mention => "mention",
                        _ => throw new SkillServiceDiscoveryException(),
                    },
                    item.Instances.Select(instance => new SkillServiceInstanceResponse(
                        instance.UserServiceId, instance.Slug, instance.Label,
                        instance.Active, instance.AccountAccessAllowed,
                        instance.CredentialSource switch
                        {
                            SkillServiceCredentialSource.Personal => "personal",
                            SkillServiceCredentialSource.Organization => "organization",
                            _ => "unknown",
                        },
                        instance.OrganizationName)).ToArray())).ToArray()));
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new SkillServiceRecommendationError("invalid_skill_name"));
        }
        catch (SkillServiceDiscoveryException)
        {
            return Results.Json(new SkillServiceRecommendationError("skill_service_discovery_unavailable"),
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}

public sealed record SkillServiceRecommendationsResponse(
    string SkillName, IReadOnlyList<SkillServiceSuggestionResponse> Suggestions);
public sealed record SkillServiceSuggestionResponse(
    string Slug, string Label, string Evidence, IReadOnlyList<SkillServiceInstanceResponse> Instances);
public sealed record SkillServiceInstanceResponse(
    string Id, string Slug, string Label, bool Active, bool Allowed, string Source, string OrganizationName);
public sealed record SkillServiceRecommendationError(string Code);
