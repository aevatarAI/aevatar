using System.Net.WebSockets;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Extension methods to map voice-presence WebSocket endpoints onto an ASP.NET host.
/// </summary>
public static class VoicePresenceEndpoints
{
    /// <summary>
    /// Maps a WebSocket endpoint that bridges user audio to a voice-enabled GAgent.
    /// <para>
    /// The <paramref name="resolveModule"/> delegate is called with the actorId from the
    /// route and must return the <see cref="VoicePresenceModule"/> attached to that agent.
    /// This keeps the endpoint decoupled from any specific grain/actor resolution mechanism.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapVoicePresenceWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<string, HttpContext, Task<VoicePresenceModule?>> resolveModule)
    {
        ArgumentNullException.ThrowIfNull(resolveModule);

        return endpoints.Map(pattern, async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketSupported)
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

            var module = await resolveModule(actorId, ctx);
            if (module == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Voice module not found for this agent.");
                return;
            }

            if (!module.IsInitialized)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice module not initialized.");
                return;
            }

            var logger = ctx.RequestServices.GetService(typeof(ILogger<WebSocketVoiceTransport>)) as ILogger;
            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var transport = new WebSocketVoiceTransport(ws);

            try
            {
                module.AttachTransport(transport, CreateControlEventDispatcher(module));
                await WaitUntilClosedAsync(ws, ctx.RequestAborted);
            }
            finally
            {
                await module.DetachTransportAsync();
            }
        });
    }

    private static Func<VoiceProviderEvent, CancellationToken, Task> CreateControlEventDispatcher(
        VoicePresenceModule module)
    {
        return (evt, ct) => module.HandleProviderEventAsync(evt, ct);
    }

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
}
