using System.IdentityModel.Tokens.Jwt;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    public static IEndpointRouteBuilder MapNyxIdChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("NyxIdChat");
        group.MapPost("/{scopeId}/nyxid-chat/conversations", HandleCreateConversationAsync);
        group.MapGet("/{scopeId}/nyxid-chat/conversations", HandleListConversationsAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:stream", HandleStreamMessageAsync);
        group.MapDelete("/{scopeId}/nyxid-chat/conversations/{actorId}", HandleDeleteConversationAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:approve", HandleApproveAsync);

        // NyxID Channel Bot Relay webhook — receives forwarded platform messages. NyxID drives
        // this callback; auth is carried via X-NyxID-User-Token rather than the JWT bearer we
        // validate in the fallback policy, so the route must stay anonymous. The health route
        // stays open for liveness checks, but the diag route remains behind host auth because it
        // can probe upstream Nyx connectivity with caller-supplied credentials.
        app.MapPost("/api/webhooks/nyxid-relay", HandleRelayWebhookAsync)
            .WithTags("NyxIdRelay")
            .AllowAnonymous();
        app.MapGet("/api/webhooks/nyxid-relay/health", () => Results.Json(new
        {
            status = "ok",
            endpoint = "/api/webhooks/nyxid-relay",
            last_check = DateTimeOffset.UtcNow,
        }))
            .WithTags("NyxIdRelay")
            .AllowAnonymous();

        // Diagnostic: deep connectivity check against NyxID gateway
        app.MapPost("/api/webhooks/nyxid-relay/diag", async (
            HttpContext http,
            [FromServices] NyxIdToolOptions nyxOptions,
            CancellationToken ct) =>
        {
            var token = http.Request.Headers["X-Test-Token"].FirstOrDefault()
                ?? http.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
            if (string.IsNullOrWhiteSpace(token))
                return Results.Json(new { error = "Provide token via X-Test-Token header" });

            var baseUrl = (nyxOptions.BaseUrl ?? "https://nyx-api.chrono-ai.fun").TrimEnd('/');
            var gateway = $"{baseUrl}/api/v1/llm/gateway/v1/chat/completions";
            var body = """{"model":"gpt-5.4","messages":[{"role":"user","content":"hi"}],"max_tokens":10}""";

            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.UserAgent.Clear();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, gateway);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            req.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);

            return Results.Json(new
            {
                status = (int)resp.StatusCode,
                statusText = resp.StatusCode.ToString(),
                responseBody = respBody.Length > 500 ? respBody[..500] : respBody,
            });
        })
            .WithTags("NyxIdRelay");

        // Access control for relay is handled by NyxID's route configuration.

        return app;
    }

    private static async Task<IResult> HandleCreateConversationAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorStore actorStore,
        CancellationToken ct)
    {
        // Conversation creation is fail-fast on IGAgentActorStore persistence.
        // NyxId chat depends on the registry being available; there is no
        // degraded mode where a conversation can run without being registered.
        var actorId = NyxIdChatServiceDefaults.GenerateActorId();
        await actorStore.AddActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, ct);
        return Results.Ok(new { actorId });
    }

    private static async Task<IResult> HandleListConversationsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorStore actorStore,
        CancellationToken ct)
    {
        try
        {
            var groups = await actorStore.GetAsync(scopeId, ct);
            var actorIds = groups
                .FirstOrDefault(g => string.Equals(g.GAgentType, NyxIdChatServiceDefaults.GAgentTypeName, StringComparison.Ordinal))
                ?.ActorIds
                ?? [];
            return Results.Ok(actorIds.Select(actorId => new { actorId }));
        }
        catch (InvalidOperationException)
        {
            return Results.Ok(Array.Empty<object>());
        }
    }

    private static async Task<IResult> HandleDeleteConversationAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromServices] IGAgentActorStore actorStore,
        [FromServices] IChatHistoryStore chatHistoryStore,
        CancellationToken ct)
    {
        await actorStore.RemoveActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, ct);
        try
        {
            await chatHistoryStore.DeleteConversationAsync(scopeId, actorId, ct);
        }
        catch
        {
            await TryRestoreConversationRegistrationAsync(http, scopeId, actorId, actorStore);
            throw;
        }

        return Results.Ok();
    }

    private static async Task TryRestoreConversationRegistrationAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        IGAgentActorStore actorStore)
    {
        try
        {
            await actorStore.AddActorAsync(scopeId, NyxIdChatServiceDefaults.GAgentTypeName, actorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("Aevatar.NyxId.Chat.DeleteConversation")
                .LogError(
                    ex,
                    "Failed to restore NyxId chat conversation registration after history deletion failure: scope={ScopeId}, actor={ActorId}",
                    scopeId,
                    actorId);
        }
    }

    private static async Task InjectUserConfigMetadataAsync(
        HttpContext http,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Aevatar.NyxId.Chat.UserConfig");

        var preferencesStore = http.RequestServices.GetService<INyxIdUserLlmPreferencesStore>();
        if (preferencesStore == null)
        {
            logger?.LogWarning("INyxIdUserLlmPreferencesStore not registered — skipping user config injection");
            return;
        }

        try
        {
            var preferences = await preferencesStore.GetAsync(ct);
            logger?.LogInformation(
                "User config loaded: model={Model}, route={Route}, maxToolRounds={MaxToolRounds}",
                preferences.DefaultModel ?? "<empty>",
                preferences.PreferredRoute ?? "<empty>",
                preferences.MaxToolRounds);

            if (!string.IsNullOrWhiteSpace(preferences.DefaultModel))
                metadata[LLMRequestMetadataKeys.ModelOverride] = preferences.DefaultModel.Trim();
            if (!string.IsNullOrWhiteSpace(preferences.PreferredRoute))
                metadata[LLMRequestMetadataKeys.NyxIdRoutePreference] = preferences.PreferredRoute.Trim();
            if (preferences.MaxToolRounds > 0)
                metadata[LLMRequestMetadataKeys.MaxToolRoundsOverride] = preferences.MaxToolRounds.ToString();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load user config from the projection read model; falling back to server defaults");
        }
    }

    private static async Task InjectUserMemoryAsync(
        HttpContext http,
        IDictionary<string, string> metadata,
        CancellationToken ct)
    {
        var memoryStore = http.RequestServices.GetService<IUserMemoryStore>();
        if (memoryStore == null)
            return;

        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Aevatar.NyxId.Chat.UserMemory");

        try
        {
            var section = await memoryStore.BuildPromptSectionAsync(2000, ct);
            if (!string.IsNullOrWhiteSpace(section))
                metadata[LLMRequestMetadataKeys.UserMemoryPrompt] = section;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load user memory from chrono-storage — continuing without memory context");
        }
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();

        return null;
    }

    /// <summary>
    /// Parse the JWT (without verification) to extract the 'sub' claim.
    /// Signature validation is handled earlier by the auth middleware / relay JWT
    /// validator; this helper only re-reads the already-accepted bearer token so the
    /// handler can recover the user scope id after header injection.
    /// </summary>
    private static string? TryExtractJwtSubject(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
                return null;

            var jwt = handler.ReadJwtToken(token);
            return jwt.Claims
                .FirstOrDefault(claim => string.Equals(claim.Type, "sub", StringComparison.Ordinal))
                ?.Value
                ?.Trim();
        }
        catch
        {
            return null;
        }
    }
}
