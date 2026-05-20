using System.Net.WebSockets;
using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Aevatar.Foundation.VoicePresence.Hosting;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.GAgents.Scheduled;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ScheduledOwnerScope = Aevatar.GAgents.Scheduled.OwnerScope;
using RoutingOwnerScope = Aevatar.ChatRouting.Core.OwnerScope;

namespace Aevatar.Mainnet.Host.Api.Voice;

public static class PolicyAwareVoiceEndpoints
{
    private const string DefaultPattern = "/ws/voice";
    private const string ScopeClaimType = "scope";
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

    // Implement (issue #695):
    //   Behavior: resolve /ws/voice target through ChatRoutePolicy before WebSocket upgrade.
    //   Why this shape: the host boundary composes routing, authorization, and voice attach without
    //   pulling ChatRouting back into Foundation.VoicePresence.
    private static async Task HandlePolicyAwareVoiceAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("WebSocket required.", http.RequestAborted);
            return;
        }

        if (!TryBuildCallerScope(http, out var routingScope, out var scheduledScope, out var channel, out var failure))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync(failure, http.RequestAborted);
            return;
        }

        var routeInput = BuildRouteInput(http, routingScope, channel);
        var queryPort = http.RequestServices.GetRequiredService<IChatRoutePolicyQueryPort>();
        var resolver = http.RequestServices.GetRequiredService<ChatRouteResolver>();
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
                http.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await http.Response.WriteAsync("Voice ForwardToModel is not supported in v1.", http.RequestAborted);
                return;
            case ChatRouteAction.ActionOneofCase.ForwardToGagent:
                break;
            default:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Voice route did not resolve to a GAgent target.", http.RequestAborted);
                return;
        }

        var actorId = action.ForwardToGagent.ActorId?.Trim();
        if (string.IsNullOrWhiteSpace(actorId))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync("Voice route target actor is empty.", http.RequestAborted);
            return;
        }

        if (!await CanAttachAsync(http, actorId, scheduledScope, http.RequestAborted))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync("Caller is not allowed to attach to this voice target.", http.RequestAborted);
            return;
        }

        var sessionResolver = http.RequestServices.GetRequiredService<IVoicePresenceSessionResolver>();
        var moduleName = FirstNonEmpty(action.ForwardToGagent.VoiceModuleName, routeInput.Voice?.VoiceModuleName);
        var session = await sessionResolver.ResolveAsync(
            new VoicePresenceSessionRequest(actorId, moduleName),
            http.RequestAborted);
        if (session is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsync("Voice session not found for this agent.", http.RequestAborted);
            return;
        }

        if (!session.IsInitialized)
        {
            // 503 (not 404) so clients treat the routed target as cold, not
            // missing, and retry. Matches the dev bypass at
            // VoicePresenceEndpoints.MapVoicePresenceWebSocket so /ws/voice
            // behaves identically while the GAgent's voice module warms up.
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync("Voice module not initialized.", http.RequestAborted);
            return;
        }

        if (session.IsTransportAttached)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsync("Voice transport already attached.", http.RequestAborted);
            return;
        }

        var ws = await http.WebSockets.AcceptWebSocketAsync();
        var transport = new WebSocketVoiceTransport(ws);
        var attached = false;
        try
        {
            await session.AttachTransportAsync(transport, http.RequestAborted);
            attached = true;
            await WaitUntilClosedAsync(ws, http.RequestAborted);
        }
        catch when (!attached)
        {
            await TryClosePolicyViolationAsync(ws);
        }
        finally
        {
            if (attached)
                await session.DetachTransportAsync(transport, http.RequestAborted);
        }
    }

    private static ChatRouteInput BuildRouteInput(
        HttpContext http,
        RoutingOwnerScope callerScope,
        string channel)
    {
        var voice = new VoiceInput
        {
            Codec = ParseEnum(http.Request.Query["codec"].ToString(), VoiceCodec.Pcm16),
            SampleRateHz = ParseInt(http.Request.Query["sample_rate_hz"].ToString()),
            Mode = ParseEnum(http.Request.Query["mode"].ToString(), VoiceConversationMode.Unspecified),
            VadMode = ParseEnum(http.Request.Query["vad_mode"].ToString(), VadMode.Unspecified),
            VoiceModuleName = NormalizeOptional(http.Request.Query["voice_module_name"].ToString())
                              ?? NormalizeOptional(http.Request.Query["module"].ToString())
                              ?? string.Empty,
        };

        return new ChatRouteInput
        {
            SourceKind = ChatSourceKind.Voice,
            CallerScope = new ChatRouteCallerScope
            {
                NyxUserId = callerScope.NyxUserId,
                Platform = callerScope.Platform,
                RegistrationScopeId = callerScope.RegistrationScopeId,
                SenderId = callerScope.SenderId,
            },
            Channel = channel,
            CommandName = string.Empty,
            ContentHint = string.Empty,
            ToolMode = ToolMode.None,
            Voice = voice,
        };
    }

    private static bool TryBuildCallerScope(
        HttpContext http,
        out RoutingOwnerScope routingScope,
        out ScheduledOwnerScope scheduledScope,
        out string channel,
        out string failure)
    {
        var nyxUserId = FirstNonEmpty(
            http.User.FindFirst(AevatarStandardClaimTypes.ScopeId)?.Value,
            http.User.FindFirst("uid")?.Value,
            http.User.FindFirst("sub")?.Value,
            http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);

        channel = NormalizeOptional(http.Request.Query["channel"].ToString()) ?? RoutingOwnerScope.NyxIdPlatform;
        if (string.IsNullOrWhiteSpace(nyxUserId))
        {
            routingScope = new RoutingOwnerScope();
            scheduledScope = new ScheduledOwnerScope();
            failure = "Authenticated caller scope is missing.";
            return false;
        }

        if (IsNativeChannel(channel))
        {
            routingScope = RoutingOwnerScope.ForNyxIdNative(nyxUserId);
            scheduledScope = ScheduledOwnerScope.ForNyxIdNative(nyxUserId);
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

        routingScope = RoutingOwnerScope.ForChannel(nyxUserId, channel, registrationScopeId ?? string.Empty, senderId ?? string.Empty);
        scheduledScope = ScheduledOwnerScope.ForChannel(nyxUserId, channel, registrationScopeId ?? string.Empty, senderId ?? string.Empty);
        failure = string.Empty;
        return true;
    }

    private static bool IsNativeChannel(string channel) =>
        string.IsNullOrWhiteSpace(channel) ||
        string.Equals(channel, "nyxid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(channel, "cli", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(channel, "web", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> CanAttachAsync(
        HttpContext http,
        string actorId,
        ScheduledOwnerScope callerScope,
        CancellationToken ct)
    {
        if (IsVoiceDevBypassPrincipal(http.User))
            return true;

        var catalog = http.RequestServices.GetRequiredService<IUserAgentCatalogQueryPort>();
        return await catalog.GetForCallerAsync(actorId, callerScope, ct) is not null;
    }

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

    private static int ParseInt(string value) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;

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

    private static async Task WaitUntilClosedAsync(WebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                await Task.Delay(500, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task TryClosePolicyViolationAsync(WebSocket ws)
    {
        if (ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Voice session policy violation.",
                cts.Token);
        }
        catch
        {
            // best effort close after websocket upgrade
        }
    }
}
