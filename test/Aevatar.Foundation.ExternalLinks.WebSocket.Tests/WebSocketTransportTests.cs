using NetWebSocket = System.Net.WebSockets.WebSocket;
using System.Net.WebSockets;
using System.Reflection;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.Foundation.ExternalLinks.WebSocket.Tests;

public sealed class WebSocketTransportTests
{
    [Fact]
    public async Task NotifyStateChangedAsync_ShouldMapStateKindsToTypedSignals()
    {
        await using var transport = new WebSocketTransport(NullLogger<WebSocketTransport>.Instance);
        var sink = new RecordingExternalLinkSignalSink();
        transport.SignalSink = sink;

        transport.TransportType.Should().Be("websocket");
        await InvokeNotifyStateChangedAsync(transport, ExternalLinkStateChange.Connected, null);
        await InvokeNotifyStateChangedAsync(transport, ExternalLinkStateChange.Error, "boom");
        await InvokeNotifyStateChangedAsync(transport, ExternalLinkStateChange.Closed, "closed");
        await InvokeNotifyStateChangedAsync(transport, (ExternalLinkStateChange)999, "unknown");

        sink.StateSignals.Select(x => (x.State, x.Reason)).Should().Equal(
            (ExternalLinkTransportStateSignalKind.Connected, string.Empty),
            (ExternalLinkTransportStateSignalKind.Error, "boom"),
            (ExternalLinkTransportStateSignalKind.Closed, "closed"),
            (ExternalLinkTransportStateSignalKind.Unspecified, "unknown"));
    }

    [Fact]
    public async Task SendAsync_WhenConnected_ShouldSendBinaryPayload()
    {
        var payload = new byte[] { 9, 8, 7 };
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = CreateServer(async webSocket =>
        {
            var buffer = new byte[16];
            var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
            received.TrySetResult(buffer[..result.Count]);
        });
        await using var transport = new WebSocketTransport(NullLogger<WebSocketTransport>.Instance)
        {
            SignalSink = new RecordingExternalLinkSignalSink(),
        };

        await transport.ConnectAsync(
            new ExternalLinkDescriptor("link-1", "websocket", server.BaseAddress),
            CancellationToken.None);
        await transport.SendAsync(payload, CancellationToken.None);

        var sent = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sent.Should().Equal(payload);
        await transport.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_WhenNotConnected_ShouldThrow()
    {
        await using var transport = new WebSocketTransport(NullLogger<WebSocketTransport>.Instance);

        var act = () => transport.SendAsync(new byte[] { 1 }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("WebSocket is not connected.");
    }

    [Fact]
    public async Task ReceiveLoop_WhenMessageArrives_ShouldPublishTypedMessageSignal()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        using var server = CreateServer(async webSocket =>
        {
            await webSocket.SendAsync(
                payload,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                CancellationToken.None);
        });
        var sink = new RecordingExternalLinkSignalSink();
        await using var transport = new WebSocketTransport(NullLogger<WebSocketTransport>.Instance)
        {
            SignalSink = sink,
        };

        await transport.ConnectAsync(
            new ExternalLinkDescriptor("link-1", "websocket", server.BaseAddress),
            CancellationToken.None);
        await sink.WaitForMessagesAsync(1);

        var signal = sink.MessageSignals.Should().ContainSingle().Subject;
        signal.LinkId.Should().BeEmpty();
        signal.RawPayload.ToByteArray().Should().Equal(payload);
        signal.ReceivedAt.Should().NotBeNull();
        await transport.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReceiveLoop_WhenRemoteCloses_ShouldPublishTypedStateSignal()
    {
        using var server = CreateServer(async webSocket =>
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "server done",
                CancellationToken.None);
        });
        var sink = new RecordingExternalLinkSignalSink();
        await using var transport = new WebSocketTransport(NullLogger<WebSocketTransport>.Instance)
        {
            SignalSink = sink,
        };

        await transport.ConnectAsync(
            new ExternalLinkDescriptor("link-1", "websocket", server.BaseAddress),
            CancellationToken.None);
        await sink.WaitForStatesAsync(1);

        var signal = sink.StateSignals.Should().ContainSingle().Subject;
        signal.LinkId.Should().BeEmpty();
        signal.State.Should().Be(ExternalLinkTransportStateSignalKind.Disconnected);
        signal.Reason.Should().Be("server done");
        await transport.DisconnectAsync(CancellationToken.None);
    }

    private static WebSocketTestServer CreateServer(Func<NetWebSocket, Task> handleWebSocketAsync) =>
        WebSocketTestServer.Start(handleWebSocketAsync);

    private static async Task InvokeNotifyStateChangedAsync(
        WebSocketTransport transport,
        ExternalLinkStateChange state,
        string? reason)
    {
        var method = typeof(WebSocketTransport).GetMethod(
            "NotifyStateChangedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var task = (Task)method!.Invoke(
            transport,
            [state, reason, CancellationToken.None])!;
        await task;
    }

    private sealed class WebSocketTestServer : IDisposable
    {
        private readonly IHost _host;

        private WebSocketTestServer(IHost host, string baseAddress)
        {
            _host = host;
            BaseAddress = baseAddress;
        }

        public string BaseAddress { get; }

        public static WebSocketTestServer Start(Func<NetWebSocket, Task> handleWebSocketAsync)
        {
            var host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseKestrel();
                    webBuilder.UseUrls("http://127.0.0.1:0");
                    webBuilder.Configure(app =>
                    {
                        app.UseWebSockets();
                        app.Run(async context =>
                        {
                            if (!context.WebSockets.IsWebSocketRequest)
                            {
                                context.Response.StatusCode = 400;
                                return;
                            }

                            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                            await handleWebSocketAsync(webSocket);
                        });
                    });
                })
                .ConfigureServices(services => services.AddLogging())
                .Build();

            host.Start();
            var address = host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            var uri = new UriBuilder(address)
            {
                Scheme = "ws",
            };
            return new WebSocketTestServer(host, uri.Uri.ToString());
        }

        public void Dispose() => _host.Dispose();
    }

    private sealed class RecordingExternalLinkSignalSink : IExternalLinkSignalSink
    {
        private readonly TaskCompletionSource _messagePublished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _statePublished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ExternalLinkMessageReceivedSignal> MessageSignals { get; } = [];
        public List<ExternalLinkTransportStateChangedSignal> StateSignals { get; } = [];

        public Task PublishMessageReceivedAsync(ExternalLinkMessageReceivedSignal signal, CancellationToken ct)
        {
            MessageSignals.Add(signal);
            _messagePublished.TrySetResult();
            return Task.CompletedTask;
        }

        public Task PublishStateChangedAsync(ExternalLinkTransportStateChangedSignal signal, CancellationToken ct)
        {
            StateSignals.Add(signal);
            _statePublished.TrySetResult();
            return Task.CompletedTask;
        }

        public async Task WaitForMessagesAsync(int count)
        {
            if (MessageSignals.Count < count)
                await _messagePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task WaitForStatesAsync(int count)
        {
            if (StateSignals.Count < count)
                await _statePublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
