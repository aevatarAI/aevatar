using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.GAgents.NyxidChat;

public static partial class NyxIdChatEndpoints
{
    private static async Task<IResult> HandleGetStateAsync(
        HttpContext http,
        string scopeId,
        string actorId,
        [FromServices] IGAgentActorRegistryQueryPort registryQueryPort,
        [FromServices] INyxIdChatConversationStateQueryPort stateQueryPort,
        CancellationToken ct)
    {
        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out var denied))
            return denied;

        if (!TryValidateControlIdentity(scopeId, out var normalizedScopeId) ||
            !TryValidateControlIdentity(actorId, out var normalizedActorId))
        {
            return ReloadRequired(0, null, "invalid_identity");
        }

        if (!TryParseStateCursor(http, out var afterStateVersion, out var turnId))
            return ReloadRequired(0, turnId, "invalid_state_version");

        var registry = await registryQueryPort
            .ListActorsAsync(normalizedScopeId, ct)
            .ConfigureAwait(false);
        var owned = string.Equals(registry.ScopeId, normalizedScopeId, StringComparison.Ordinal) &&
                    registry.Groups.Any(group =>
                        string.Equals(
                            group.AgentKind,
                            NyxIdChatServiceDefaults.GAgentKind,
                            StringComparison.Ordinal) &&
                        group.ActorIds.Contains(normalizedActorId, StringComparer.Ordinal));
        if (!owned)
        {
            return Results.Json(
                new { status = "not_found" },
                statusCode: StatusCodes.Status404NotFound);
        }

        var result = await stateQueryPort.GetAsync(
                new NyxIdChatConversationStateQuery(
                    normalizedScopeId,
                    normalizedActorId,
                    afterStateVersion,
                    turnId),
                ct)
            .ConfigureAwait(false);

        return result.Status switch
        {
            NyxIdChatConversationStateQueryStatus.Current => Results.Ok(new
            {
                status = "current",
                result.StateVersion,
                result.TurnId,
                result.Snapshot,
            }),
            NyxIdChatConversationStateQueryStatus.NotModified => Results.Ok(new
            {
                status = "not_modified",
                result.StateVersion,
                result.TurnId,
            }),
            NyxIdChatConversationStateQueryStatus.ReloadRequired =>
                ReloadRequired(result.StateVersion, result.TurnId, result.ReasonCode),
            NyxIdChatConversationStateQueryStatus.NotFound => Results.Json(
                new { status = "not_found" },
                statusCode: StatusCodes.Status404NotFound),
            _ => ReloadRequired(result.StateVersion, result.TurnId, "unknown_query_status"),
        };
    }

    private static bool TryParseStateCursor(
        HttpContext http,
        out long? afterStateVersion,
        out string? turnId)
    {
        afterStateVersion = null;
        turnId = null;

        var versionValues = http.Request.Query["afterStateVersion"];
        if (versionValues.Count > 1)
            return false;
        if (versionValues.Count == 1)
        {
            var raw = versionValues[0];
            if (string.IsNullOrWhiteSpace(raw) ||
                !long.TryParse(
                    raw,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return false;
            }

            afterStateVersion = parsed;
        }

        var turnValues = http.Request.Query["turnId"];
        if (turnValues.Count > 1)
            return false;
        if (turnValues.Count == 1)
        {
            if (!TryValidateControlIdentity(turnValues[0], out var normalizedTurnId))
                return false;
            turnId = normalizedTurnId;
        }

        return true;
    }

    private static IResult ReloadRequired(
        long stateVersion,
        string? turnId,
        string? reasonCode) =>
        Results.Ok(new
        {
            status = "reload_required",
            stateVersion,
            turnId,
            reasonCode = string.IsNullOrWhiteSpace(reasonCode)
                ? "reload_required"
                : reasonCode,
        });
}
