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
    private bool _heartbeatStarted;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoop;

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

    public async Task WriteAsync(AGUIEvent evt, CancellationToken ct)
    {
        if (evt == null) return;

        StartHeartbeat();
        var payload = _jsonFormatter.Format(evt);
        var bytes = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
        await WriteRawAsync(bytes, ct);
    }

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
        if (_heartbeatStarted)
            return;

        _heartbeatStarted = true;
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
        if (_heartbeatCancellation != null)
        {
            await _heartbeatCancellation.CancelAsync();
            if (_heartbeatLoop != null)
                await _heartbeatLoop;

            _heartbeatCancellation.Dispose();
        }

        _writeGate.Dispose();
    }
}
