using System.Net.WebSockets;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Microsoft.Extensions.Logging;

namespace Aevatar.Foundation.ExternalLinks.WebSocket;

/// <summary>
/// WebSocket implementation of <see cref="IExternalLinkTransport"/>.
///
/// Known limitations (TODO):
/// - No per-message type framing (caller must handle payload disambiguation).
/// - No sub-protocol negotiation.
/// - No custom HTTP headers for the handshake.
/// - Receive buffer is fixed at 8 KB.
/// </summary>
internal sealed class WebSocketTransport : IExternalLinkTransport
{
    private const int ReceiveBufferSize = 8192;

    private readonly ILogger _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;

    public string TransportType => "websocket";

    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? OnMessageReceived { private get; set; }
    public Func<ExternalLinkStateChange, string?, CancellationToken, Task>? OnStateChanged { private get; set; }

    public WebSocketTransport(ILogger logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        await _ws.ConnectAsync(new Uri(descriptor.Endpoint), ct);

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("WebSocket is not connected.");

        await _ws.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _receiveCts?.Cancel();

        if (_ws is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket close handshake failed (best-effort)");
            }
        }

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; }
            catch { /* best-effort */ }
            _receiveLoop = null;
        }

        _ws?.Dispose();
        _ws = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                ms.SetLength(0);
                bool endOfMessage;

                do
                {
                    var vResult = await _ws.ReceiveAsync(buffer.AsMemory(), ct);
                    if (vResult.MessageType == WebSocketMessageType.Close)
                    {
                        await NotifyStateChangedAsync(ExternalLinkStateChange.Disconnected,
                            _ws.CloseStatusDescription ?? "remote close", ct);
                        return;
                    }

                    ms.Write(buffer, 0, vResult.Count);
                    endOfMessage = vResult.EndOfMessage;
                } while (!endOfMessage);

                if (ms.Length > 0 && OnMessageReceived != null)
                {
                    var data = ms.ToArray();
                    await OnMessageReceived(data, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket receive error");
            await NotifyStateChangedAsync(ExternalLinkStateChange.Disconnected, ex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in WebSocket receive loop");
            await NotifyStateChangedAsync(ExternalLinkStateChange.Error, ex.Message, CancellationToken.None);
        }
    }

    private Task NotifyStateChangedAsync(ExternalLinkStateChange state, string? reason, CancellationToken ct) =>
        OnStateChanged?.Invoke(state, reason, ct) ?? Task.CompletedTask;
}
