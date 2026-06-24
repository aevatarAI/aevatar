using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;
using Aevatar.Foundation.VoicePresence.Transport.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            static (request, ctx) => ResolveSessionFromServicesAsync(ctx, request),
            static ctx => ctx.RequestServices.GetRequiredService<IVoiceVolatileMediaStreamPort>(),
            transportFactory);

    public static IEndpointConventionBuilder MapVoicePresenceWhip(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<VoiceRealtimeSessionRequest, HttpContext, Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>>> startSession,
        Func<HttpContext, IVoiceVolatileMediaStreamPort> resolveMediaPort,
        IWebRtcVoiceTransportFactory? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(startSession);
        ArgumentNullException.ThrowIfNull(resolveMediaPort);

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

            var result = await startSession(CreateSessionRequest(ctx, actorId, VoiceRealtimeSessionPurpose.Attach), ctx);
            var accepted = await WriteNonAcceptedResolutionAsync(ctx, result);
            if (accepted == null)
                return;

            var transportSession = await transportFactory.CreateAsync(
                offerSdp,
                new WebRtcVoiceTransportOptions
                {
                    PcmSampleRateHz = accepted.PcmSampleRateHz,
                },
                ctx.RequestAborted);

            var attached = false;
            var mediaPort = resolveMediaPort(ctx);
            var logger = ResolveLogger(ctx);
            try
            {
                var lifetimeCompleted = await mediaPort.AttachAsync(accepted.LeaseHandle, transportSession.Transport, ctx.RequestAborted);
                attached = true;
                _ = ObserveTransportLifetimeAsync(mediaPort, accepted.LeaseHandle, lifetimeCompleted, transportSession.Completion, logger);

                ctx.Response.StatusCode = StatusCodes.Status201Created;
                ctx.Response.ContentType = "application/sdp";
                ctx.Response.Headers.Location = ctx.Request.Path.ToString();
                await ctx.Response.WriteAsync(transportSession.AnswerSdp);
            }
            catch (VoiceVolatileMediaStreamUnavailableException)
            {
                if (!attached)
                    await transportSession.Transport.DisposeAsync();

                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(VoiceVolatileMediaStreamUnavailableException.Reason);
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

            var result = await startSession(CreateSessionRequest(ctx, actorId, VoiceRealtimeSessionPurpose.Detach), ctx);
            if (!result.Succeeded && result.Error == VoiceRealtimeSessionStartError.NotFound)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Voice session not found for this agent.");
                return;
            }

            if (!result.Succeeded || result.Receipt == null)
            {
                await WriteNonAcceptedResolutionAsync(ctx, result);
                return;
            }

            await resolveMediaPort(ctx).DetachAsync(result.Receipt.LeaseHandle, null, ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        return group;
    }

    private static Task<RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion>> ResolveSessionFromServicesAsync(
        HttpContext ctx,
        VoiceRealtimeSessionRequest request)
    {
        var session = ctx.RequestServices.GetRequiredService<
            IRealtimeSession<VoiceRealtimeSessionRequest, VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeFrame, VoiceRealtimeSessionCompletion>>();
        return session.ExecuteAsync(request, static (_, _) => ValueTask.CompletedTask, ct: ctx.RequestAborted);
    }

    private static async Task<VoiceRealtimeSessionAccepted?> WriteNonAcceptedResolutionAsync(
        HttpContext ctx,
        RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion> result)
    {
        if (result.Succeeded)
            return result.Receipt ?? throw new InvalidOperationException("Accepted voice realtime session requires a receipt.");

        switch (result.Error)
        {
            case VoiceRealtimeSessionStartError.Unsupported:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync(VoiceVolatileMediaStreamUnavailableException.Reason);
                return null;
            case VoiceRealtimeSessionStartError.CapabilityNotReady:
                // Capability grain accepted the lease but its read-model projection has not caught up.
                // Typed, retryable 503 with an {error,detail} body instead of an opaque empty-body 500.
                // Mirror VoiceWebSocketAttachExecutor's CapabilityNotReady handling so both voice ingresses
                // return the same contract.
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "1";
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync(
                    $$"""{"error":"{{VoiceWebSocketAttachExecutor.CapabilityNotReadyReason}}","detail":"Voice capability is not ready yet (read-model projection is catching up). Retry shortly."}""");
                return null;
            case VoiceRealtimeSessionStartError.NotFound:
            case VoiceRealtimeSessionStartError.NotInitialized:
            case VoiceRealtimeSessionStartError.TransportAlreadyAttached:
                await WritePreflightFailureAsync(ctx, result.Error);
                return null;
            default:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice realtime session failed.");
                return null;
        }
    }

    private static async Task WritePreflightFailureAsync(
        HttpContext ctx,
        VoiceRealtimeSessionStartError failure)
    {
        switch (failure)
        {
            case VoiceRealtimeSessionStartError.NotFound:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("Voice session not found for this agent.");
                break;
            case VoiceRealtimeSessionStartError.NotInitialized:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice module not initialized.");
                break;
            case VoiceRealtimeSessionStartError.TransportAlreadyAttached:
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                ctx.Response.Headers.RetryAfter = "1";
                await ctx.Response.WriteAsync("Voice transport already attached.");
                break;
            default:
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("Voice session preflight failed.");
                break;
        }
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
        IVoiceVolatileMediaStreamPort mediaPort,
        VoicePresenceSessionLeaseHandle handle,
        VoiceTransportLifetimeCompleted? lifetimeCompleted,
        Task completion,
        ILogger logger)
    {
        try
        {
            await completion;
        }
        catch (Exception ex)
        {
            // transport completion is best-effort cleanup only
            logger.LogWarning(ex, "Voice transport completion faulted; proceeding to lifetime cleanup.");
        }

        await mediaPort.CompleteTransportLifetimeAsync(handle, lifetimeCompleted, "host_transport_completed");
    }

    private static ILogger ResolveLogger(HttpContext ctx) =>
        ctx.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(VoicePresenceEndpoints));
}
