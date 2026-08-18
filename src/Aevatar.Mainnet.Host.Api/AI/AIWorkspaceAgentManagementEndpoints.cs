using System.Text.Json.Serialization;
using Aevatar.Authentication.Abstractions;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Application.AgentProfiles;
using Aevatar.Mainnet.Host.Api.AgentProfiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.AI;

/// <summary>
/// Caller-scope facade for Agent Profile management used by the AI workspace.
/// The Agent Profile application service remains the sole command/query boundary;
/// these routes only adapt the URL and scope identity.
/// </summary>
internal static class AIWorkspaceAgentManagementEndpoints
{
    public static IEndpointRouteBuilder MapAIWorkspaceAgentManagementEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var agents = app.MapGroup("/api/ai/agents")
            .WithTags("AIWorkspaceAgents")
            .RequireAuthorization();

        // Map the collection command on the canonical no-trailing-slash path so it
        // remains distinct from the existing GET /api/ai/agents read model route.
        var create = app.MapPost("/api/ai/agents", CreateAsync)
            .WithTags("AIWorkspaceAgents")
            .RequireAuthorization();
        var editorOptions = agents.MapGet("/editor-options", GetEditorOptions);
        var detail = agents.MapGet("/{profileSlug}", GetDetailAsync);
        var draft = agents.MapPut("/{profileSlug}/draft", UpdateDraftAsync);
        var validate = agents.MapPost("/{profileSlug}:validate", ValidateAsync);
        var publish = agents.MapPost("/{profileSlug}:publish", PublishAsync);
        var binding = agents.MapGet("/default/{agentKind}", GetBindingAsync);
        var setBinding = agents.MapPut("/default/{agentKind}", SetBindingAsync);
        var clearBinding = agents.MapDelete("/default/{agentKind}", ClearBindingAsync);

        Audit(create, "create", targetKind: "ai-agent-collection");
        Audit(editorOptions, "editor-options", targetKind: "ai-agent-editor-options");
        Audit(detail, "get", "profileSlug");
        Audit(draft, "update-draft", "profileSlug");
        Audit(validate, "validate", "profileSlug");
        Audit(publish, "publish", "profileSlug");
        Audit(binding, "get-default", "agentKind", "ai-agent-default-binding");
        Audit(setBinding, "set-default", "agentKind", "ai-agent-default-binding");
        Audit(clearBinding, "clear-default", "agentKind", "ai-agent-default-binding");
        return app;
    }

    private static Task<IResult> CreateAsync(
        HttpContext http,
        AgentProfileEndpoints.AgentProfileCreateInput? input,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        if (!TryAuditSubject(http, out var subject))
            return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));

        return AgentProfileEndpoints.CreateAsync(
            http,
            service,
            owner,
            input?.ProfileSlug,
            input?.IdempotencyKey,
            subject,
            slug => $"/api/ai/agents/{Uri.EscapeDataString(slug)}",
            ct,
            includeActorId: false);
    }

    private static IResult GetEditorOptions(HttpContext http)
    {
        if (!TryGetCallerOwner(http, out _, out var denied))
            return denied!;
        return AgentProfileEndpoints.GetEditorOptions();
    }

    private static Task<IResult> GetDetailAsync(
        HttpContext http,
        string profileSlug,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        return AgentProfileEndpoints.GetDetailAsync(service, owner, profileSlug, ct);
    }

    private static Task<IResult> UpdateDraftAsync(
        HttpContext http,
        string profileSlug,
        AgentProfileEndpoints.AgentProfileDraftUpdateInput? input,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        if (!TryAuditSubject(http, out var subject))
            return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));

        return AgentProfileEndpoints.UpdateDraftAsync(
            http,
            service,
            owner,
            profileSlug,
            input,
            subject,
            $"/api/ai/agents/{Uri.EscapeDataString(profileSlug)}",
            ct,
            includeActorId: false);
    }

    private static Task<IResult> ValidateAsync(
        HttpContext http,
        string profileSlug,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        return AgentProfileEndpoints.ValidateAsync(service, owner, profileSlug, BearerToken(http), ct);
    }

    private static Task<IResult> PublishAsync(
        HttpContext http,
        string profileSlug,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        if (!TryAuditSubject(http, out var subject))
            return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));

        return AgentProfileEndpoints.PublishAsync(
            http,
            service,
            owner,
            profileSlug,
            subject,
            BearerToken(http),
            $"/api/ai/agents/{Uri.EscapeDataString(profileSlug)}",
            ct,
            includeActorId: false);
    }

    private static Task<IResult> GetBindingAsync(
        HttpContext http,
        string agentKind,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        return AgentProfileEndpoints.GetBindingAsync(
            service,
            owner,
            agentKind,
            ct,
            includeSystemRollout: false);
    }

    private static Task<IResult> SetBindingAsync(
        HttpContext http,
        string agentKind,
        AIWorkspaceAgentBindingInput? input,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        if (!TryAuditSubject(http, out var subject))
            return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));

        return AgentProfileEndpoints.SetBindingAsync(
            http,
            service,
            owner,
            agentKind,
            input is null
                ? null
                : new AgentProfileEndpoints.AgentProfileBindingInput(
                    input.AgentProfile is null
                        ? null
                        : new AgentProfileEndpoints.AgentProfileReferenceInput(
                            input.AgentProfile.OwnerKind,
                            input.AgentProfile.ProfileSlug),
                    Enabled: null,
                    CohortBasisPoints: null,
                    input.ExpectedVersion,
                    input.IdempotencyKey),
            subject,
            $"/api/ai/agents/default/{Uri.EscapeDataString(agentKind)}",
            ct,
            includeActorId: false);
    }

    private static Task<IResult> ClearBindingAsync(
        HttpContext http,
        string agentKind,
        [FromServices] AgentProfileApplicationService service,
        CancellationToken ct)
    {
        if (!TryGetCallerOwner(http, out var owner, out var denied))
            return Task.FromResult(denied!);
        if (!TryAuditSubject(http, out var subject))
            return Task.FromResult(Error(StatusCodes.Status403Forbidden, "AUDIT_SUBJECT_REQUIRED", "Authenticated caller subject is required."));

        return AgentProfileEndpoints.ClearBindingAsync(
            http,
            service,
            owner,
            agentKind,
            subject,
            $"/api/ai/agents/default/{Uri.EscapeDataString(agentKind)}",
            ct,
            includeActorId: false);
    }

    private static bool TryGetCallerOwner(HttpContext http, out AgentProfileOwner owner, out IResult? denied)
    {
        if (AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
        {
            owner = AgentProfileOwners.ForScope(scopeId);
            denied = null;
            return true;
        }

        owner = new AgentProfileOwner();
        denied = AIWorkspaceEndpoints.Error(
            http.User.Identity?.IsAuthenticated == true
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized,
            "AI_SCOPE_REQUIRED",
            "A single authenticated scope is required.");
        return false;
    }

    private static bool TryAuditSubject(HttpContext http, out string subject)
    {
        subject = http.User.FindFirst("uid")?.Value?.Trim()
                  ?? http.User.FindFirst("sub")?.Value?.Trim()
                  ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value?.Trim()
                  ?? http.User.FindFirst("user_id")?.Value?.Trim()
                  ?? string.Empty;
        return subject.Length > 0;
    }

    private static string BearerToken(HttpContext http)
    {
        var value = http.Request.Headers.Authorization.ToString().Trim();
        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..].Trim()
            : string.Empty;
    }

    private static IResult Error(int statusCode, string code, string message) =>
        AIWorkspaceEndpoints.Error(statusCode, code, message);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AIWorkspaceAgentBindingInput(
        AIWorkspaceAgentReferenceInput? AgentProfile,
        long? ExpectedVersion,
        string? IdempotencyKey);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AIWorkspaceAgentReferenceInput(
        string? OwnerKind,
        string? ProfileSlug);

    private static void Audit(
        RouteHandlerBuilder builder,
        string operation,
        string? routeValueName = null,
        string targetKind = "ai-agent") =>
        builder.WithEndpointAudit(
            $"ai-workspace.agents.{operation}",
            AuditSensitivityLevel.Confidential,
            "ai-workspace-agents",
            routeValueName is null
                ? http => ValueTask.FromResult<EndpointAuditTarget?>(
                    new EndpointAuditTarget(
                        targetKind,
                        AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId)
                            ? EndpointAuditSanitizers.SanitizeValue(scopeId)
                            : string.Empty))
                : EndpointAuditTargetResolvers.FromRouteValue(targetKind, routeValueName));
}
