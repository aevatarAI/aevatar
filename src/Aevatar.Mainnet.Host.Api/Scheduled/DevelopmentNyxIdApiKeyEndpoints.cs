using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Mainnet.Host.Api.Scheduled;

internal static class DevelopmentNyxIdApiKeyEndpoints
{
    internal const string ActiveUserServicesSectionName =
        "Aevatar:NyxId:DevelopmentActiveUserServices";

    public static WebApplication MapDevelopmentNyxIdApiKeyEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Environment.IsDevelopment() || AevatarScopeAccessGuard.IsAuthenticationEnabled(app.Services))
            return app;

        app.MapGet("/api/v1/api-keys", () => Results.Ok(new { keys = Array.Empty<object>() }))
            .WithTags("DevelopmentNyxId");
        app.MapGet("/api/v1/keys", HandleUserServiceKeys)
            .WithTags("DevelopmentNyxId");
        app.MapPost("/api/v1/api-keys/scope-plan", HandleScopePlanAsync)
            .WithTags("DevelopmentNyxId");
        app.MapPost("/api/v1/api-keys", HandleCreateApiKeyAsync)
            .WithTags("DevelopmentNyxId");
        app.MapDelete("/api/v1/api-keys/{apiKeyId}", () => Results.Ok(new { revoked = true }))
            .WithTags("DevelopmentNyxId");
        return app;
    }

    private static IResult HandleUserServiceKeys(
        HttpContext http,
        IConfiguration configuration)
    {
        if (!TryResolveDevelopmentSubject(http, out _))
            return Results.Json(Error(401, "unauthorized"), statusCode: StatusCodes.Status401Unauthorized);

        var configured = configuration
            .GetSection(ActiveUserServicesSectionName)
            .Get<DevelopmentNyxIdActiveUserService[]>() ?? [];
        if (!TryNormalizeActiveUserServices(configured, out var services))
        {
            return Results.Json(
                Error(500, "development_user_service_configuration_invalid"),
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new
        {
            keys = services.Select(static service => new
            {
                id = service.UserServiceId,
                slug = service.ServiceSlug,
                label = service.DisplayName,
                catalog_service_name = service.DisplayName,
                catalog_service_slug = service.ServiceSlug,
                status = "active",
                is_active = true,
                credential_source = new { type = "personal" },
                connected = true,
            }),
        });
    }

    private static IResult HandleScopePlanAsync(
        HttpContext http,
        ScopePlanRequest? request)
    {
        if (!TryResolveDevelopmentSubject(http, out var subject))
            return Results.Json(Error(401, "unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
        if (request is null)
            return Results.Json(Error(400, "bad_request"), statusCode: StatusCodes.Status400BadRequest);

        var selectedServiceIds = request.SelectedServiceIds ?? [];
        if (selectedServiceIds.Length > 0)
        {
            return Results.Json(
                Error(403, "api_key_scope_plan_route_unresolved"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var targetOrganizationId = NormalizeOptional(request.TargetOrganizationId);
        var owner = targetOrganizationId is null
            ? new { id = subject, type = "personal" }
            : new { id = targetOrganizationId, type = "organization" };

        return Results.Ok(new
        {
            authority = NyxIdApiAccessResponseParser.ScopePlanAuthority,
            contract_version = NyxIdApiAccessResponseParser.ScopePlanContractVersion,
            policy_version = NyxIdApiAccessResponseParser.ScopePlanPolicyVersion,
            authenticated_actor = new { id = subject, type = "personal" },
            intended_key_owner = owner,
            services = Array.Empty<object>(),
            allowed_service_ids = Array.Empty<string>(),
            allowed_node_ids = Array.Empty<string>(),
            evaluated_at = DateTimeOffset.UtcNow.ToString("O"),
            normalized_grant_digest = BuildScopePlanDigest(subject, targetOrganizationId),
            freshness = new
            {
                mode = "mutation_revalidated_snapshot",
                precondition_field = "scope_plan_digest",
                post_creation_drift = "fail_closed",
            },
            completeness = new
            {
                list_complete = true,
                no_duplicates = true,
                route_candidate_basis = "active_configured_routes",
                transient_node_state_excluded = true,
            },
        });
    }

    private static IResult HandleCreateApiKeyAsync(
        HttpContext http,
        CreateApiKeyRequest? request)
    {
        if (!TryResolveDevelopmentSubject(http, out var subject))
            return Results.Json(Error(401, "unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
        if (request is null || NormalizeOptional(request.Name) is not { } name)
            return Results.Json(Error(400, "bad_request"), statusCode: StatusCodes.Status400BadRequest);

        var keyId = "dev-scheduled-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(subject + "\n" + name)).AsSpan(0, 8));
        return Results.Ok(new
        {
            id = keyId,
            full_key = "dev-scheduled-secret-" + keyId,
        });
    }

    private static object Error(int status, string code) => new
    {
        error = true,
        status,
        body = "{\"error\":\"" + code + "\"}",
        message = code,
    };

    private static bool TryResolveDevelopmentSubject(HttpContext http, out string subject)
    {
        subject = string.Empty;
        var authorization = http.Request.Headers.Authorization.ToString().Trim();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        subject = authorization[prefix.Length..].Trim();
        return subject.Length > 0 && !subject.Contains(',');
    }

    private static string BuildScopePlanDigest(string subject, string? targetOrganizationId) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', "development-nyxid-api-key-scope-plan/v1", subject, targetOrganizationId ?? string.Empty))));

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool TryNormalizeActiveUserServices(
        IReadOnlyList<DevelopmentNyxIdActiveUserService> configured,
        out IReadOnlyList<DevelopmentNyxIdActiveUserService> services)
    {
        var normalized = new List<DevelopmentNyxIdActiveUserService>(configured.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in configured)
        {
            var userServiceId = service.UserServiceId?.Trim();
            var serviceSlug = service.ServiceSlug?.Trim();
            if (string.IsNullOrWhiteSpace(userServiceId) ||
                string.IsNullOrWhiteSpace(serviceSlug) ||
                !ids.Add(userServiceId))
            {
                services = [];
                return false;
            }

            normalized.Add(new DevelopmentNyxIdActiveUserService
            {
                UserServiceId = userServiceId,
                ServiceSlug = serviceSlug,
                DisplayName = NormalizeOptional(service.DisplayName) ?? serviceSlug,
            });
        }

        services = normalized;
        return true;
    }

    private sealed record ScopePlanRequest(
        [property: JsonPropertyName("selected_service_ids")] string[]? SelectedServiceIds,
        [property: JsonPropertyName("target_org_id")] string? TargetOrganizationId);

    private sealed record CreateApiKeyRequest(
        [property: JsonPropertyName("name")] string? Name);
}

internal sealed class DevelopmentNyxIdActiveUserService
{
    public string? UserServiceId { get; init; }

    public string? ServiceSlug { get; init; }

    public string? DisplayName { get; init; }
}
