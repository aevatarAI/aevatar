using System.Net.WebSockets;
using System.Text;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Transport;
using Google.Protobuf;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class WebSocketVoiceTransportTests
{
    [Fact]
    public async Task Send_methods_should_emit_binary_audio_and_text_control()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        await using var transport = new WebSocketVoiceTransport(socket);

        await transport.SendAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        await transport.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await transport.SendControlAsync(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 7,
                PlayoutSequence = 42,
            },
        }, CancellationToken.None);

        socket.SentBinaries.Count.ShouldBe(1);
        socket.SentBinaries[0].ShouldBe([1, 2, 3]);
        socket.SentTexts.Count.ShouldBe(1);

        var parsed = JsonParser.Default.Parse<VoiceControlFrame>(socket.SentTexts[0]);
        parsed.DrainAcknowledged.ResponseId.ShouldBe(7);
        parsed.DrainAcknowledged.PlayoutSequence.ShouldBe(42);
    }

    [Fact]
    public async Task ReceiveFramesAsync_should_yield_audio_and_control_and_ignore_invalid_text()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        socket.EnqueueReceive(WebSocketMessageType.Binary, [10, 20, 30]);
        socket.EnqueueReceive(
            WebSocketMessageType.Text,
            Encoding.UTF8.GetBytes(JsonFormatter.Default.Format(new VoiceControlFrame
            {
                DrainAcknowledged = new VoiceDrainAcknowledged
                {
                    ResponseId = 9,
                    PlayoutSequence = 88,
                },
            })));
        socket.EnqueueReceive(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("{not-valid-json"));

        await using var transport = new WebSocketVoiceTransport(socket);
        var frames = new List<VoiceTransportFrame>();

        await foreach (var frame in transport.ReceiveFramesAsync(CancellationToken.None))
            frames.Add(frame);

        frames.Count.ShouldBe(2);
        frames[0].IsAudio.ShouldBeTrue();
        frames[0].AudioPcm16.ToArray().ShouldBe([10, 20, 30]);
        frames[1].IsAudio.ShouldBeFalse();
        frames[1].Control.ShouldNotBeNull();
        frames[1].Control!.DrainAcknowledged.ResponseId.ShouldBe(9);
    }

    [Fact]
    public async Task ReceiveFramesAsync_should_reassemble_fragmented_binary_messages()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var bytes = Enumerable.Range(0, 9000).Select(i => (byte)(i % 251)).ToArray();
        socket.EnqueueReceive(WebSocketMessageType.Binary, bytes[..8192], endOfMessage: false);
        socket.EnqueueReceive(WebSocketMessageType.Binary, bytes[8192..], endOfMessage: true);

        await using var transport = new WebSocketVoiceTransport(socket);
        var frames = new List<VoiceTransportFrame>();

        await foreach (var frame in transport.ReceiveFramesAsync(CancellationToken.None))
            frames.Add(frame);

        frames.Count.ShouldBe(1);
        frames[0].IsAudio.ShouldBeTrue();
        frames[0].AudioPcm16.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task ReceiveFramesAsync_should_stop_when_websocket_throws()
    {
        var socket = new FakeWebSocket(WebSocketState.Open)
        {
            ThrowOnReceive = true,
        };

        await using var transport = new WebSocketVoiceTransport(socket);
        var count = 0;

        await foreach (var _ in transport.ReceiveFramesAsync(CancellationToken.None))
            count++;

        count.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_should_close_open_socket_and_swallow_close_failures()
    {
        var openSocket = new FakeWebSocket(WebSocketState.Open);
        await using (var transport = new WebSocketVoiceTransport(openSocket))
        {
        }

        openSocket.CloseCalls.ShouldBe(1);
        openSocket.Disposed.ShouldBeTrue();

        var failingSocket = new FakeWebSocket(WebSocketState.Open)
        {
            ThrowOnClose = true,
        };

        await using (var transport = new WebSocketVoiceTransport(failingSocket))
        {
        }

        failingSocket.CloseCalls.ShouldBe(1);
        failingSocket.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Send_methods_should_throw_after_dispose()
    {
        var socket = new FakeWebSocket(WebSocketState.Open);
        var transport = new WebSocketVoiceTransport(socket);
        await transport.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            transport.SendAudioAsync(new byte[] { 1 }, CancellationToken.None));
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            transport.SendControlAsync(new VoiceControlFrame(), CancellationToken.None));
    }
}
