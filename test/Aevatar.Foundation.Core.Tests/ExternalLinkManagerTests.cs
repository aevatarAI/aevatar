using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.ExternalLinks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class ExternalLinkManagerTests
{
    [Fact]
    public async Task StartAsync_WhenInitialConnectFails_ShouldScheduleTypedReconnectSignal()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(dispatch, callbacks, transport);

        await manager.StartAsync([Descriptor()]);

        transport.ConnectCalls.Should().Be(1);
        callbacks.Timeouts.Should().ContainSingle();
        var request = callbacks.Timeouts[0];
        request.ActorId.Should().Be("actor-1");
        request.CallbackId.Should().Be("external-link-reconnect:link-1");
        var signal = request.TriggerEnvelope.Payload.Unpack<ExternalLinkReconnectDueSignal>();
        signal.LinkId.Should().Be("link-1");
        signal.ExpectedAttempt.Should().Be(1);
        dispatch.Payloads.OfType<ExternalLinkReconnectingEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1" && e.Attempt == 1);
    }

    [Fact]
    public async Task HandleAsync_WhenReconnectSignalIsStale_ShouldNotReconnect()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 2,
        }));

        transport.ConnectCalls.Should().Be(1);
        dispatch.Payloads.OfType<ExternalLinkConnectedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenReconnectSignalMatchesActiveAttempt_ShouldReconnectAndResetState()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 1,
        }));

        transport.ConnectCalls.Should().Be(2);
        dispatch.Payloads.OfType<ExternalLinkConnectedEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1");

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 1,
        }));

        transport.ConnectCalls.Should().Be(2);
    }

    private static ExternalLinkManager CreateManager(
        RecordingDispatchPort dispatch,
        RecordingCallbackScheduler callbacks,
        RecordingTransport transport) =>
        new(
            "actor-1",
            dispatch,
            callbacks,
            [new RecordingTransportFactory(transport)],
            NullLogger.Instance);

    private static ExternalLinkDescriptor Descriptor() =>
        new(
            "link-1",
            "recording",
            "memory://link",
            new ExternalLinkOptions
            {
                ReconnectBaseDelay = TimeSpan.FromMilliseconds(100),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectAttempts = 3,
            });

    private static EventEnvelope Envelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("actor-1", TopologyAudience.Self),
        };

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<IMessage> Payloads { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            actorId.Should().Be("actor-1");
            envelope.Payload.Should().NotBeNull();
            Payloads.Add(Unpack(envelope.Payload));
            return Task.CompletedTask;
        }

        private static IMessage Unpack(Any payload)
        {
            if (payload.Is(ExternalLinkConnectedEvent.Descriptor))
                return payload.Unpack<ExternalLinkConnectedEvent>();
            if (payload.Is(ExternalLinkDisconnectedEvent.Descriptor))
                return payload.Unpack<ExternalLinkDisconnectedEvent>();
            if (payload.Is(ExternalLinkReconnectingEvent.Descriptor))
                return payload.Unpack<ExternalLinkReconnectingEvent>();
            if (payload.Is(ExternalLinkErrorEvent.Descriptor))
                return payload.Unpack<ExternalLinkErrorEvent>();
            if (payload.Is(ExternalLinkMessageReceivedEvent.Descriptor))
                return payload.Unpack<ExternalLinkMessageReceivedEvent>();
            if (payload.Is(ExternalLinkReconnectDueSignal.Descriptor))
                return payload.Unpack<ExternalLinkReconnectDueSignal>();
            if (payload.Is(ExternalLinkTransportStateChangedSignal.Descriptor))
                return payload.Unpack<ExternalLinkTransportStateChangedSignal>();

            throw new InvalidOperationException($"Unexpected payload '{payload.TypeUrl}'.");
        }
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingTransportFactory(RecordingTransport transport) : IExternalLinkTransportFactory
    {
        public bool CanCreate(string transportType) => transportType == "recording";
        public IExternalLinkTransport Create() => transport;
    }

    private sealed class RecordingTransport : IExternalLinkTransport
    {
        public int ConnectCalls { get; private set; }
        public int ConnectFailuresRemaining { get; set; }
        public string TransportType => "recording";
        public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? OnMessageReceived { private get; set; }
        public Func<ExternalLinkStateChange, string?, CancellationToken, Task>? OnStateChanged { private get; set; }

        public Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct)
        {
            ConnectCalls++;
            if (ConnectFailuresRemaining > 0)
            {
                ConnectFailuresRemaining--;
                throw new InvalidOperationException("connect-failed");
            }

            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
