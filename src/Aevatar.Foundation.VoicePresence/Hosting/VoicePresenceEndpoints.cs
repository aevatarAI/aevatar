using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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

            var result = await startSession(CreateSessionRequest(ctx, actorId, VoiceRealtimeSessionPurpose.Attach), ctx);
            var accepted = await WriteNonAcceptedResolutionAsync(ctx, result);
            if (accepted == null)
                return;

            var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var transport = new WebSocketVoiceTransport(ws);
            var mediaPort = resolveMediaPort(ctx);
            var attached = false;
            var detachHandle = accepted.LeaseHandle;
            IAsyncDisposable? realtimeSubscription = null;

            try
            {
                realtimeSubscription = await VoiceRealtimeTransportControlBridge.SubscribeAsync(
                    ctx.RequestServices,
                    accepted,
                    transport,
                    ctx.RequestAborted);
                try
                {
                    await VoiceRealtimeTransportControlBridge.SendSessionAcceptedAsync(
                        transport,
                        accepted,
                        ctx.RequestAborted);
                }
                catch
                {
                    await CleanupAcceptedTransportAsync(mediaPort, accepted.LeaseHandle, transport);
                    throw;
                }

                var lifetimeCompleted = await mediaPort.AttachAsync(accepted.LeaseHandle, transport, ctx.RequestAborted);
                if (!string.IsNullOrWhiteSpace(lifetimeCompleted?.TransportLeaseId))
                    detachHandle = detachHandle with { ActiveTransportLeaseId = lifetimeCompleted.TransportLeaseId };
                attached = true;
                await WaitUntilClosedAsync(transport, ctx.RequestAborted);
            }
            catch (VoiceVolatileMediaStreamUnavailableException)
            {
                await TryCloseUnsupportedRemoteAudioAsync(ws);
            }
            catch (InvalidOperationException) when (!attached)
            {
                await TryCloseConflictAsync(ws);
            }
            finally
            {
                if (realtimeSubscription != null)
                    await realtimeSubscription.DisposeAsync();

                if (attached)
                    await mediaPort.DetachAsync(detachHandle, transport, ctx.RequestAborted);
            }
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
                VoiceVolatileMediaStreamUnavailableException.Reason,
                cts.Token);
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

    private static async Task CleanupAcceptedTransportAsync(
        IVoiceVolatileMediaStreamPort mediaPort,
        VoicePresenceSessionLeaseHandle handle,
        WebSocketVoiceTransport transport)
    {
        try
        {
            await mediaPort.DetachAsync(handle, transport, CancellationToken.None);
        }
        catch
        {
            // best effort cleanup for an accepted lease whose control channel failed before attach
        }

        await transport.DisposeAsync();
    }

}
