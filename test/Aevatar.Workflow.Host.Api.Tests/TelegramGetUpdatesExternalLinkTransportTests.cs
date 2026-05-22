using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Workflow.Extensions.Bridge;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class TelegramGetUpdatesExternalLinkTransportTests
{
    [Fact]
    public async Task SendAsync_WhenConnectorSucceeds_ShouldMapRequestAndPublishResult()
    {
        var connector = new RecordingConnector(new ConnectorResponse
        {
            Success = true,
            Output = """{"ok":true,"result":[]}""",
        });
        var registry = new InMemoryConnectorRegistry();
        registry.Register(connector);
        var transport = CreateTransport(registry);
        var received = new List<TelegramGetUpdatesResult>();
        transport.OnMessageReceived = (payload, ct) =>
        {
            received.Add(TelegramGetUpdatesResult.Parser.ParseFrom(payload.Span));
            return Task.CompletedTask;
        };
        var request = BuildRequest();

        await transport.SendAsync(request.ToByteArray(), CancellationToken.None);

        connector.Received.Should().ContainSingle();
        connector.Received[0].RunId.Should().Be("cmd-1");
        connector.Received[0].StepId.Should().Be("session-1");
        connector.Received[0].Connector.Should().Be("telegram");
        connector.Received[0].Operation.Should().Be("/getUpdates");
        var payload = JsonDocument.Parse(connector.Received[0].Payload).RootElement;
        payload.GetProperty("timeout").GetInt32().Should().Be(1);
        payload.GetProperty("offset").GetInt64().Should().Be(42);
        payload.GetProperty("allowed_updates").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Equal("message", "channel_post");
        connector.Received[0].Parameters["method"].Should().Be("POST");
        connector.Received[0].Parameters["content_type"].Should().Be("application/json");
        connector.Received[0].Parameters["timeout_ms"].Should().Be("4000");
        received.Should().ContainSingle();
        received[0].CommandId.Should().Be("cmd-1");
        received[0].Generation.Should().Be(7);
        received[0].RequestId.Should().Be("request-1");
        received[0].Success.Should().BeTrue();
        received[0].Output.Should().Be("""{"ok":true,"result":[]}""");
        received[0].RequestedOffset.Should().Be(42);
    }

    [Fact]
    public async Task SendAsync_WhenConnectorMissing_ShouldPublishFailureResult()
    {
        var transport = CreateTransport(new InMemoryConnectorRegistry());
        var received = new List<TelegramGetUpdatesResult>();
        transport.OnMessageReceived = (payload, ct) =>
        {
            received.Add(TelegramGetUpdatesResult.Parser.ParseFrom(payload.Span));
            return Task.CompletedTask;
        };

        await transport.SendAsync(BuildRequest().ToByteArray(), CancellationToken.None);

        var result = received.Should().ContainSingle().Subject;
        result.Success.Should().BeFalse();
        result.Error.Should().Be("telegram connector 'telegram' not found");
    }

    [Fact]
    public async Task SendAsync_WhenConnectorThrowsSynchronously_ShouldPublishFailureResult()
    {
        var registry = new InMemoryConnectorRegistry();
        registry.Register(new ThrowingConnector(new InvalidOperationException("sync broke")));
        var transport = CreateTransport(registry);
        var received = new List<TelegramGetUpdatesResult>();
        transport.OnMessageReceived = (payload, ct) =>
        {
            received.Add(TelegramGetUpdatesResult.Parser.ParseFrom(payload.Span));
            return Task.CompletedTask;
        };

        await transport.SendAsync(BuildRequest().ToByteArray(), CancellationToken.None);

        var result = received.Should().ContainSingle().Subject;
        result.Success.Should().BeFalse();
        result.Error.Should().Be("telegram getUpdates execution failed: sync broke");
    }

    [Fact]
    public async Task SendAsync_WhenConnectorFaultsAsynchronously_ShouldPublishFailureResult()
    {
        var registry = new InMemoryConnectorRegistry();
        registry.Register(new FaultingConnector(new InvalidOperationException("async broke")));
        var transport = CreateTransport(registry);
        var received = new List<TelegramGetUpdatesResult>();
        transport.OnMessageReceived = (payload, ct) =>
        {
            received.Add(TelegramGetUpdatesResult.Parser.ParseFrom(payload.Span));
            return Task.CompletedTask;
        };

        await transport.SendAsync(BuildRequest().ToByteArray(), CancellationToken.None);

        var result = received.Should().ContainSingle().Subject;
        result.Success.Should().BeFalse();
        result.Error.Should().Be("telegram getUpdates execution failed: async broke");
    }

    [Fact]
    public async Task ConnectAndDisconnect_ShouldPublishStateCallbacks()
    {
        var transport = CreateTransport(new InMemoryConnectorRegistry());
        var states = new List<(ExternalLinkStateChange State, string? Reason)>();
        transport.OnStateChanged = (state, reason, ct) =>
        {
            states.Add((state, reason));
            return Task.CompletedTask;
        };

        await transport.ConnectAsync(
            new ExternalLinkDescriptor("telegram-get-updates", "telegram-get-updates", "telegram://get-updates"),
            CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);

        states.Should().Equal(
            (ExternalLinkStateChange.Connected, null),
            (ExternalLinkStateChange.Closed, "closed"));
    }

    [Fact]
    public void Factory_ShouldMatchTransportTypeCaseInsensitivelyAndCreateTransport()
    {
        var factory = new TelegramGetUpdatesExternalLinkTransportFactory(
            new InMemoryConnectorRegistry(),
            NullLogger<TelegramGetUpdatesExternalLinkTransport>.Instance);

        factory.CanCreate("telegram-get-updates").Should().BeTrue();
        factory.CanCreate("TELEGRAM-GET-UPDATES").Should().BeTrue();
        factory.CanCreate("websocket").Should().BeFalse();
        factory.Create().Should().BeOfType<TelegramGetUpdatesExternalLinkTransport>();
    }

    private static TelegramGetUpdatesExternalLinkTransport CreateTransport(IConnectorRegistry registry) =>
        new(registry, NullLogger<TelegramGetUpdatesExternalLinkTransport>.Instance);

    private static TelegramGetUpdatesRequest BuildRequest()
    {
        var request = new TelegramGetUpdatesRequest
        {
            CommandId = "cmd-1",
            Generation = 7,
            RequestId = "request-1",
            ConnectorName = "telegram",
            RunId = "cmd-1",
            StepId = "session-1",
            PollTimeoutSeconds = 1,
            PerCallTimeoutMs = 4000,
            HttpMethod = "POST",
            ContentType = "application/json",
            Bootstrap = true,
            RequestedOffset = 42,
        };
        request.AllowedUpdates.Add("message");
        request.AllowedUpdates.Add("channel_post");
        return request;
    }

    private sealed class RecordingConnector(params ConnectorResponse[] responses) : IConnector
    {
        private int _responseIndex;

        public List<ConnectorRequest> Received { get; } = [];
        public string Name { get; } = "telegram";
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            Received.Add(request);
            var index = Math.Min(_responseIndex, responses.Length - 1);
            _responseIndex++;
            return Task.FromResult(responses[index]);
        }
    }

    private sealed class ThrowingConnector(Exception exception) : IConnector
    {
        public string Name { get; } = "telegram";
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            throw exception;
        }
    }

    private sealed class FaultingConnector(Exception exception) : IConnector
    {
        public string Name { get; } = "telegram";
        public string Type { get; } = "http";

        public Task<ConnectorResponse> ExecuteAsync(ConnectorRequest request, CancellationToken ct = default)
        {
            _ = request;
            _ = ct;
            return Task.FromException<ConnectorResponse>(exception);
        }
    }

    private sealed class InMemoryConnectorRegistry : IConnectorRegistry
    {
        private readonly Dictionary<string, IConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);

        public void Register(IConnector connector) => _connectors[connector.Name] = connector;
        public bool TryGet(string name, out IConnector? connector) => _connectors.TryGetValue(name, out connector);
        public IReadOnlyList<string> ListNames() => _connectors.Keys.ToList();
    }
}
