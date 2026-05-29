using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.ChatRouting.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

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
    private static async Task HandlePolicyAwareVoiceAsync(
        HttpContext http,
        [FromServices] IChatRoutePolicyQueryPort queryPort,
        [FromServices] ChatRouteResolver resolver)
    {
        // Refactor (issue1321-first): reject unsupported policy-aware voice routes
        // before accepting the WebSocket.
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
                // Refactor (issue1321-first): ForwardToModel.tool_choice_hint is tool prefill only,
                // not a voice actor address. Ordinary policy-aware voice fails closed before accept.
                http.Response.StatusCode = StatusCodes.Status501NotImplemented;
                await http.Response.WriteAsync("Voice ForwardToModel is not supported in v1.", http.RequestAborted);
                return;
            default:
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("Voice route did not resolve to a GAgent target.", http.RequestAborted);
                return;
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

}
