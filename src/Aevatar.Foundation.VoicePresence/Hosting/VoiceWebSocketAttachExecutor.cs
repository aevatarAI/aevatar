using System.Net.WebSockets;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Foundation.VoicePresence.Hosting;

public sealed class VoiceWebSocketAttachExecutor
{
    public const string TransportAlreadyAttachedBody = "Voice transport already attached.";
    public const string VoiceCredentialUnavailableReason = VoiceVolatileToolCredentialUnavailableException.Reason;

    private readonly IOptions<VoiceWebSocketAttachOptions> _options;
    private readonly ILogger<VoiceWebSocketAttachExecutor>? _logger;
    private readonly TimeProvider _timeProvider;

    public VoiceWebSocketAttachExecutor(
        IOptions<VoiceWebSocketAttachOptions> options,
        ILogger<VoiceWebSocketAttachExecutor>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ExecuteAsync(
        HttpContext http,
        VoiceRealtimeSessionAccepted accepted,
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        VoiceToolCredentialTransportBinding? transportBinding = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(mediaStreamPort);

        var options = _options.Value;
        var ws = await http.WebSockets.AcceptWebSocketAsync();
        var transport = new WebSocketVoiceTransport(ws, _logger);
        var attached = false;
        var detachHandle = accepted.LeaseHandle;
        IAsyncDisposable? realtimeSubscription = null;

        try
        {
            realtimeSubscription = await VoiceRealtimeTransportControlBridge.SubscribeAsync(
                http.RequestServices,
                accepted,
                transport,
                _logger,
                http.RequestAborted);
            try
            {
                await VoiceRealtimeTransportControlBridge.SendSessionAcceptedAsync(
                    transport,
                    accepted,
                    http.RequestAborted);
            }
            catch
            {
                await CleanupAcceptedTransportAsync(mediaStreamPort, accepted.LeaseHandle, transport);
                throw;
            }

            var lifetimeCompleted = await AttachWithTimeoutAsync(
                mediaStreamPort,
                accepted.LeaseHandle,
                transport,
                transportBinding,
                options.AttachTimeout,
                http.RequestAborted);
            if (!string.IsNullOrWhiteSpace(lifetimeCompleted?.TransportLeaseId))
                detachHandle = detachHandle with { ActiveTransportLeaseId = lifetimeCompleted.TransportLeaseId };

            attached = true;
            await WaitUntilClosedAsync(transport, options.CloseWaitTimeout, http.RequestAborted);
        }
        catch (VoiceVolatileMediaStreamUnavailableException)
        {
            await TryCloseAsync(ws, VoiceVolatileMediaStreamUnavailableException.Reason, options.PolicyViolationCloseTimeout);
        }
        catch (VoiceVolatileToolCredentialUnavailableException)
        {
            await TryCloseAsync(ws, VoiceCredentialUnavailableReason, options.PolicyViolationCloseTimeout);
        }
        catch (TimeoutException)
        {
            await TryCloseAsync(ws, "Voice transport attach timed out.", options.PolicyViolationCloseTimeout);
        }
        catch (InvalidOperationException) when (!attached)
        {
            await TryCloseAsync(ws, TransportAlreadyAttachedBody, options.PolicyViolationCloseTimeout);
        }
        finally
        {
            if (realtimeSubscription != null)
                await realtimeSubscription.DisposeAsync();

            if (attached)
                await mediaStreamPort.DetachAsync(detachHandle, transport, CancellationToken.None);
        }
    }

    public static async Task WritePreflightFailureAsync(
        HttpContext http,
        VoiceRealtimeSessionStartError failure,
        VoiceWebSocketAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        switch (failure)
        {
            case VoiceRealtimeSessionStartError.NotFound:
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync("Voice session not found for this agent.", http.RequestAborted);
                break;
            case VoiceRealtimeSessionStartError.NotInitialized:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync("Voice module not initialized.", http.RequestAborted);
                break;
            case VoiceRealtimeSessionStartError.TransportAlreadyAttached:
                http.Response.StatusCode = StatusCodes.Status409Conflict;
                http.Response.Headers.RetryAfter = Math.Max(1, options.ConflictRetryAfterSeconds).ToString();
                await http.Response.WriteAsync(TransportAlreadyAttachedBody, http.RequestAborted);
                break;
            default:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync("Voice session preflight failed.", http.RequestAborted);
                break;
        }
    }

    public static async Task<VoiceRealtimeSessionAccepted?> WriteNonAcceptedResolutionAsync(
        HttpContext http,
        RealtimeSessionResult<VoiceRealtimeSessionAccepted, VoiceRealtimeSessionStartError, VoiceRealtimeSessionCompletion> result,
        VoiceWebSocketAttachOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        if (result.Succeeded)
            return result.Receipt ?? throw new InvalidOperationException("Accepted voice realtime session requires a receipt.");

        switch (result.Error)
        {
            case VoiceRealtimeSessionStartError.Unsupported:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync(VoiceVolatileMediaStreamUnavailableException.Reason, http.RequestAborted);
                return null;
            case VoiceRealtimeSessionStartError.NotFound:
            case VoiceRealtimeSessionStartError.NotInitialized:
            case VoiceRealtimeSessionStartError.TransportAlreadyAttached:
                await WritePreflightFailureAsync(http, result.Error, options);
                return null;
            default:
                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await http.Response.WriteAsync("Voice realtime session failed.", http.RequestAborted);
                return null;
        }
    }

    private async Task<VoiceTransportLifetimeCompleted?> AttachWithTimeoutAsync(
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        VoicePresenceSessionLeaseHandle leaseHandle,
        WebSocketVoiceTransport transport,
        VoiceToolCredentialTransportBinding? transportBinding,
        TimeSpan timeout,
        CancellationToken requestAborted)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, timeoutCts.Token);
        try
        {
            return await mediaStreamPort.AttachAsync(leaseHandle, transport, transportBinding, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !requestAborted.IsCancellationRequested)
        {
            throw new TimeoutException("Voice transport attach timed out.");
        }
    }

    private async Task WaitUntilClosedAsync(
        WebSocketVoiceTransport transport,
        TimeSpan timeout,
        CancellationToken requestAborted)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, timeoutCts.Token);
        try
        {
            await transport.Completion.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !requestAborted.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupAcceptedTransportAsync(
        IVoiceVolatileMediaStreamPort mediaStreamPort,
        VoicePresenceSessionLeaseHandle handle,
        WebSocketVoiceTransport transport)
    {
        try
        {
            await mediaStreamPort.DetachAsync(handle, transport, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to detach accepted voice transport during control-channel cleanup.");
        }

        await transport.DisposeAsync();
    }

    private async Task TryCloseAsync(WebSocket ws, string reason, TimeSpan timeout)
    {
        if (ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return;

        try
        {
            using var cts = new CancellationTokenSource(timeout, _timeProvider);
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to close voice WebSocket after upgrade.");
        }
    }
}
