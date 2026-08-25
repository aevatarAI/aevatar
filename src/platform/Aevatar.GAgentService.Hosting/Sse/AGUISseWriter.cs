using System.Text;
using Aevatar.AGUI.Contracts;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgentService.Hosting.Sse;

/// <summary>
/// Writes AG-UI events to an HTTP response as SSE frames.
/// </summary>
public sealed class AGUISseWriter : IAsyncDisposable
{
    private static readonly TypeRegistry DefaultTypeRegistry = TypeRegistry.FromFiles(
        AGUIEvent.Descriptor.File,
        GAgentDraftRunResultPayload.Descriptor.File,
        AnyReflection.Descriptor,
        StructReflection.Descriptor,
        WrappersReflection.Descriptor);
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly byte[] HeartbeatBytes = Encoding.UTF8.GetBytes(": keepalive\n\n");

    private readonly HttpResponse _response;
    private readonly JsonFormatter _jsonFormatter;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private bool _started;
    private bool _disposed;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoop;

    public bool ResponseStarted => Volatile.Read(ref _started);

    public AGUISseWriter(
        HttpResponse response,
        TypeRegistry? typeRegistry = null,
        TimeSpan? heartbeatInterval = null,
        ILogger? logger = null)
    {
        _response = response;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _logger = logger;
        _jsonFormatter = new JsonFormatter(
            JsonFormatter.Settings.Default
                .WithFormatDefaultValues(false)
                .WithTypeRegistry(typeRegistry ?? DefaultTypeRegistry));
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await StartCoreAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task WriteAsync(AGUIEvent evt, CancellationToken ct)
    {
        if (evt == null) return;

        var payload = _jsonFormatter.Format(evt);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await WriteFrameAsync(bytes, ct);
    }

    private async ValueTask WriteFrameAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            await StartCoreAsync(ct);
            await WriteRawAsync(bytes, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask StartCoreAsync(CancellationToken ct)
    {
        if (_started)
            return;

        ThrowIfDisposed();
        _response.StatusCode = StatusCodes.Status200OK;
        _response.Headers.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers.CacheControl = "no-store";
        _response.Headers.Pragma = "no-cache";
        _response.Headers["X-Accel-Buffering"] = "no";
        await _response.StartAsync(ct);
        _started = true;
        StartHeartbeat();
    }

    private async ValueTask WriteHeartbeatAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await WriteRawAsync(HeartbeatBytes, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask WriteRawAsync(byte[] bytes, CancellationToken ct)
    {
        await _response.Body.WriteAsync(bytes, ct);
        await _response.Body.FlushAsync(ct);
    }

    private void StartHeartbeat()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _heartbeatLoop != null)
                return;

            _heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(_response.HttpContext.RequestAborted);
            _heartbeatLoop = PumpHeartbeatAsync(_heartbeatCancellation.Token);
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private async Task PumpHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, ct);
                await WriteHeartbeatAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            _logger?.LogDebug(ex, "AG-UI SSE keepalive stopped because the response stream is no longer writable.");
        }
        catch (ObjectDisposedException ex)
        {
            _logger?.LogDebug(ex, "AG-UI SSE keepalive stopped because the response stream is no longer writable.");
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogDebug(ex, "AG-UI SSE keepalive stopped because the response stream is no longer writable.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AG-UI SSE keepalive stopped unexpectedly.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? heartbeatCancellation;
        Task? heartbeatLoop;

        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            heartbeatCancellation = _heartbeatCancellation;
            heartbeatLoop = _heartbeatLoop;
            _heartbeatCancellation = null;
            _heartbeatLoop = null;
        }

        if (heartbeatCancellation != null)
        {
            await heartbeatCancellation.CancelAsync();
            if (heartbeatLoop != null)
                await heartbeatLoop;

            heartbeatCancellation.Dispose();
        }

        await _writeGate.WaitAsync(CancellationToken.None);
        _writeGate.Release();
        _writeGate.Dispose();
    }
}
