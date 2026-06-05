using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class ScopedWorkflowToolContextFactory
{
    public static AgentToolExecutionContext? Build(
        HttpContext? http,
        string? scopeId,
        string? sessionId,
        IReadOnlyDictionary<string, string>? headers,
        LLMControlContext? llmControl)
    {
        if (http == null)
            return null;

        var normalizedScopeId = Normalize(scopeId);
        var bearerToken = ExtractBearerToken(http);
        var model = Normalize(llmControl?.ModelOverride);
        var route = Normalize(llmControl?.NyxIdRoutePreference);

        var effectiveRequestId = Normalize(sessionId);
        return AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(effectiveRequestId, null),
            Credentials = new AgentToolCredentials(bearerToken, bearerToken, null),
            Caller = new AgentToolCallerContext(
                normalizedScopeId,
                ResolveOwnerSubject(http),
                Normalize(sessionId) ?? effectiveRequestId),
            Channel = new AgentToolChannelContext(
                "scope-workflow",
                null,
                normalizedScopeId,
                null,
                null),
            Routing = new LLMRequestRoutingContext(model, route, null, null),
            ExternalMetadata = AgentToolExecutionContextMapper.StripOwnedControlKeys(headers),
        };
    }

    private static string? ResolveOwnerSubject(HttpContext http) =>
        Normalize(
            http.User.FindFirst("uid")?.Value ??
            http.User.FindFirst("sub")?.Value ??
            http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);

    private static string? ExtractBearerToken(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.FirstOrDefault();
        if (auth == null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var bearerToken = auth["Bearer ".Length..].Trim();
        return string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
