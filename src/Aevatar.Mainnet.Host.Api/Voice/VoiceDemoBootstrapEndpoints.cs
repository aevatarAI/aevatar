using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.Voice;

// Refactor (iter34/cluster-004-voice-bootstrap-application-port):
//   Old pattern: Voice bootstrap endpoint(VoiceDemoBootstrapEndpoints)同步等待 actor readiness/observation loop;POST 返回前阻塞读取 actor 状态;route-policy mutation 在 Host 内做。
//   New principle: Medium-B framing(reflector force-pick): 删除 POST readiness polling;移 voice bootstrap + voice-demo route mutation 到 Application/actor-owned typed command port;无新 bootstrap actor / 新 envelope / 新 projection phase / mandatory status endpoint / shared route-policy command port(留给可能后续 cluster)。POST 返回 honest accepted receipt + stable id;readiness 由 client 显式 readmodel query 获取(或事件 notification,无需 Host 内同步等)。
internal static class VoiceDemoBootstrapEndpoints
{
    public static IEndpointRouteBuilder MapVoiceDemoBootstrapEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/demo/voice/bootstrap", HandleBootstrapAsync)
            .WithTags("VoiceDemo");

        return app;
    }

    private static async Task<IResult> HandleBootstrapAsync(
        HttpContext http,
        [FromServices] VoiceDemoAgentCommandPort commandPort,
        CancellationToken ct)
    {
        // Refactor (iter34/cluster-004-voice-bootstrap-application-port):
        //   Old pattern: Voice bootstrap endpoint(VoiceDemoBootstrapEndpoints)同步等待 actor readiness/observation loop;POST 返回前阻塞读取 actor 状态;route-policy mutation 在 Host 内做。
        //   New principle: Medium-B framing(reflector force-pick): 删除 POST readiness polling;移 voice bootstrap + voice-demo route mutation 到 Application/actor-owned typed command port;无新 bootstrap actor / 新 envelope / 新 projection phase / mandatory status endpoint / shared route-policy command port(留给可能后续 cluster)。POST 返回 honest accepted receipt + stable id;readiness 由 client 显式 readmodel query 获取(或事件 notification,无需 Host 内同步等)。
        if (!TryResolveScopeId(http.User, out var scopeId))
        {
            return Results.Json(
                new { error = "scope_missing", detail = "Authenticated NyxID scope_id claim is required." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var receipt = await commandPort.AcceptBootstrapAsync(new VoiceDemoBootstrapCommand(scopeId), ct);
        return Results.Accepted(value: new
        {
            status = "accepted",
            actor_id = receipt.ActorId,
            route_policy_actor_id = receipt.RoutePolicyActorId,
            voice_module_name = receipt.VoiceModuleName,
            policy_rule_id = receipt.PolicyRuleId,
            run_id = receipt.RunId,
            correlation_id = receipt.CorrelationId,
            agent_command_id = receipt.AgentCommandId,
            route_policy_command_id = receipt.RoutePolicyCommandId,
            nyxid_proxy = "https://nyx.chrono-ai.fun/api/v1/proxy/s/llm-openai",
            readiness = "query readmodels or subscribe to events; this POST only confirms dispatch acceptance",
        });
    }

    private static bool TryResolveScopeId(ClaimsPrincipal user, out string scopeId)
    {
        scopeId = FirstNonEmpty(
            user.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value,
            user.FindFirst("uid")?.Value,
            user.FindFirst("sub")?.Value,
            user.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? string.Empty;

        return !string.IsNullOrWhiteSpace(scopeId);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = value?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return null;
    }
}

// Refactor helper, no behavior change: typed request for the voice-demo command port.
internal sealed record VoiceDemoBootstrapCommand(string ScopeId);

// Refactor helper, no behavior change: typed accepted receipt returned by the voice-demo command port.
internal sealed record VoiceDemoBootstrapReceipt(
    string ActorId,
    string RoutePolicyActorId,
    string VoiceModuleName,
    string PolicyRuleId,
    string RunId,
    string CorrelationId,
    string AgentCommandId,
    string RoutePolicyCommandId);
