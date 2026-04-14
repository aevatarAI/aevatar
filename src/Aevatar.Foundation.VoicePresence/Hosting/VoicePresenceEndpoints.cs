using System.Net.WebSockets;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
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
    Func<IMessage, CancellationToken, Task> SelfEventDispatcher,
    int PcmSampleRateHz = WebRtcVoiceTransportOptions.DefaultPcmSampleRateHz);

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
                    await session.Module.DetachTransportAsync(transport);
            }
        });
    }

    /// <summary>
    /// Maps a minimal WHIP-compatible endpoint for browser WebRTC voice sessions.
    /// Audio uses RTP/Opus and control frames use a WebRTC data channel.
    /// </summary>
    public static IEndpointConventionBuilder MapVoicePresenceWhip(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<string, HttpContext, Task<VoicePresenceSession?>> resolveSession,
        IWebRtcVoiceTransportFactory? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(resolveSession);

        transportFactory ??= new SipsorceryWebRtcVoiceTransportFactory();
        var group = endpoints.MapGroup(pattern);

        group.MapPost(string.Empty, async (HttpContext ctx) =>
        {
            var actorId = ctx.GetRouteValue("actorId")?.ToString();
            if (string.IsNullOrWhiteSpace(actorId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("actorId is required.");
                return;
            }

            var offerSdp = await ReadSdpBodyAsync(ctx.Request);
            if (string.IsNullOrWhiteSpace(offerSdp))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("SDP offer is required.");
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

            var transportSession = await transportFactory.CreateAsync(
                offerSdp,
                new WebRtcVoiceTransportOptions
                {
                    PcmSampleRateHz = session.PcmSampleRateHz,
                },
                ctx.RequestAborted);

            var attached = false;
            try
            {
                session.Module.AttachTransport(transportSession.Transport, session.SelfEventDispatcher);
                attached = true;
                _ = ObserveTransportLifetimeAsync(session.Module, transportSession.Transport, transportSession.Completion);

                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/sdp";
                ctx.Response.Headers.Location = ctx.Request.Path.ToString();
                await ctx.Response.WriteAsync(transportSession.AnswerSdp);
            }
            catch
            {
                if (!attached)
                    await transportSession.Transport.DisposeAsync();
                throw;
            }
        });

        group.MapDelete(string.Empty, async (HttpContext ctx) =>
        {
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

            await session.Module.DetachTransportAsync();
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        return group;
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

    private static async Task<string> ReadSdpBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var sdp = await reader.ReadToEndAsync();
        request.Body.Seek(0, SeekOrigin.Begin);
        return sdp.Trim();
    }

    private static async Task ObserveTransportLifetimeAsync(
        VoicePresenceModule module,
        IVoiceTransport transport,
        Task completion)
    {
        try
        {
            await completion;
        }
        catch
        {
            // transport completion is best-effort cleanup only
        }

        await module.DetachTransportAsync(transport);
    }
}
