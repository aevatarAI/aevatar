using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatWebSocketProtocolCoverageTests
{
    [Fact]
    public async Task ReceiveAsync_ShouldAggregateFrames_AndSkipUnsupportedMessageTypes()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueFrame(new WebSocketReceiveResult(0, (WebSocketMessageType)99, endOfMessage: false), Array.Empty<byte>());
        socket.EnqueueFrame(new WebSocketReceiveResult(0, (WebSocketMessageType)99, endOfMessage: true), Array.Empty<byte>());
        socket.EnqueueFrame(new WebSocketReceiveResult(2, WebSocketMessageType.Text, endOfMessage: false), Encoding.UTF8.GetBytes("he"));
        socket.EnqueueFrame(new WebSocketReceiveResult(3, WebSocketMessageType.Text, endOfMessage: true), Encoding.UTF8.GetBytes("llo"));

        var frame = await ChatWebSocketProtocol.ReceiveAsync(socket, CancellationToken.None);

        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(frame.Value.Payload.Span).Should().Be("hello");
    }

    [Fact]
    public async Task ReceiveAsync_ShouldReturnNull_WhenSocketCloses()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueFrame(new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true), Array.Empty<byte>());

        var frame = await ChatWebSocketProtocol.ReceiveAsync(socket, CancellationToken.None);

        frame.Should().BeNull();
    }

    [Fact]
    public void TryDecodeUtf8_ShouldReturnFalse_ForInvalidBytes()
    {
        var decoded = ChatWebSocketProtocol.TryDecodeUtf8(new byte[] { 0xC3, 0x28 }, out var text);

        decoded.Should().BeFalse();
        text.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_ShouldSkipClosedSocket_AndValidateMessageType()
    {
        var closedSocket = new FakeWebSocket(WebSocketState.Closed);

        await ChatWebSocketProtocol.SendAsync(closedSocket, new { ok = true }, CancellationToken.None);
        closedSocket.SentTexts.Should().BeEmpty();

        var openSocket = new FakeWebSocket(WebSocketState.Open);
        var act = async () => await ChatWebSocketProtocol.SendAsync(
            openSocket,
            new { ok = true },
            CancellationToken.None,
            (WebSocketMessageType)123);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CloseAsync_ShouldCloseOnlyOpenOrCloseReceivedSockets()
    {
        var openSocket = new FakeWebSocket(WebSocketState.Open);
        var closeReceivedSocket = new FakeWebSocket(WebSocketState.CloseReceived);
        var closedSocket = new FakeWebSocket(WebSocketState.Closed);

        await ChatWebSocketProtocol.CloseAsync(openSocket, CancellationToken.None);
        await ChatWebSocketProtocol.CloseAsync(closeReceivedSocket, CancellationToken.None);
        await ChatWebSocketProtocol.CloseAsync(closedSocket, CancellationToken.None);

        openSocket.CloseCalls.Should().Be(1);
        closeReceivedSocket.CloseCalls.Should().Be(1);
        closedSocket.CloseCalls.Should().Be(0);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private readonly ConcurrentQueue<(WebSocketReceiveResult Result, byte[] Bytes)> _frames = new();
        private WebSocketState _state;

        public FakeWebSocket(WebSocketState state)
        {
            _state = state;
        }

        public List<string> SentTexts { get; } = [];
        public int CloseCalls { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void EnqueueFrame(WebSocketReceiveResult result, byte[] bytes) => _frames.Enqueue((result, bytes));

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = closeStatus;
            _ = statusDescription;
            CloseCalls++;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_frames.TryDequeue(out var frame))
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            if (frame.Result.Count > 0 && buffer.Array != null)
                Array.Copy(frame.Bytes, 0, buffer.Array, buffer.Offset, frame.Result.Count);
            return Task.FromResult(frame.Result);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = endOfMessage;
            if (messageType == WebSocketMessageType.Text)
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
