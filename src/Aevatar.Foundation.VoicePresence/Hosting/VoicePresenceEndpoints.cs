using System.Net.WebSockets;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.Transport;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Resolved voice session. The grain provides both the module and
/// a dispatcher that routes voice control/provider events through the grain inbox.
/// </summary>
public sealed record VoicePresenceSession(
    VoicePresenceModule Module,
    Func<IMessage, CancellationToken, Task> SelfEventDispatcher);

/// <summary>
/// Extension methods to map voice-presence WebSocket endpoints onto an ASP.NET host.
/// </summary>
public static class VoicePresenceEndpoints
{
    /// <summary>
    /// Maps a WebSocket endpoint that bridges user audio to a voice-enabled GAgent.
    /// <para>
    /// The <paramref name="resolveSession"/> delegate returns a <see cref="VoicePresenceSession"/>
    /// containing the module and a self-event dispatcher that routes voice control events
    /// through the grain inbox (e.g. via <c>SendToAsync(selfId, envelope)</c>). This ensures
    /// provider callbacks and user control frames are processed in the actor's single-threaded turn.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapVoicePresenceWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<string, HttpContext, Task<VoicePresenceSession?>> resolveSession)
    {
        ArgumentNullException.ThrowIfNull(resolveSession);

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

            var session = await resolveSession(actorId, ctx);
            if (session == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Voice session not found for this agent.");
                return;
            }

            if (!session.Module.IsInitialized)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice module not initialized.");
                return;
            }

            if (session.Module.IsTransportAttached)
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("Voice transport already attached.");
                return;
            }

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var transport = new WebSocketVoiceTransport(ws);
            var attached = false;

            try
            {
                session.Module.AttachTransport(transport, session.SelfEventDispatcher);
                attached = true;
                await WaitUntilClosedAsync(ws, ctx.RequestAborted);
            }
            catch (InvalidOperationException) when (!attached)
            {
                await TryCloseConflictAsync(ws);
            }
            finally
            {
                if (attached)
                    await session.Module.DetachTransportAsync();
            }
        });
    }

    private static async Task TryCloseConflictAsync(WebSocket ws)
    {
        if (ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Voice transport already attached.",
                cts.Token);
        }
        catch
        {
            // best effort close after websocket upgrade
        }
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
