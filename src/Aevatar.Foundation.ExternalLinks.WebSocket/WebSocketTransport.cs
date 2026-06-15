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
// Refactor (iter56/cluster-912-external-link-signal-contract):
// old=transport direct callback, new=typed signal sink.
// The WebSocket I/O loop publishes typed internal signals only.
// Actor/module turns consume those signals and reconcile link state.
internal sealed class WebSocketTransport : IExternalLinkTransport
{
    private const int ReceiveBufferSize = 8192;

    private readonly ILogger _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;

    public string TransportType => "websocket";

    public IExternalLinkSignalSink? SignalSink { private get; set; }

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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug(ex, "WebSocket close handshake failed (best-effort)");
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "WebSocket close handshake failed (best-effort)");
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "WebSocket close handshake failed (best-effort)");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "WebSocket close handshake failed (best-effort)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected WebSocket close handshake failure");
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected WebSocket receive loop failure during dispose");
            }
            _receiveLoop = null;
        }

        _ws?.Dispose();
        _ws = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        // Refactor (iter56/cluster-912-external-link-signal-contract):
        // old=transport direct callback, new=typed signal sink.
        // Received frames become ExternalLinkMessageReceivedSignal only.
        // Caller business handlers are never invoked from this I/O loop.
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

                if (ms.Length > 0 && SignalSink != null)
                {
                    var data = ms.ToArray();
                    await SignalSink.PublishMessageReceivedAsync(
                        new ExternalLinkMessageReceivedSignal
                        {
                            RawPayload = Google.Protobuf.ByteString.CopyFrom(data),
                            ReceivedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                        },
                        ct);
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

    // Refactor (iter56/cluster-912-external-link-signal-contract):
    // old=transport direct callback, new=typed signal sink.
    // State transitions are serialized as ExternalLinkTransportStateChangedSignal.
    // The owning actor/module turn decides the resulting business event.
    private Task NotifyStateChangedAsync(ExternalLinkStateChange state, string? reason, CancellationToken ct) =>
        SignalSink?.PublishStateChangedAsync(
            new ExternalLinkTransportStateChangedSignal
            {
                State = ToSignalKind(state),
                Reason = reason ?? string.Empty,
            },
            ct) ?? Task.CompletedTask;

    private static ExternalLinkTransportStateSignalKind ToSignalKind(ExternalLinkStateChange state) =>
        state switch
        {
            ExternalLinkStateChange.Connected => ExternalLinkTransportStateSignalKind.Connected,
            ExternalLinkStateChange.Disconnected => ExternalLinkTransportStateSignalKind.Disconnected,
            ExternalLinkStateChange.Error => ExternalLinkTransportStateSignalKind.Error,
            ExternalLinkStateChange.Closed => ExternalLinkTransportStateSignalKind.Closed,
            _ => ExternalLinkTransportStateSignalKind.Unspecified,
        };

}
