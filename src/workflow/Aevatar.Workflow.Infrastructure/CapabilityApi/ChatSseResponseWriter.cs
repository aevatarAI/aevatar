using System.Text;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal sealed class ChatSseResponseWriter : IAsyncDisposable
{
    // The chat SSE stream sits behind an nginx ingress whose default proxy-read-timeout is 60s — an
    // IDLE timeout measured between two successive reads from this upstream. A long agent run has silent
    // stretches (LLM rounds that emit no frame) that can exceed that window, so the proxy severs the
    // connection mid-run and the browser surfaces a "network error" even though the run finished
    // server-side. A periodic SSE comment keeps the stream from ever going idle for that long. Comments
    // (lines beginning with ':') are inert to consumers: the EventSource spec ignores them, and the
    // studio client only parses 'data:' lines.
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes(": keepalive\n\n");

    private readonly HttpResponse _response;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _started;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoop;

    public ChatSseResponseWriter(HttpResponse response, TimeSpan? heartbeatInterval = null, ILogger? logger = null)
    {
        _response = response;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _logger = logger;
    }

    public bool Started => _started;

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (_started)
            return;

        _started = true;
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-store";
        _response.Headers.Pragma = "no-cache";
        _response.Headers["X-Accel-Buffering"] = "no";
        await _response.StartAsync(ct);
        StartHeartbeat();
    }

    public async ValueTask WriteAsync(WorkflowRunEventEnvelope frame, CancellationToken ct = default)
    {
        await StartAsync(ct);
        var payload = ChatJsonPayloads.Format(frame);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await WriteRawAsync(bytes, ct);
    }

    // Serializes every write to the response body (data frames and heartbeat comments) so the keepalive
    // pump can never interleave bytes in the middle of a data frame.
    private async ValueTask WriteRawAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await _response.Body.WriteAsync(bytes, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void StartHeartbeat()
    {
        // Stop the keepalive when the client disconnects (RequestAborted) or when the writer is disposed
        // at the end of the run; linking to RequestAborted means we stop writing to a dead connection.
        _heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(_response.HttpContext.RequestAborted);
        _heartbeatLoop = PumpHeartbeatAsync(_heartbeatCancellation.Token);
    }

    private async Task PumpHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, ct);
                await WriteRawAsync(HeartbeatBytes, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the run finished (dispose) or the client went away (RequestAborted).
        }
        catch (IOException ex)
        {
            // Expected when the client connection drops mid-write; the main pump owns the run lifecycle.
            _logger?.LogDebug(ex, "Chat SSE keepalive stopped because the response stream is no longer writable.");
        }
        catch (ObjectDisposedException ex)
        {
            _logger?.LogDebug(ex, "Chat SSE keepalive stopped because the response stream is no longer writable.");
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogDebug(ex, "Chat SSE keepalive stopped because the response stream is no longer writable.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Chat SSE keepalive stopped unexpectedly.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_heartbeatCancellation != null)
        {
            await _heartbeatCancellation.CancelAsync();
            if (_heartbeatLoop != null)
            {
                // PumpHeartbeatAsync observes all of its own exceptions, so awaiting it cannot throw.
                await _heartbeatLoop;
            }

            _heartbeatCancellation.Dispose();
        }

        _writeGate.Dispose();
    }
}
