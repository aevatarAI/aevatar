using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.Hosting;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.Voice;

public static class PolicyAwareVoiceEndpoints
{
    private const string DefaultPattern = "/ws/voice";
    private const string WhipOfferPattern = "/whip/offer";
    internal const string VoiceNotConfiguredReason = "voice_not_configured";
    private const string VoiceCredentialUnavailableReason = "voice_credential_unavailable";
    internal const string VoiceToolCatalogUnavailableReason = "voice_tool_catalog_unavailable";

    public static IEndpointConventionBuilder MapPolicyAwareVoiceEndpoint(this IEndpointRouteBuilder app) =>
        app.Map(DefaultPattern, HandlePolicyAwareVoiceAsync);

    public static IEndpointConventionBuilder MapPolicyAwareVoiceWhipEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost(WhipOfferPattern, HandlePolicyAwareVoiceWhipAsync);

    /// <summary>
    /// True when the voice feature registered its realtime session services.
    /// Voice registration is conditional (no provider configured → skipped),
    /// so the host must not map handlers whose [FromServices] dependencies
    /// would crash request-time DI resolution (issue #2023).
    /// </summary>
    public static bool IsVoiceRealtimeConfigured(IServiceProvider services) =>
        services.GetService<IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>() is not null;

    /// <summary>
    /// Fail-closed stand-ins for deployments without a configured voice
    /// provider: the Mainnet voice ingress answers 503 voice_not_configured
    /// instead of throwing an unhandled DI exception.
    /// </summary>
    public static IEndpointRouteBuilder MapVoiceNotConfiguredEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(DefaultPattern, HandleVoiceNotConfiguredAsync);
        app.Map(DefaultPattern + "/{actorId}", HandleVoiceNotConfiguredAsync);
        app.MapPost(WhipOfferPattern, HandleVoiceNotConfiguredAsync);
        return app;
    }

    private static async Task HandleVoiceNotConfiguredAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await http.Response.WriteAsync(VoiceNotConfiguredReason, http.RequestAborted);
    }

    // Implement (issue #695):
    //   Behavior: resolve /ws/voice target through ChatRoutePolicy before WebSocket upgrade.
    //   Why this shape: the host boundary composes routing, authorization, and voice attach without
    //   pulling ChatRouting back into Foundation.VoicePresence.
    private static async Task HandlePolicyAwareVoiceAsync(
        HttpContext http,
        [FromServices] IChatRoutePolicyQueryPort queryPort,
        [FromServices] IChatRoutePolicyProjectionRecoveryPort recoveryPort,
        [FromServices] ChatRouteResolver resolver,
        [FromServices] IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion> voiceRealtimeSession,
        [FromServices] IVoiceVolatileMediaStreamPort mediaStreamPort,
        [FromServices] VoiceWebSocketAttachExecutor attachExecutor,
        [FromServices] IOptions<VoiceWebSocketAttachOptions> attachOptions)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("WebSocket required.", http.RequestAborted);
            return;
        }

        var voiceTarget = await ResolveVoiceTargetWithLegacyRecoveryAsync(http, queryPort, recoveryPort, resolver);
        if (!voiceTarget.Succeeded)
            return;

        await AttachVoiceTargetAsync(
            http,
            voiceRealtimeSession,
            mediaStreamPort,
            attachExecutor,
            attachOptions.Value,
            voiceTarget.Target);
    }

    private static async Task HandlePolicyAwareVoiceWhipAsync(
        HttpContext http,
        [FromServices] IChatRoutePolicyQueryPort queryPort,
        [FromServices] ChatRouteResolver resolver,
        [FromServices] IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion> voiceRealtimeSession,
        [FromServices] VoiceWhipAttachExecutor whipAttachExecutor,
        [FromServices] IOptions<VoiceWebSocketAttachOptions> attachOptions)
    {
        var sessionId = NormalizeOptional(http.Request.Query["sessionId"].ToString());
        if (sessionId is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("sessionId is required.", http.RequestAborted);
            return;
        }

        var offerSdp = await ReadSdpBodyAsync(http.Request);
        if (string.IsNullOrWhiteSpace(offerSdp))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("SDP offer is required.", http.RequestAborted);
            return;
        }

        var voiceTarget = await ResolveVoiceTargetFromReadModelAsync(http, queryPort, resolver);
        if (!voiceTarget.Succeeded)
            return;

        var toolContextAdmission = await TryBuildToolContextAsync(http);
        if (!toolContextAdmission.Accepted)
            return;

        try
        {
            var result = await voiceRealtimeSession.ExecuteAsync(
                new VoiceRealtimeSessionRequest(
                    voiceTarget.Target.ActorId.Trim(),
                    NormalizeOptional(voiceTarget.Target.VoiceModuleName),
                    VoiceRealtimeSessionPurpose.Attach,
                    voiceTarget.Target.SessionOverrides?.Clone(),
                    toolContextAdmission.ToolContext?.Clone()),
                static (_, _) => ValueTask.CompletedTask,
                ct: http.RequestAborted);

            var accepted = await VoiceWebSocketAttachExecutor.WriteNonAcceptedResolutionAsync(http, result, attachOptions.Value);
            if (accepted is null)
            {
                await ReleasePendingToolCredentialAsync(http, toolContextAdmission.ToolContext);
                return;
            }

            var attached = await whipAttachExecutor.AttachAsync(
                http,
                accepted,
                offerSdp,
                BuildWhipResourceLocation(sessionId),
                toolContextAdmission.TransportBinding,
                http.RequestAborted);

            http.Response.StatusCode = StatusCodes.Status201Created;
            http.Response.ContentType = "application/sdp";
            http.Response.Headers.Location = attached.ResourceLocation;
            await http.Response.WriteAsync(attached.AnswerSdp, http.RequestAborted);
        }
        catch (VoiceVolatileMediaStreamUnavailableException)
        {
            await ReleasePendingToolCredentialAsync(http, toolContextAdmission.ToolContext);
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync(VoiceVolatileMediaStreamUnavailableException.Reason, http.RequestAborted);
        }
        catch (VoiceVolatileToolCredentialUnavailableException)
        {
            await ReleasePendingToolCredentialAsync(http, toolContextAdmission.ToolContext);
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync(VoiceWebSocketAttachExecutor.VoiceCredentialUnavailableReason, http.RequestAborted);
        }
        catch (RealtimeProviderCredentialException ex)
        {
            await ReleasePendingToolCredentialAsync(http, toolContextAdmission.ToolContext);
            GetLogger(http).LogWarning(ex, "Voice WHIP provider credential resolution failed.");
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync(
                VoiceWebSocketAttachExecutor.VoiceProviderCredentialUnavailableReason,
                http.RequestAborted);
        }
        catch (VoiceWhipTransportAttachConflictException)
        {
            await ReleasePendingToolCredentialAsync(http, toolContextAdmission.ToolContext);
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            http.Response.Headers.RetryAfter = Math.Max(1, attachOptions.Value.ConflictRetryAfterSeconds).ToString();
            await http.Response.WriteAsync(VoiceWebSocketAttachExecutor.TransportAlreadyAttachedBody, http.RequestAborted);
        }
    }

    private static async Task AttachVoiceTargetAsync(
        HttpContext http,
        IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion> voiceRealtimeSession,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        VoiceWebSocketAttachExecutor attachExecutor,
        VoiceWebSocketAttachOptions attachOptions,
        ChatRouteVoiceAttachTarget voiceTarget)
    {
        var toolContextAdmission = await TryBuildToolContextAsync(http);
        if (!toolContextAdmission.Accepted)
            return;

        try
        {
            var result = await voiceRealtimeSession.ExecuteAsync(
                new VoiceRealtimeSessionRequest(
                    voiceTarget.ActorId.Trim(),
                    NormalizeOptional(voiceTarget.VoiceModuleName),
                    VoiceRealtimeSessionPurpose.Attach,
                    voiceTarget.SessionOverrides?.Clone(),
                    toolContextAdmission.ToolContext?.Clone()),
                static (_, _) => ValueTask.CompletedTask,
                ct: http.RequestAborted);

            var accepted = await VoiceWebSocketAttachExecutor.WriteNonAcceptedResolutionAsync(http, result, attachOptions);
            if (accepted is null)
                return;

            await attachExecutor.ExecuteAsync(
                http,
                accepted,
                mediaStreamPort,
                toolContextAdmission.TransportBinding,
                WebSocketSubprotocolToken.SelectVoiceSubprotocol(
                    http.WebSockets.WebSocketRequestedProtocols));
        }
        finally
        {
            await ReleasePendingToolCredentialAsync(http, toolContextAdmission.ToolContext);
        }
    }

    private static async Task ReleasePendingToolCredentialAsync(HttpContext http, VoiceToolExecutionContext? toolContext)
    {
        var credentialRef = NormalizeOptional(toolContext?.CredentialRef);
        if (credentialRef is null)
            return;

        var issuer = http.RequestServices.GetService<IVoiceToolCredentialIssuer>();
        if (issuer is null)
            return;

        try
        {
            await issuer.ReleaseAsync(credentialRef, CancellationToken.None);
        }
        catch (Exception ex)
        {
            GetLogger(http).LogWarning(ex, "Failed to release voice tool credential ref.");
        }
    }

    private static async Task<VoiceToolContextAdmission> TryBuildToolContextAsync(HttpContext http)
    {
        var allowedToolNames = ResolveVoiceToolAllowlist();
        var callerBearer = ExtractCallerBearer(http);
        VoiceToolCredentialIssueResult? issued = null;
        if (!string.IsNullOrWhiteSpace(callerBearer))
        {
            var issuer = http.RequestServices.GetService<IVoiceToolCredentialIssuer>();
            if (issuer is null)
            {
                await WriteCredentialUnavailableAsync(http);
                return new VoiceToolContextAdmission(false, null);
            }

            try
            {
                issued = await issuer.IssueAsync(
                    new VoiceToolCredentialIssueRequest(
                        callerBearer,
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    http.RequestAborted);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await WriteCredentialUnavailableAsync(http);
                return new VoiceToolContextAdmission(false, null);
            }

            if (issued is null || string.IsNullOrWhiteSpace(issued.CredentialRef))
            {
                await WriteCredentialUnavailableAsync(http);
                return new VoiceToolContextAdmission(false, null);
            }
        }

        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = issued?.CredentialRef ?? string.Empty,
            CallerScopeId = FirstNonEmpty(
                http.User.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value,
                http.User.FindFirst("uid")?.Value,
                http.User.FindFirst("sub")?.Value,
                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? string.Empty,
            CallerSubject = FirstNonEmpty(
                http.User.FindFirst("sub")?.Value,
                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? string.Empty,
            OwnerSubject = FirstNonEmpty(
                http.User.FindFirst("sub")?.Value,
                http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value) ?? string.Empty,
            ChannelPlatform = NormalizeOptional(http.Request.Query["channel"].ToString()) ?? OwnerScope.NyxIdPlatform,
            ChannelSenderId = FirstNonEmpty(
                http.Request.Query["sender_id"].ToString(),
                http.User.FindFirst("sender_id")?.Value) ?? string.Empty,
            ChannelRegistrationScopeId = FirstNonEmpty(
                http.Request.Query["registration_scope_id"].ToString(),
                http.User.FindFirst("registration_scope_id")?.Value,
                http.User.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value) ?? string.Empty,
            ChannelMessageId = NormalizeOptional(http.Request.Query["message_id"].ToString()) ?? string.Empty,
            ChannelPlatformMessageId = NormalizeOptional(http.Request.Query["platform_message_id"].ToString()) ?? string.Empty,
            ChannelDeliveryTargetId = NormalizeOptional(http.Request.Query["delivery_target_id"].ToString()) ?? string.Empty,
            ConnectedServicesContextJson = NormalizeOptional(http.Request.Query["connected_services_context"].ToString()) ?? string.Empty,
            NyxIdRoutePreference = NormalizeOptional(http.Request.Query["nyxid_route_preference"].ToString()) ?? string.Empty,
            SenderBindingId = NormalizeOptional(http.Request.Query["sender_binding_id"].ToString()) ?? string.Empty,
        };
        if (issued is not null)
        {
            toolContext.ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                issued.ExpiresAtUtc.ToUniversalTime());
        }

        foreach (var allowed in allowedToolNames)
            toolContext.AllowedToolNames.Add(allowed);

        var toolCatalog = http.RequestServices.GetService<IVoiceToolCatalog>();
        if (toolCatalog is null)
        {
            await ReleasePendingToolCredentialAsync(http, toolContext);
            await WriteToolCatalogUnavailableAsync(http);
            return new VoiceToolContextAdmission(false, null);
        }

        try
        {
            var snapshot = await toolCatalog.DiscoverAsync(toolContext, http.RequestAborted);
            VoiceToolCatalogSnapshotValidator.Validate(snapshot);
            toolContext.ToolCatalogProof = snapshot.Proof.Clone();
            toolContext.ToolCatalogPolicyVersion = snapshot.PolicyVersion;
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            await ReleasePendingToolCredentialAsync(http, toolContext);
            throw;
        }
        catch (Exception ex)
        {
            GetLogger(http).LogWarning(ex, "Voice tool catalog admission failed closed.");
            await ReleasePendingToolCredentialAsync(http, toolContext);
            await WriteToolCatalogUnavailableAsync(http);
            return new VoiceToolContextAdmission(false, null);
        }

        return new VoiceToolContextAdmission(true, toolContext, issued?.TransportBinding);
    }

    // The realtime voice agent's job is to operate the user's NyxID-connected services + follow its
    // skill playbooks. Default set: nyxid_proxy (call any service — Home Assistant, Frigate, …),
    // nyxid_status / nyxid_services (what's connected), nyxid_catalog (what else can be connected), and
    // ornn_search_skills + use_skill (find and load the Ornn skill playbooks — also a NyxID downstream
    // service). Override with a comma-separated VOICE_TOOL_ALLOWLIST to widen or change the set.
    private static readonly string[] DefaultVoiceToolAllowlist =
        ["nyxid_proxy", "nyxid_status", "nyxid_services", "nyxid_catalog", "ornn_search_skills", "use_skill"];

    private static IReadOnlyList<string> ResolveVoiceToolAllowlist()
    {
        var configured = Environment.GetEnvironmentVariable("VOICE_TOOL_ALLOWLIST");
        if (configured is null)
            return DefaultVoiceToolAllowlist;

        return configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record VoiceToolContextAdmission(
        bool Accepted,
        VoiceToolExecutionContext? ToolContext,
        VoiceToolCredentialTransportBinding? TransportBinding = null);

    private static async Task WriteCredentialUnavailableAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await http.Response.WriteAsync(VoiceCredentialUnavailableReason, http.RequestAborted);
    }

    private static async Task WriteToolCatalogUnavailableAsync(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await http.Response.WriteAsync(VoiceToolCatalogUnavailableReason, http.RequestAborted);
    }

    // internal (not private) so M5 can unit-test the extraction precedence
    // directly without standing up a full WebSocket handshake.
    internal static string? ExtractCallerBearer(HttpContext http)
    {
        // M5: prefer the token carried in the Sec-WebSocket-Protocol handshake
        // header (aevatar-bearer.<token>), so it never appears in the request
        // URL (request-URL logging → stdout → Elasticsearch/ingress logs). This
        // mirrors the JWT bearer-events extraction in
        // AevatarAuthenticationHostExtensions. Fall back to the Authorization
        // header, then the legacy ?access_token= query param, so older clients
        // still work (non-breaking).
        var subprotocolToken = WebSocketSubprotocolToken.ExtractBearer(
            http.WebSockets.WebSocketRequestedProtocols);
        if (!string.IsNullOrWhiteSpace(subprotocolToken))
            return subprotocolToken.Trim();

        var header = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = header[bearerPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        var queryToken = http.Request.Query["access_token"].ToString();
        return string.IsNullOrWhiteSpace(queryToken) ? null : queryToken.Trim();
    }

    private static ILogger GetLogger(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PolicyAwareVoiceEndpoints));

    private static async Task<VoiceTargetResolution> ResolveVoiceTargetWithLegacyRecoveryAsync(
        HttpContext http,
        IChatRoutePolicyQueryPort queryPort,
        IChatRoutePolicyProjectionRecoveryPort recoveryPort,
        ChatRouteResolver resolver)
    {
        if (!TryBuildCallerScope(http, out var routingScope, out var channel, out var failure))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync(failure, http.RequestAborted);
            return VoiceTargetResolution.Failed();
        }

        var routeInput = BuildRouteInput(http, routingScope, channel);
        var snapshot = await queryPort.LookupForCallerAsync(routingScope, http.RequestAborted);

        // Keep the legacy WebSocket self-heal path out of WHIP, whose request path only reads materialized state.
        if (snapshot is null && await recoveryPort.TryRematerializeAsync(routingScope, http.RequestAborted))
        {
            for (var attempt = 0; attempt < 5 && snapshot is null; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(400), http.RequestAborted);
                snapshot = await queryPort.LookupForCallerAsync(routingScope, http.RequestAborted);
            }
        }

        return await ResolveVoiceTargetDecisionAsync(http, snapshot, routeInput, resolver);
    }

    private static async Task<VoiceTargetResolution> ResolveVoiceTargetFromReadModelAsync(
        HttpContext http,
        IChatRoutePolicyQueryPort queryPort,
        ChatRouteResolver resolver)
    {
        if (!TryBuildCallerScope(http, out var routingScope, out var channel, out var failure))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync(failure, http.RequestAborted);
            return VoiceTargetResolution.Failed();
        }

        var routeInput = BuildRouteInput(http, routingScope, channel);
        var snapshot = await queryPort.LookupForCallerAsync(routingScope, http.RequestAborted);
        return await ResolveVoiceTargetDecisionAsync(http, snapshot, routeInput, resolver);
    }

    private static async Task<VoiceTargetResolution> ResolveVoiceTargetDecisionAsync(
        HttpContext http,
        ChatRoutePolicySnapshot? snapshot,
        ChatRouteInput routeInput,
        ChatRouteResolver resolver)
    {
        var decision = resolver.Resolve(snapshot, routeInput);

        var action = decision.Action;
        switch (action.ActionCase)
        {
            case ChatRouteAction.ActionOneofCase.Reject:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync(action.Reject?.Reason ?? "Voice route rejected.", http.RequestAborted);
                return VoiceTargetResolution.Failed();
            case ChatRouteAction.ActionOneofCase.ForwardToModel:
                if (ChatRouteActionTargets.TryGetVoiceAttachTarget(action, out var voiceTarget))
                    return VoiceTargetResolution.Success(voiceTarget);

                http.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await http.Response.WriteAsync("Voice ForwardToModel is not supported in v1.", http.RequestAborted);
                return VoiceTargetResolution.Failed();
            default:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Voice route did not resolve to a GAgent target.", http.RequestAborted);
                return VoiceTargetResolution.Failed();
        }
    }

    private static async Task<string> ReadSdpBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var sdp = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        request.Body.Seek(0, SeekOrigin.Begin);
        return sdp.Trim();
    }

    private static string BuildWhipResourceLocation(string sessionId) =>
        QueryString.Create("sessionId", sessionId).ToUriComponent() is var query && !string.IsNullOrEmpty(query)
            ? WhipOfferPattern + query
            : WhipOfferPattern;

    private static ChatRouteInput BuildRouteInput(
        HttpContext http,
        OwnerScope callerScope,
        string channel)
    {
        var voice = new VoiceInput
        {
            Codec = ParseEnum(http.Request.Query["codec"].ToString(), VoiceCodec.Pcm16),
            Mode = ParseEnum(http.Request.Query["mode"].ToString(), VoiceConversationMode.Unspecified),
            VoiceModuleName = NormalizeOptional(http.Request.Query["voice_module_name"].ToString())
                              ?? NormalizeOptional(http.Request.Query["module"].ToString())
                              ?? string.Empty,
        };

        return new ChatRouteInput
        {
            SourceKind = ChatSourceKind.Voice,
            CallerScope = callerScope.Clone(),
            Channel = channel,
            CommandName = string.Empty,
            ContentHint = string.Empty,
            ToolMode = ToolMode.None,
            Voice = voice,
        };
    }

    private static bool TryBuildCallerScope(
        HttpContext http,
        out OwnerScope routingScope,
        out string channel,
        out string failure)
    {
        var nyxUserId = FirstNonEmpty(
            http.User.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value,
            http.User.FindFirst("uid")?.Value,
            http.User.FindFirst("sub")?.Value,
            http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        channel = NormalizeOptional(http.Request.Query["channel"].ToString()) ?? OwnerScope.NyxIdPlatform;
        if (string.IsNullOrWhiteSpace(nyxUserId))
        {
            routingScope = new OwnerScope();
            failure = "Authenticated caller scope is missing.";
            return false;
        }

        if (IsNativeChannel(channel))
        {
            routingScope = OwnerScope.ForNyxIdNative(nyxUserId);
            channel = string.Empty;
            failure = string.Empty;
            return true;
        }

        var registrationScopeId = FirstNonEmpty(
            http.Request.Query["registration_scope_id"].ToString(),
            http.User.FindFirst("registration_scope_id")?.Value,
            http.User.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value);
        var senderId = FirstNonEmpty(
            http.Request.Query["sender_id"].ToString(),
            http.User.FindFirst("sender_id")?.Value);

        routingScope = OwnerScope.ForChannel(nyxUserId, channel, registrationScopeId ?? string.Empty, senderId ?? string.Empty);
        failure = string.Empty;
        return true;
    }

    private static bool IsNativeChannel(string channel) =>
        string.IsNullOrWhiteSpace(channel) ||
        string.Equals(channel, "nyxid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(channel, "cli", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(channel, "web", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizeOptional(value);
            if (normalized is not null)
                return normalized;
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
            return fallback;

        if (Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed))
            return parsed;

        var candidate = NormalizeEnumToken(normalized);
        foreach (var enumValue in Enum.GetValues<TEnum>())
        {
            var enumToken = NormalizeEnumToken(enumValue.ToString());
            if (string.Equals(candidate, enumToken, StringComparison.Ordinal) ||
                candidate.EndsWith(enumToken, StringComparison.Ordinal))
                return enumValue;
        }

        return fallback;
    }

    private static string NormalizeEnumToken(string value) =>
        new(value
            .Where(static ch => char.IsLetterOrDigit(ch))
            .Select(static ch => char.ToLowerInvariant(ch))
            .ToArray());

    private sealed record VoiceTargetResolution(bool Succeeded, ChatRouteVoiceAttachTarget Target)
    {
        public static VoiceTargetResolution Success(ChatRouteVoiceAttachTarget target) => new(true, target);

        public static VoiceTargetResolution Failed() => new(false, new ChatRouteVoiceAttachTarget());
    }
}
