using System.Net.WebSockets;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.VoicePresence.Hosting;

/// <summary>
/// Extension methods to map voice-presence WebSocket endpoints onto an ASP.NET host.
/// </summary>
// Refactor (iter15/cluster-025-voice-host-session-state-actorization):
//   Old pattern: voice host resolver locks shared mutable lease state outside actor lifecycle
//   New principle: media endpoints expose remote audio unavailability honestly.
//   Remote setup/control remains actor-owned; chunks never cross EventEnvelope.
public static class VoicePresenceEndpoints
{
    public static IEndpointConventionBuilder MapVoicePresenceWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern) =>
        endpoints.MapVoicePresenceWebSocket(
            pattern,
            static (actorId, ctx) => ResolveSessionFromServicesAsync(ctx, actorId));

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
        return endpoints.MapVoicePresenceWebSocket(
            pattern,
            async (actorId, ctx) => ToResolution(await resolveSession(actorId, ctx)));
    }

    public static IEndpointConventionBuilder MapVoicePresenceWebSocket(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<string, HttpContext, Task<VoicePresenceSessionResolution>> resolveSession)
    {
        ArgumentNullException.ThrowIfNull(resolveSession);

        // Refactor (iter74/cluster-074-voice-ws-request-polling-close-wait):
        //   Old pattern: while ws.State == Open { Task.Delay(500) } polling to keep request alive
        //   New principle: Transport owns close notification; endpoint awaits completion task without periodic sleep
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

            var resolution = await resolveSession(actorId, ctx);
            var session = await WriteNonAcceptedResolutionAsync(ctx, resolution);
            if (session == null)
                return;

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var transport = new WebSocketVoiceTransport(ws);
            var attached = false;

            try
            {
                await session.AttachTransportAsync(transport, ctx.RequestAborted);
                attached = true;
                await WaitUntilClosedAsync(transport, ctx.RequestAborted);
            }
            catch (VoiceRemoteAudioTransportUnavailableException)
            {
                await TryCloseUnsupportedRemoteAudioAsync(ws);
            }
            catch (InvalidOperationException) when (!attached)
            {
                await TryCloseConflictAsync(ws);
            }
            finally
            {
                if (attached)
                    await session.DetachTransportAsync(transport, ctx.RequestAborted);
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
        IWebRtcVoiceTransportFactory? transportFactory = null) =>
        endpoints.MapVoicePresenceWhip(
            pattern,
            static (actorId, ctx) => ResolveSessionFromServicesAsync(ctx, actorId),
            transportFactory);

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
        return endpoints.MapVoicePresenceWhip(
            pattern,
            async (actorId, ctx) => ToResolution(await resolveSession(actorId, ctx)),
            transportFactory);
    }

    public static IEndpointConventionBuilder MapVoicePresenceWhip(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<string, HttpContext, Task<VoicePresenceSessionResolution>> resolveSession,
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

            var resolution = await resolveSession(actorId, ctx);
            var session = await WriteNonAcceptedResolutionAsync(ctx, resolution);
            if (session == null)
                return;

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
                await session.AttachTransportAsync(transportSession.Transport, ctx.RequestAborted);
                attached = true;
                _ = ObserveTransportLifetimeAsync(session, transportSession.Transport, transportSession.Completion);

                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/sdp";
                ctx.Response.Headers.Location = ctx.Request.Path.ToString();
                await ctx.Response.WriteAsync(transportSession.AnswerSdp);
            }
            catch (VoiceRemoteAudioTransportUnavailableException)
            {
                if (!attached)
                    await transportSession.Transport.DisposeAsync();

                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(VoiceRemoteAudioTransportUnavailableException.Reason);
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

            var resolution = await resolveSession(actorId, ctx);
            if (resolution.Kind == VoicePresenceSessionResolutionKind.PreflightFailed &&
                resolution.PreflightFailure == VoicePresencePreflightFailureKind.NotFound)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Voice session not found for this agent.");
                return;
            }

            var session = resolution.Session;
            if (session == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(VoiceRemoteAudioTransportUnavailableException.Reason);
                return;
            }

            await session.DetachTransportAsync(ct: ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        return group;
    }

    private static Task<VoicePresenceSessionResolution> ResolveSessionFromServicesAsync(
        HttpContext ctx,
        string actorId)
    {
        var resolver = ctx.RequestServices.GetRequiredService<IVoicePresenceSessionResolver>();
        return resolver.ResolveAsync(CreateSessionRequest(ctx, actorId), ctx.RequestAborted);
    }

    private static VoicePresenceSessionResolution ToResolution(VoicePresenceSession? session)
    {
        if (session == null)
            return VoicePresenceSessionResolution.PreflightFailed(VoicePresencePreflightFailureKind.NotFound);

        if (!session.IsInitialized)
            return VoicePresenceSessionResolution.PreflightFailed(VoicePresencePreflightFailureKind.NotInitialized);

        if (session.IsTransportAttached)
        {
            return new VoicePresenceSessionResolution(
                VoicePresenceSessionResolutionKind.PreflightFailed,
                session,
                VoicePresencePreflightFailureKind.TransportAlreadyAttached);
        }

        return VoicePresenceSessionResolution.LeaseAcceptedAttached(session);
    }

    private static async Task<VoicePresenceSession?> WriteNonAcceptedResolutionAsync(
        HttpContext ctx,
        VoicePresenceSessionResolution resolution)
    {
        switch (resolution.Kind)
        {
            case VoicePresenceSessionResolutionKind.LeaseAcceptedPendingAttach:
            case VoicePresenceSessionResolutionKind.LeaseAcceptedAttached:
                return resolution.Session ?? throw new InvalidOperationException("Accepted voice session resolution requires a session.");
            case VoicePresenceSessionResolutionKind.Unsupported:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(VoiceRemoteAudioTransportUnavailableException.Reason);
                return null;
            case VoicePresenceSessionResolutionKind.PreflightFailed:
                await WritePreflightFailureAsync(ctx, resolution.PreflightFailure);
                return null;
            default:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice session resolution failed.");
                return null;
        }
    }

    private static async Task WritePreflightFailureAsync(
        HttpContext ctx,
        VoicePresencePreflightFailureKind? failure)
    {
        switch (failure)
        {
            case VoicePresencePreflightFailureKind.NotFound:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Voice session not found for this agent.");
                break;
            case VoicePresencePreflightFailureKind.NotInitialized:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice module not initialized.");
                break;
            case VoicePresencePreflightFailureKind.TransportAlreadyAttached:
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await ctx.Response.WriteAsync("Voice transport already attached.");
                break;
            default:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice session preflight failed.");
                break;
        }
    }

    private static VoicePresenceSessionRequest CreateSessionRequest(HttpContext ctx, string actorId) =>
        new(
            actorId,
            ResolveRequestedModuleName(ctx),
            string.Equals(ctx.Request.Method, HttpMethods.Delete, StringComparison.OrdinalIgnoreCase)
                ? VoicePresenceSessionRequestPurpose.Detach
                : VoicePresenceSessionRequestPurpose.Attach);

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

    private static async Task TryCloseUnsupportedRemoteAudioAsync(WebSocket ws)
    {
        if (ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ws.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                VoiceRemoteAudioTransportUnavailableException.Reason,
                cts.Token);
        }
        catch
        {
            // best effort close after websocket upgrade
        }
    }

    // Refactor (iter74/cluster-074-voice-ws-request-polling-close-wait):
    //   Old pattern: while ws.State == Open { Task.Delay(500) } polling to keep request alive
    //   New principle: Transport owns close notification; endpoint awaits completion task without periodic sleep
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
        VoicePresenceSession session,
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

        await session.DetachTransportAsync(transport);
    }
}

// Refactor (iter15/cluster-025-voice-host-session-state-actorization):
//   Old pattern: resolver and endpoints matched duplicated private exception-message constants.
//   New principle: remote media unavailability is a typed host signal with one shared reason.
internal sealed class VoiceRemoteAudioTransportUnavailableException()
    : NotSupportedException(Reason)
{
    public const string Reason = "remote_audio_transport_unavailable";
}
