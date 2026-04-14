using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Aevatar.Foundation.VoicePresence.Tests;

internal sealed class FakeHttpWebSocketFeature(FakeWebSocket socket, bool isWebSocketRequest = true)
    : IHttpWebSocketFeature
{
    public bool IsWebSocketRequest { get; } = isWebSocketRequest;

    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
    {
        _ = context;
        return Task.FromResult<WebSocket>(socket);
    }
}

internal sealed class FakeWebSocket : WebSocket
{
    private readonly Queue<ReceiveFrame> _frames = [];
    private readonly bool _keepOpenUntilCancelledWhenEmpty;
    private WebSocketState _state;

    public FakeWebSocket(WebSocketState state, bool keepOpenUntilCancelledWhenEmpty = false)
    {
        _state = state;
        _keepOpenUntilCancelledWhenEmpty = keepOpenUntilCancelledWhenEmpty;
    }

    public bool ThrowOnReceive { get; set; }

    public bool ThrowOnClose { get; set; }

    public bool Disposed { get; private set; }

    public int CloseCalls { get; private set; }

    public List<string> SentTexts { get; } = [];

    public List<byte[]> SentBinaries { get; } = [];

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public void EnqueueReceive(WebSocketMessageType messageType, byte[] data, bool endOfMessage = true) =>
        _frames.Enqueue(new ReceiveFrame(messageType, data, endOfMessage));

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = closeStatus;
        _ = statusDescription;
        CloseCalls++;
        if (ThrowOnClose)
            throw new WebSocketException("close failed");

        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = closeStatus;
        _ = statusDescription;
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        Disposed = true;
        _state = WebSocketState.Closed;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnReceive)
            throw new WebSocketException("receive failed");

        if (_frames.Count == 0)
        {
            if (_keepOpenUntilCancelledWhenEmpty)
            {
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => gate.TrySetResult());
                await gate.Task;
            }

            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        var frame = _frames.Dequeue();
        _state = WebSocketState.Open;
        if (frame.Data.Length > 0 && buffer.Array != null)
            Array.Copy(frame.Data, 0, buffer.Array, buffer.Offset, frame.Data.Length);

        return new WebSocketReceiveResult(frame.Data.Length, frame.MessageType, frame.EndOfMessage);
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = endOfMessage;

        if (messageType == WebSocketMessageType.Text)
        {
            SentTexts.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        }
        else if (messageType == WebSocketMessageType.Binary)
        {
            var bytes = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, bytes, 0, buffer.Count);
            SentBinaries.Add(bytes);
        }

        return Task.CompletedTask;
    }

    private readonly record struct ReceiveFrame(
        WebSocketMessageType MessageType,
        byte[] Data,
        bool EndOfMessage);
}
