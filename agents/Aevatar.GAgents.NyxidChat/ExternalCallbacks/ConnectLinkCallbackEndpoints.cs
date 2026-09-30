using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GAgents.NyxidChat.ExternalCallbacks;

/// <summary>Browser-return adapter. Provider status is never interpreted as completion evidence.</summary>
public static class ConnectLinkCallbackEndpoints
{
    public const string CallbackPath = "/api/callbacks/connect-link";

    public static IEndpointRouteBuilder MapConnectLinkCallbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(CallbackPath, HandleAsync).AllowAnonymous();
        return endpoints;
    }

    internal static async Task<IResult> HandleAsync(HttpContext context, IExternalCallbackCommandPort callbacks, CancellationToken ct)
    {
        var query = context.Request.Query;
        var linkIds = query["connect_link_id"];
        var callbackIds = query["callback_id"];
        if (linkIds.Count != 1 || !IsIdentifier(linkIds[0]) || callbackIds.Count > 1 ||
            (callbackIds.Count == 1 && !IsIdentifier(callbackIds[0])))
            return Results.BadRequest(new { error = "invalid_callback_hint" });
        try
        {
            // status is deliberately ignored. Only the exact original-user query can choose an outcome.
            await callbacks.HintAsync(callbackIds.Count == 0 ? null : callbackIds[0], linkIds[0]!, ct).ConfigureAwait(false);
            return Results.Accepted(value: new { status = "accepted" });
        }
        catch (KeyNotFoundException) { return Results.NotFound(new { error = "callback_not_found" }); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "callback_identity_mismatch" }); }
    }

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}
