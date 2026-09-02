using System.IdentityModel.Tokens.Jwt;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Capabilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private const string NyxIdDelegationTokenHeader = "X-NyxID-Delegation-Token";

    public static IEndpointRouteBuilder MapNyxIdChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scopes").WithTags("NyxIdChat");
        group.MapPost("/{scopeId}/nyxid-chat/conversations", HandleCreateConversationAsync);
        group.MapGet("/{scopeId}/nyxid-chat/conversations", HandleListConversationsAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:stream", HandleStreamMessageAsync);
        group.MapGet("/{scopeId}/nyxid-chat/conversations/{actorId}/state", HandleGetStateAsync);
        group.MapDelete("/{scopeId}/nyxid-chat/conversations/{actorId}", HandleDeleteConversationAsync);
        group.MapPost("/{scopeId}/nyxid-chat/conversations/{actorId}:approve", HandleApproveAsync);
        MapControlEndpoints(group);

        // NyxID Channel Bot Relay webhook — receives forwarded platform messages. NyxID drives
        // this callback and authenticates it with the dedicated X-NyxID-Callback-Token JWT, so
        // the route must stay anonymous to the normal bearer policy. The diag + health routes
        // under the same prefix are operator probes that also must stay open.
        app.MapPost("/api/webhooks/nyxid-relay", HandleRelayWebhookAsync)
            .WithTags("NyxIdRelay")
            .WithEndpointAudit(
                "channel.relay.inbound",
                AuditSensitivityLevel.Confidential,
                "channel_relay",
                EndpointAuditTargetResolvers.Static("channel_relay", "inbound"),
                captureUnauthenticated: true)
            .AllowAnonymous();
        app.MapGet("/api/webhooks/nyxid-relay/health", () => Results.Json(new
        {
            status = "ok",
            endpoint = "/api/webhooks/nyxid-relay",
            last_check = DateTimeOffset.UtcNow,
        }))
            .WithTags("NyxIdRelay")
            .AllowAnonymous();

        // Diagnostic: deep connectivity check against NyxID gateway.
        //
        // SECURITY (M4): this route is a token-relay oracle — it takes an
        // arbitrary caller-supplied X-Test-Token, forwards it as a Bearer to
        // the NyxID LLM gateway, and echoes up to 500 chars of the response.
        // That lets anyone who can reach it probe whether an arbitrary token is
        // a valid NyxID credential (and read the gateway's reply). Gating it on
        // IPlatformAdminAuthorizer is a poor fit: the endpoint's purpose is to
        // test a token the operator supplies, which is not necessarily the
        // caller's own admin bearer, so an admin gate does not remove the oracle
        // — it just moves the trust boundary. The cleaner hardening is to
        // compile the oracle out of production entirely: it is only mapped when
        // the host runs in the Development environment, so a mainnet deployment
        // has no diag route at all. Operators keep the local dev probe.
        if (app.ServiceProvider.GetService<IHostEnvironment>()?.IsDevelopment() == true)
        {
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
        }

        // Access control for relay is handled by NyxID's route configuration.

        return app;
    }

    private static async Task<IResult> HandleCreateConversationAsync(
        HttpContext http,
        string scopeId,
        [FromServices] NyxIdChatLifecycleFacade lifecycleFacade,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        // Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
        //   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and chat history command-port side effects.
        //   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
        // Refactor (iter56/cluster-891-endpoint-ack-honesty): old=200-shaped accepted, new=202 + Location
        //   The create facade returns accepted/admission-visible command trace, not read-model-observed conversation state.
        //   Clients must poll the conversation list or observe the stream/status path instead of treating this body as committed.
        var receipt = await lifecycleFacade.CreateConversationAsync(scopeId, ct);
        return receipt.Status switch
        {
            NyxIdChatConversationCreateStatus.Accepted => Results.Accepted(
                $"/api/scopes/{Uri.EscapeDataString(scopeId)}/nyxid-chat/conversations",
                new
                {
                    status = "accepted",
                    actorId = receipt.ActorId,
                    acceptedCommandId = receipt.CommandId,
                    correlationId = receipt.CorrelationId,
                    statusUrl = $"/api/scopes/{Uri.EscapeDataString(scopeId)}/nyxid-chat/conversations",
                }),
            NyxIdChatConversationCreateStatus.RouteRejected => ChatRouteRejected(receipt.Reject),
            NyxIdChatConversationCreateStatus.RegistrationUnavailable => Results.Json(
                new { error = "Conversation registration is not admission-visible" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(
                new { error = "Conversation creation failed" },
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static IResult ChatRouteRejected(Reject? reject) =>
        Results.Json(
            new
            {
                error = "chat_route_rejected",
                detail = string.IsNullOrWhiteSpace(reject?.Reason)
                    ? "The chat route policy rejected this request."
                    : reject.Reason,
            },
            statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> HandleListConversationsAsync(
        HttpContext http,
        string scopeId,
        [FromServices] IGAgentActorRegistryQueryPort registryQueryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        var snapshot = await registryQueryPort.ListActorsAsync(scopeId, ct);
        var actorIds = snapshot.Groups
            .FirstOrDefault(g => string.Equals(g.AgentKind, NyxIdChatServiceDefaults.GAgentKind, StringComparison.Ordinal))
            ?.ActorIds
            ?? [];
        return Results.Ok(new
        {
            snapshot.ScopeId,
            snapshot.StateVersion,
            snapshot.UpdatedAt,
            snapshot.ObservedAt,
            Conversations = actorIds.Select(actorId => new { actorId }),
        });
    }

    private static async Task<IResult> HandleDeleteConversationAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromServices] NyxIdChatLifecycleFacade lifecycleFacade,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        // Refactor (iter47/issue-877-chat-endpoints-own-lifecycle-and-compensation):
        //   Old pattern: Chat endpoints owned actor lifecycle, registry compensation, participant orchestration, terminal-state recovery, and chat history command-port side effects.
        //   New principle: Endpoint is adapter-only (HTTP/SSE); typed command facade owns lifecycle; existing chat actors own compensation events and terminal-state publication.
        var receipt = await lifecycleFacade.DeleteConversationAsync(scopeId, actorId, ct);
        return receipt.Status switch
        {
            NyxIdChatConversationDeleteStatus.Accepted => Results.Ok(),
            NyxIdChatConversationDeleteStatus.NotFound => Results.NotFound(new { error = "Conversation not found" }),
            NyxIdChatConversationDeleteStatus.AccessDenied => Results.Json(
                new { error = "Conversation access denied" },
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(
                new { error = "Conversation admission unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task<IResult?> AuthorizeConversationAsync(
        IScopeResourceAdmissionPort admissionPort,
        string scopeId,
        string actorId,
        ScopeResourceOperation operation,
        CancellationToken ct)
    {
        var admission = await admissionPort.AuthorizeTargetAsync(
            new ScopeResourceTarget(
                scopeId,
                ScopeResourceKind.GAgentActor,
                NyxIdChatServiceDefaults.GAgentKind,
                actorId,
                operation),
            ct);
        return admission.Status switch
        {
            ScopeResourceAdmissionStatus.Allowed => null,
            ScopeResourceAdmissionStatus.NotFound => Results.NotFound(new { error = "Conversation not found" }),
            ScopeResourceAdmissionStatus.Denied or ScopeResourceAdmissionStatus.ScopeMismatch =>
                Results.Json(new { error = "Conversation access denied" }, statusCode: StatusCodes.Status403Forbidden),
            ScopeResourceAdmissionStatus.Unavailable =>
                Results.Json(new { error = "Conversation admission unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(new { error = "Conversation admission failed" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task<bool> TryAuthorizeConversationAsync(
        HttpContext http,
        IScopeResourceAdmissionPort admissionPort,
        string scopeId,
        string actorId,
        ScopeResourceOperation operation,
        CancellationToken ct)
    {
        var admissionError = await AuthorizeConversationAsync(admissionPort, scopeId, actorId, operation, ct);
        if (admissionError == null)
            return true;

        http.Response.StatusCode = admissionError is IStatusCodeHttpResult { StatusCode: { } statusCode }
            ? statusCode
            : StatusCodes.Status500InternalServerError;
        return false;
    }

    private static async Task<LLMControlContext> BuildLlmControlAsync(
        HttpContext http,
        string accessToken,
        CancellationToken ct)
    {
        var control = new LLMControlContext(
            NyxIdAccessToken: accessToken,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: null,
            NyxIdRoutePreference: null,
            MaxToolRoundsOverride: null,
            UserMemoryPrompt: null);

        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Aevatar.NyxId.Chat.UserConfig");

        var preferencesStore = http.RequestServices.GetService<INyxIdUserLlmPreferencesStore>();
        if (preferencesStore != null)
        {
            try
            {
                // Studio chat endpoint always uses the ambient (bot owner) scope —
                // the channel inbound path passes the sender binding-id explicitly.
                var preferences = await preferencesStore.GetOwnerAsync(ct);
                control = preferences.ApplyTo(control);
                logger?.LogInformation(
                    "User LLM selection loaded: status={Status}, maxToolRounds={MaxToolRounds}",
                    preferences.Status,
                    preferences.MaxToolRounds);
            }
            catch (LLMSelectionRepairRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to load user config from the projection read model; falling back to server defaults");
            }
        }

        var promptContextProvider = http.RequestServices.GetService<IUserMemoryPromptContextProvider>();
        if (promptContextProvider == null)
            return control;

        var section = await promptContextProvider.BuildAsync(2000, ct);
        if (!string.IsNullOrWhiteSpace(section))
            control = control with { UserMemoryPrompt = section };

        return control;
    }

    private static string? ExtractNyxIdAccessToken(HttpContext http) =>
        ExtractNyxIdCredentials(http)?.NyxIdAccessToken;

    private static AgentToolCredentials? ExtractNyxIdCredentials(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            if (authorizationValues.Count != 1)
                return null;

            var authorization = authorizationValues[0]?.Trim();
            if (string.IsNullOrWhiteSpace(authorization) ||
                !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bearerToken = authorization["Bearer ".Length..].Trim();
            return string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Any(char.IsWhiteSpace)
                ? null
                : new AgentToolCredentials(
                    bearerToken,
                    null,
                    null,
                    AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        }

        if (http.Request.Headers.TryGetValue(NyxIdDelegationTokenHeader, out var delegationValues))
        {
            if (delegationValues.Count != 1)
                return null;

            var delegationToken = delegationValues[0]?.Trim();
            return string.IsNullOrWhiteSpace(delegationToken) || delegationToken.Any(char.IsWhiteSpace)
                ? null
                : new AgentToolCredentials(
                    delegationToken,
                    null,
                    null,
                    AgentToolNyxIdCredentialKind.ProxyDelegation);
        }

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
        catch (ArgumentException)
        {
            // The bearer was already accepted by auth middleware; a parse failure here is a
            // malformed-but-authenticated token. Fail soft (no subject) rather than 500.
            return null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
    }
}
