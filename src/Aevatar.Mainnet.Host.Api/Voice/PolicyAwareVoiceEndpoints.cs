using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
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

namespace Aevatar.Mainnet.Host.Api.Voice;

public static class PolicyAwareVoiceEndpoints
{
    private const string DefaultPattern = "/ws/voice";
    internal const string VoiceNotConfiguredReason = "voice_not_configured";
    private const string ScopeClaimType = "scope";
    private const string RemoteAudioTransportUnavailableReason = "remote_audio_transport_unavailable";
    private const string VoiceCredentialUnavailableReason = "voice_credential_unavailable";
    private static readonly string[] RoleClaimTypes =
    [
        "scope_role",
        "scope.role",
        "role",
        ClaimTypes.Role,
    ];

    private static readonly HashSet<string> AdminRoleValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "owner",
        "scope-admin",
        "scope_admin",
    };

    public static IEndpointConventionBuilder MapPolicyAwareVoiceEndpoint(this IEndpointRouteBuilder app) =>
        app.Map(DefaultPattern, HandlePolicyAwareVoiceAsync);

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
    /// provider: both voice routes answer 503 voice_not_configured instead
    /// of throwing an unhandled DI exception.
    /// </summary>
    public static IEndpointRouteBuilder MapVoiceNotConfiguredEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(DefaultPattern, HandleVoiceNotConfiguredAsync);
        app.Map(DefaultPattern + "/{actorId}", HandleVoiceNotConfiguredAsync);
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
        [FromServices] ChatRouteResolver resolver,
        [FromServices] IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion> voiceRealtimeSession,
        [FromServices] IVoiceVolatileMediaStreamPort mediaStreamPort)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("WebSocket required.", http.RequestAborted);
            return;
        }

        if (!TryBuildCallerScope(http, out var routingScope, out var channel, out var failure))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync(failure, http.RequestAborted);
            return;
        }

        var routeInput = BuildRouteInput(http, routingScope, channel);
        var snapshot = await queryPort.LookupForCallerAsync(routingScope, http.RequestAborted);
        var decision = resolver.Resolve(snapshot, routeInput);

        var action = decision.Action;
        switch (action.ActionCase)
        {
            case ChatRouteAction.ActionOneofCase.Reject:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync(action.Reject?.Reason ?? "Voice route rejected.", http.RequestAborted);
                return;
            case ChatRouteAction.ActionOneofCase.ForwardToModel:
                if (ChatRouteActionTargets.TryGetVoiceAttachTarget(action, out var voiceTarget))
                {
                    await AttachVoiceTargetAsync(http, voiceRealtimeSession, mediaStreamPort, voiceTarget);
                    return;
                }

                // ForwardToModel without a typed voice target is ordinary model forwarding,
                // not voice attachment. Keep it fail-closed before WebSocket accept.
                http.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await http.Response.WriteAsync("Voice ForwardToModel is not supported in v1.", http.RequestAborted);
                return;
            default:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Voice route did not resolve to a GAgent target.", http.RequestAborted);
                return;
        }
    }

    private static async Task AttachVoiceTargetAsync(
        HttpContext http,
        IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion> voiceRealtimeSession,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
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

            var accepted = await WriteNonAcceptedResolutionAsync(http, result);
            if (accepted is null)
                return;

            var ws = await http.WebSockets.AcceptWebSocketAsync();
            var transport = new WebSocketVoiceTransport(ws);
            var attached = false;
            // AcquireAsync returns a handle with an empty ActiveTransportLeaseId (the id is only known
            // post-attach). Carry the resolved id so the close-time DetachAsync can stop the right relay
            // and detach the transport — without it StopRelay/transport-detach are no-ops.
            var detachHandle = accepted.LeaseHandle;
            IAsyncDisposable? realtimeSubscription = null;
            try
            {
                realtimeSubscription = await VoiceRealtimeTransportControlBridge.SubscribeAsync(
                    http.RequestServices,
                    accepted,
                    transport,
                    http.RequestAborted);
                try
                {
                    await VoiceRealtimeTransportControlBridge.SendSessionAcceptedAsync(
                        transport,
                        accepted,
                        http.RequestAborted);
                }
                catch
                {
                    await CleanupAcceptedTransportAsync(http, mediaStreamPort, accepted.LeaseHandle, transport);
                    throw;
                }

                var lifetimeCompleted = await mediaStreamPort.AttachAsync(
                    accepted.LeaseHandle,
                    transport,
                    toolContextAdmission.TransportBinding,
                    http.RequestAborted);
                if (!string.IsNullOrWhiteSpace(lifetimeCompleted?.TransportLeaseId))
                    detachHandle = detachHandle with { ActiveTransportLeaseId = lifetimeCompleted!.TransportLeaseId };

                attached = true;
                await WaitUntilClosedAsync(transport, http.RequestAborted);
            }
            catch (VoiceVolatileMediaStreamUnavailableException)
            {
                await TryCloseAsync(http, ws, RemoteAudioTransportUnavailableReason);
            }
            catch (VoiceVolatileToolCredentialUnavailableException)
            {
                await TryCloseAsync(http, ws, VoiceCredentialUnavailableReason);
            }
            catch (InvalidOperationException) when (!attached)
            {
                await TryCloseAsync(http, ws, "Voice transport already attached.");
            }
            finally
            {
                if (realtimeSubscription != null)
                    await realtimeSubscription.DisposeAsync();

                if (attached)
                    // CancellationToken.None: on a normal browser close http.RequestAborted is ALREADY
                    // cancelled, and the dispatch port throws on a cancelled token before producing the
                    // release signal — so the lease would never be released and TransportAttached would
                    // stay stuck true (blocking the next session with 409). Detach must run uncancelled.
                    await mediaStreamPort.DetachAsync(detachHandle, transport, CancellationToken.None);
            }
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
        var callerBearer = ExtractCallerBearer(http);
        if (string.IsNullOrWhiteSpace(callerBearer))
            return new VoiceToolContextAdmission(true, null);

        var issuer = http.RequestServices.GetService<IVoiceToolCredentialIssuer>();
        if (issuer is null)
        {
            await WriteCredentialUnavailableAsync(http);
            return new VoiceToolContextAdmission(false, null);
        }

        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        VoiceToolCredentialIssueResult? issued;
        try
        {
            issued = await issuer.IssueAsync(
                new VoiceToolCredentialIssueRequest(
                    callerBearer,
                    expiresAtUtc),
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

        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = issued.CredentialRef,
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(issued.ExpiresAtUtc.ToUniversalTime()),
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

        // Slim the voice tool surface to the NyxID service-operation tools (overridable via
        // VOICE_TOOL_ALLOWLIST). Unrestricted, the model session gets ~69 tools across every source
        // (skills / workflow / storage / nyxid account-mgmt), which overwhelms the realtime model's
        // tool selection so it never calls nyxid_proxy for Home Assistant. AllowedToolNames flows to
        // both the actor and the relay (model) tool discovery, so the model only sees this focused set.
        foreach (var allowed in ResolveVoiceToolAllowlist())
            toolContext.AllowedToolNames.Add(allowed);

        return new VoiceToolContextAdmission(true, toolContext, issued.TransportBinding);
    }

    // The realtime voice agent's job is to operate the user's NyxID-connected services, so by default
    // it only needs: nyxid_proxy (call any service — Home Assistant, Frigate, …), nyxid_status /
    // nyxid_services (what's connected), nyxid_catalog (what else can be connected). Override with a
    // comma-separated VOICE_TOOL_ALLOWLIST to widen or change the set.
    private static readonly string[] DefaultVoiceToolAllowlist =
        ["nyxid_proxy", "nyxid_status", "nyxid_services", "nyxid_catalog"];

    private static IReadOnlyList<string> ResolveVoiceToolAllowlist()
    {
        var configured = Environment.GetEnvironmentVariable("VOICE_TOOL_ALLOWLIST");
        if (string.IsNullOrWhiteSpace(configured))
            return DefaultVoiceToolAllowlist;

        var names = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length > 0 ? names : DefaultVoiceToolAllowlist;
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

    private static string? ExtractCallerBearer(HttpContext http)
    {
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

    private static async Task<VoiceRealtimeSessionAccepted?> WriteNonAcceptedResolutionAsync(
        HttpContext http,
        RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion> result)
    {
        if (result.Succeeded)
            return result.Receipt ?? throw new InvalidOperationException("Accepted voice realtime session requires a receipt.");

        switch (result.Error)
        {
            case VoiceRealtimeSessionStartError.Unsupported:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync(RemoteAudioTransportUnavailableReason, http.RequestAborted);
                return null;
            case VoiceRealtimeSessionStartError.NotFound:
            case VoiceRealtimeSessionStartError.NotInitialized:
            case VoiceRealtimeSessionStartError.TransportAlreadyAttached:
                await WritePreflightFailureAsync(http, result.Error);
                return null;
            default:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync("Voice realtime session failed.", http.RequestAborted);
                return null;
        }
    }

    private static async Task WritePreflightFailureAsync(
        HttpContext http,
        VoiceRealtimeSessionStartError failure)
    {
        switch (failure)
        {
            case VoiceRealtimeSessionStartError.NotFound:
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync("Voice session not found for this agent.", http.RequestAborted);
                break;
            case VoiceRealtimeSessionStartError.NotInitialized:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync("Voice module not initialized.", http.RequestAborted);
                break;
            case VoiceRealtimeSessionStartError.TransportAlreadyAttached:
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                await http.Response.WriteAsync("Voice transport already attached.", http.RequestAborted);
                break;
            default:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync("Voice session preflight failed.", http.RequestAborted);
                break;
        }
    }

    private static async Task TryCloseAsync(HttpContext http, System.Net.WebSockets.WebSocket ws, string reason)
    {
        if (ws.State is not System.Net.WebSockets.WebSocketState.Open and not System.Net.WebSockets.WebSocketState.CloseReceived)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
        }
        catch (Exception ex)
        {
            GetLogger(http).LogWarning(ex, "Failed to close voice WebSocket after upgrade.");
        }
    }

    private static async Task WaitUntilClosedAsync(WebSocketVoiceTransport transport, CancellationToken ct)
    {
        try
        {
            await transport.Completion.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task CleanupAcceptedTransportAsync(
        HttpContext http,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        VoicePresenceSessionLeaseHandle handle,
        WebSocketVoiceTransport transport)
    {
        try
        {
            await mediaStreamPort.DetachAsync(handle, transport, CancellationToken.None);
        }
        catch (Exception ex)
        {
            GetLogger(http).LogWarning(ex, "Failed to detach accepted voice transport during control-channel cleanup.");
        }

        await transport.DisposeAsync();
    }

    private static ILogger GetLogger(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PolicyAwareVoiceEndpoints));

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

    internal static bool IsVoiceDevBypassPrincipal(ClaimsPrincipal user) =>
        HasScope(user, "voice:bypass") || HasAdminRole(user);

    private static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.Claims
            .Where(static claim => string.Equals(claim.Type, ScopeClaimType, StringComparison.OrdinalIgnoreCase))
            .SelectMany(static claim => (claim.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Any(value => string.Equals(value, scope, StringComparison.Ordinal));

    private static bool HasAdminRole(ClaimsPrincipal user) =>
        user.Claims.Any(static claim =>
            RoleClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase) &&
            AdminRoleValues.Contains(claim.Value?.Trim() ?? string.Empty));

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

}
