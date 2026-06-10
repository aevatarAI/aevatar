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

namespace Aevatar.Mainnet.Host.Api.Voice;

public static class PolicyAwareVoiceEndpoints
{
    private const string DefaultPattern = "/ws/voice";
    private const string ScopeClaimType = "scope";
    private const string RemoteAudioTransportUnavailableReason = "remote_audio_transport_unavailable";
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
        var result = await voiceRealtimeSession.ExecuteAsync(
            new VoiceRealtimeSessionRequest(
                voiceTarget.ActorId.Trim(),
                NormalizeOptional(voiceTarget.VoiceModuleName),
                VoiceRealtimeSessionPurpose.Attach,
                voiceTarget.SessionOverrides?.Clone()),
            static (_, _) => ValueTask.CompletedTask,
            ct: http.RequestAborted);

        var accepted = await WriteNonAcceptedResolutionAsync(http, result);
        if (accepted is null)
            return;

        var ws = await http.WebSockets.AcceptWebSocketAsync();
        var transport = new WebSocketVoiceTransport(ws);
        var attached = false;
        try
        {
            await mediaStreamPort.AttachAsync(accepted.LeaseHandle, transport, http.RequestAborted);
            attached = true;
            await WaitUntilClosedAsync(transport, http.RequestAborted);
        }
        catch (VoiceVolatileMediaStreamUnavailableException)
        {
            await TryCloseAsync(ws, RemoteAudioTransportUnavailableReason);
        }
        catch (InvalidOperationException) when (!attached)
        {
            await TryCloseAsync(ws, "Voice transport already attached.");
        }
        finally
        {
            if (attached)
                await mediaStreamPort.DetachAsync(accepted.LeaseHandle, transport, http.RequestAborted);
        }
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

    private static async Task TryCloseAsync(System.Net.WebSockets.WebSocket ws, string reason)
    {
        if (ws.State is not System.Net.WebSockets.WebSocketState.Open and not System.Net.WebSockets.WebSocketState.CloseReceived)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
        }
        catch
        {
            // best effort close after websocket upgrade
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
