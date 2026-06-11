using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.ExternalLinks;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.ExternalLinks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

public sealed class ExternalLinkManagerTests
{
    [Fact]
    public void CanHandle_ShouldRecognizeOnlyExternalLinkInternalSignals()
    {
        var manager = CreateManager(new RecordingDispatchPort(), new RecordingCallbackScheduler(), new RecordingTransport());

        manager.CanHandle(Envelope(new ExternalLinkReconnectDueSignal())).Should().BeTrue();
        manager.CanHandle(Envelope(new ExternalLinkMessageReceivedSignal())).Should().BeTrue();
        manager.CanHandle(Envelope(new ExternalLinkTransportStateChangedSignal())).Should().BeTrue();
        manager.CanHandle(new EventEnvelope()).Should().BeFalse();
        manager.CanHandle(Envelope(new ExternalLinkConnectedEvent())).Should().BeFalse();
    }

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
    public async Task TransportCallback_WhenStateChanges_ShouldDispatchTypedSelfSignal()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport();
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        dispatch.Payloads.Clear();

        await transport.EmitStateChangedAsync(
            ExternalLinkStateChange.Disconnected,
            "socket-lost",
            CancellationToken.None);

        dispatch.Payloads.OfType<ExternalLinkTransportStateChangedSignal>()
            .Should().ContainSingle(signal =>
                signal.LinkId == "link-1" &&
                signal.State == ExternalLinkTransportStateSignalKind.Disconnected &&
                signal.Reason == "socket-lost");
        dispatch.Payloads.OfType<ExternalLinkDisconnectedEvent>().Should().BeEmpty();
        callbacks.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public void ExternalLinkManagerSource_ShouldNotUseBackgroundReconnectLoopPrimitives()
    {
        var sourcePath = FindRepositoryFile("src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs");

        var source = File.ReadAllText(sourcePath);

        source.Should().NotContain("Task." + "Run(");
        source.Should().NotContain("Task." + "Delay(");
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
        callbacks.CancelledLeases.Should().ContainSingle(lease =>
            lease.ActorId == "actor-1" &&
            lease.CallbackId == "external-link-reconnect:link-1" &&
            lease.Generation == 1);

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 1,
        }));

        transport.ConnectCalls.Should().Be(2);
    }

    [Fact]
    public async Task DisconnectAsync_WhenReconnectIsScheduled_ShouldCancelReconnectLease()
    {
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(new RecordingDispatchPort(), callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        callbacks.Timeouts.Should().ContainSingle();

        await manager.DisconnectAsync("link-1");

        callbacks.CancelledLeases.Should().ContainSingle(lease =>
            lease.ActorId == "actor-1" &&
            lease.CallbackId == "external-link-reconnect:link-1" &&
            lease.Generation == 1);
    }

    [Fact]
    public async Task DisposeAsync_WhenReconnectIsScheduled_ShouldCancelReconnectLease()
    {
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(new RecordingDispatchPort(), callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        callbacks.Timeouts.Should().ContainSingle();

        await manager.DisposeAsync();

        callbacks.CancelledLeases.Should().ContainSingle(lease =>
            lease.ActorId == "actor-1" &&
            lease.CallbackId == "external-link-reconnect:link-1" &&
            lease.Generation == 1);
    }

    [Fact]
    public async Task HandleAsync_WhenReconnectAttemptFails_ShouldPublishAndScheduleNextAttempt()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 2 };
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        callbacks.Timeouts.Should().ContainSingle();
        dispatch.Payloads.Clear();

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 1,
        }));

        transport.ConnectCalls.Should().Be(2);
        callbacks.Timeouts.Should().HaveCount(2);
        var nextRequest = callbacks.Timeouts[1];
        nextRequest.CallbackId.Should().Be("external-link-reconnect:link-1");
        var nextSignal = nextRequest.TriggerEnvelope.Payload.Unpack<ExternalLinkReconnectDueSignal>();
        nextSignal.LinkId.Should().Be("link-1");
        nextSignal.ExpectedAttempt.Should().Be(2);
        dispatch.Payloads.OfType<ExternalLinkReconnectingEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1" && e.Attempt == 2);
    }

    [Fact]
    public async Task HandleAsync_WhenReconnectAttemptExceedsMaximum_ShouldPublishTerminalDisconnectedWithoutScheduling()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 4 };
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor(maxReconnectAttempts: 1)]);
        callbacks.Timeouts.Should().ContainSingle();
        dispatch.Payloads.Clear();

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 1,
        }));

        transport.ConnectCalls.Should().Be(2);
        callbacks.Timeouts.Should().ContainSingle();
        dispatch.Payloads.OfType<ExternalLinkDisconnectedEvent>()
            .Should().ContainSingle(e =>
                e.LinkId == "link-1" &&
                e.Reason == "max reconnect attempts reached" &&
                !e.WillReconnect &&
                e.ReconnectAttempt == 2);
        dispatch.Payloads.OfType<ExternalLinkReconnectingEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenTransportDisconnects_ShouldPublishDisconnectedAndScheduleReconnect()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport();
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        dispatch.Payloads.Clear();

        await manager.HandleAsync(Envelope(new ExternalLinkTransportStateChangedSignal
        {
            LinkId = "link-1",
            State = ExternalLinkTransportStateSignalKind.Disconnected,
            Reason = "socket-lost",
        }));

        dispatch.Payloads.OfType<ExternalLinkDisconnectedEvent>()
            .Should().ContainSingle(e =>
                e.LinkId == "link-1" &&
                e.Reason == "socket-lost" &&
                e.WillReconnect);
        dispatch.Payloads.OfType<ExternalLinkReconnectingEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1" && e.Attempt == 1);
        callbacks.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WhenTransportConnectsAfterScheduledReconnect_ShouldCancelLeaseAndIgnoreStaleReconnect()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        callbacks.Timeouts.Should().ContainSingle();
        dispatch.Payloads.Clear();

        await manager.HandleAsync(Envelope(new ExternalLinkTransportStateChangedSignal
        {
            LinkId = "link-1",
            State = ExternalLinkTransportStateSignalKind.Connected,
        }));

        callbacks.CancelledLeases.Should().ContainSingle(lease =>
            lease.ActorId == "actor-1" &&
            lease.CallbackId == "external-link-reconnect:link-1" &&
            lease.Generation == 1);
        dispatch.Payloads.OfType<ExternalLinkConnectedEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1");

        await manager.HandleAsync(Envelope(new ExternalLinkReconnectDueSignal
        {
            LinkId = "link-1",
            ExpectedAttempt = 1,
        }));

        transport.ConnectCalls.Should().Be(1);
        dispatch.Payloads.OfType<ExternalLinkConnectedEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1");
    }

    [Fact]
    public async Task HandleAsync_WhenTransportErrors_ShouldPublishExternalLinkErrorEvent()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport();
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        dispatch.Payloads.Clear();

        await manager.HandleAsync(Envelope(new ExternalLinkTransportStateChangedSignal
        {
            LinkId = "link-1",
            State = ExternalLinkTransportStateSignalKind.Error,
            Reason = "socket-error",
        }));

        dispatch.Payloads.OfType<ExternalLinkErrorEvent>()
            .Should().ContainSingle(e =>
                e.LinkId == "link-1" &&
                e.ErrorMessage == "socket-error");
    }

    [Fact]
    public async Task HandleAsync_WhenTransportCloses_ShouldPublishDisconnectedWithoutReconnect()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var manager = CreateManager(dispatch, callbacks, transport);
        await manager.StartAsync([Descriptor()]);
        callbacks.Timeouts.Should().ContainSingle();
        dispatch.Payloads.Clear();

        await manager.HandleAsync(Envelope(new ExternalLinkTransportStateChangedSignal
        {
            LinkId = "link-1",
            State = ExternalLinkTransportStateSignalKind.Closed,
            Reason = "remote-closed",
        }));

        dispatch.Payloads.OfType<ExternalLinkDisconnectedEvent>()
            .Should().ContainSingle(e =>
                e.LinkId == "link-1" &&
                e.Reason == "remote-closed" &&
                !e.WillReconnect);
        dispatch.Payloads.OfType<ExternalLinkReconnectingEvent>().Should().BeEmpty();
        callbacks.CancelledLeases.Should().ContainSingle(lease =>
            lease.ActorId == "actor-1" &&
            lease.CallbackId == "external-link-reconnect:link-1" &&
            lease.Generation == 1);
    }

    [Fact]
    public async Task GAgentBaseHandleEventAsync_WhenExternalLinkSignalArrives_ShouldBypassRegularPipeline()
    {
        var dispatch = new RecordingDispatchPort();
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport();
        var module = new TrackingModule();
        var agent = new ExternalLinkAwareAgent();
        agent.SetId("actor-1");
        agent.Services = TestRuntimeServices.BuildProvider(services =>
        {
            services.AddSingleton<IActorDispatchPort>(dispatch);
            services.AddSingleton<IActorRuntimeCallbackScheduler>(callbacks);
            services.AddSingleton<IExternalLinkTransportFactory>(new RecordingTransportFactory(transport));
        });
        agent.RegisterModule(module);
        await agent.ActivateAsync();
        dispatch.Payloads.Clear();

        await agent.HandleEventAsync(Envelope(new ExternalLinkTransportStateChangedSignal
        {
            LinkId = "link-1",
            State = ExternalLinkTransportStateSignalKind.Disconnected,
            Reason = "socket-lost",
        }));

        agent.AllEventHandlerCalls.Should().Be(0);
        module.InvocationCount.Should().Be(0);
        dispatch.Payloads.OfType<ExternalLinkDisconnectedEvent>()
            .Should().ContainSingle(e => e.LinkId == "link-1" && e.WillReconnect);
        callbacks.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task GAgentBaseActivateAsync_WhenCallbackSchedulerIsMissing_ShouldDisableExternalLinksWithoutThrowing()
    {
        var dispatch = new RecordingDispatchPort();
        var transport = new RecordingTransport();
        var agent = new ExternalLinkAwareAgent();
        agent.SetId("actor-1");
        var services = new ServiceCollection();
        services.AddSingleton<IActorDispatchPort>(dispatch);
        services.AddSingleton<IExternalLinkTransportFactory>(new RecordingTransportFactory(transport));
        agent.Services = services.BuildServiceProvider();

        var act = () => agent.ActivateAsync();

        await act.Should().NotThrowAsync();
        transport.ConnectCalls.Should().Be(0);
        transport.HasStateChangedHandler.Should().BeFalse();
        transport.HasMessageReceivedHandler.Should().BeFalse();
        dispatch.Payloads.Should().BeEmpty();
    }

    [Fact]
    public async Task GAgentBaseDeactivateAsync_WhenReconnectIsScheduled_ShouldCancelReconnectLease()
    {
        var callbacks = new RecordingCallbackScheduler();
        var transport = new RecordingTransport { ConnectFailuresRemaining = 1 };
        var agent = new ExternalLinkAwareAgent();
        agent.SetId("actor-1");
        agent.Services = TestRuntimeServices.BuildProvider(services =>
        {
            services.AddSingleton<IActorDispatchPort>(new RecordingDispatchPort());
            services.AddSingleton<IActorRuntimeCallbackScheduler>(callbacks);
            services.AddSingleton<IExternalLinkTransportFactory>(new RecordingTransportFactory(transport));
        });
        await agent.ActivateAsync();
        callbacks.Timeouts.Should().ContainSingle();

        await agent.DeactivateAsync();

        callbacks.CancelledLeases.Should().ContainSingle(lease =>
            lease.ActorId == "actor-1" &&
            lease.CallbackId == "external-link-reconnect:link-1" &&
            lease.Generation == 1);
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

    private static ExternalLinkDescriptor Descriptor(int maxReconnectAttempts = 3) =>
        new(
            "link-1",
            "recording",
            "memory://link",
            new ExternalLinkOptions
            {
                ReconnectBaseDelay = TimeSpan.FromMilliseconds(100),
                ReconnectMaxDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectAttempts = maxReconnectAttempts,
            });

    private static EventEnvelope Envelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("actor-1", TopologyAudience.Self),
        };

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<IMessage> Payloads { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            actorId.Should().Be("actor-1");
            envelope.Payload.Should().NotBeNull();
            Payloads.Add(Unpack(envelope.Payload));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
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
        public List<RuntimeCallbackLease> CancelledLeases { get; } = [];

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

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
        {
            CancelledLeases.Add(lease);
            return Task.CompletedTask;
        }

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
        public IExternalLinkSignalSink? SignalSink { private get; set; }
        public bool HasMessageReceivedHandler => SignalSink != null;
        public bool HasStateChangedHandler => SignalSink != null;

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

        public Task EmitStateChangedAsync(
            ExternalLinkStateChange state,
            string? reason,
            CancellationToken ct)
        {
            SignalSink.Should().NotBeNull();
            return SignalSink.PublishStateChangedAsync(
                new ExternalLinkTransportStateChangedSignal
                {
                    State = state switch
                    {
                        ExternalLinkStateChange.Connected => ExternalLinkTransportStateSignalKind.Connected,
                        ExternalLinkStateChange.Disconnected => ExternalLinkTransportStateSignalKind.Disconnected,
                        ExternalLinkStateChange.Error => ExternalLinkTransportStateSignalKind.Error,
                        ExternalLinkStateChange.Closed => ExternalLinkTransportStateSignalKind.Closed,
                        _ => ExternalLinkTransportStateSignalKind.Unspecified,
                    },
                    Reason = reason ?? string.Empty,
                },
                ct);
        }
    }

    private sealed class TrackingModule : IEventModule<IEventHandlerContext>
    {
        public string Name => "tracking";
        public int Priority => 0;
        public int InvocationCount { get; private set; }
        public bool CanHandle(EventEnvelope envelope) => true;

        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
        {
            InvocationCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ExternalLinkAwareAgent : GAgentBase, IExternalLinkAware
    {
        public int AllEventHandlerCalls { get; private set; }

        public IReadOnlyList<ExternalLinkDescriptor> GetLinkDescriptors() => [Descriptor()];

        [AllEventHandler(AllowSelfHandling = true)]
        public Task HandleAny(EventEnvelope envelope)
        {
            AllEventHandlerCalls++;
            return Task.CompletedTask;
        }
    }
}
