using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Extension methods to map voice-presence WebSocket endpoints onto an ASP.NET host.
/// </summary>
public static class VoicePresenceEndpoints
{
    public static IEndpointConventionBuilder MapVoicePresenceWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern) =>
        endpoints.MapVoicePresenceWebSocket(
            pattern,
            static (request, ctx) => ResolveSessionFromServicesAsync(ctx, request),
            static ctx => ctx.RequestServices.GetRequiredService<IVoiceVolatileMediaStreamPort>());

    public static IEndpointConventionBuilder MapVoicePresenceWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<VoiceRealtimeSessionRequest, HttpContext, Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>>> startSession,
        Func<HttpContext, IVoiceVolatileMediaStreamPort> resolveMediaPort)
    {
        ArgumentNullException.ThrowIfNull(startSession);
        ArgumentNullException.ThrowIfNull(resolveMediaPort);

        return endpoints.Map(pattern, async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket required.");
                return;
            }

            var actorId = ctx.GetRouteValue("actorId")?.ToString();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("actorId is required.");
                return;
            }

            var options = ctx.RequestServices.GetRequiredService<IOptions<VoiceWebSocketAttachOptions>>().Value;
            var result = await startSession(CreateSessionRequest(ctx, actorId, VoiceRealtimeSessionPurpose.Attach), ctx);
            var accepted = await VoiceWebSocketAttachExecutor.WriteNonAcceptedResolutionAsync(ctx, result, options);
            if (accepted == null)
                return;

            await ctx.RequestServices.GetRequiredService<VoiceWebSocketAttachExecutor>()
                .ExecuteAsync(ctx, accepted, resolveMediaPort(ctx));
        });
    }

    private static Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>> ResolveSessionFromServicesAsync(
        HttpContext ctx,
        VoiceRealtimeSessionRequest request)
    {
        var session = ctx.RequestServices.GetRequiredService<
            IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>();
        return session.ExecuteAsync(request, static (_, _) => ValueTask.CompletedTask, ct: ctx.RequestAborted);
    }

    private static VoiceRealtimeSessionRequest CreateSessionRequest(
        HttpContext ctx,
        string actorId,
        VoiceRealtimeSessionPurpose purpose) =>
        new(
            actorId,
            ResolveRequestedModuleName(ctx),
            purpose);

    private static string? ResolveRequestedModuleName(HttpContext ctx)
    {
        var routeModuleName = ctx.GetRouteValue("moduleName")?.ToString();
        if (!string.IsNullOrWhiteSpace(routeModuleName))
            return routeModuleName;

        var queryModuleName = ctx.Request.Query["module"].ToString();
        return string.IsNullOrWhiteSpace(queryModuleName)
            ? null
            : queryModuleName.Trim();
    }

}
