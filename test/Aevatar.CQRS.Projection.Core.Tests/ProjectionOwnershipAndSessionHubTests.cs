using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ActorProjectionOwnershipCoordinatorTests
{
    [Fact]
    public async Task AcquireAsync_ShouldCreateCoordinatorActor_AndDispatchAcquireEvent()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var coordinator = CreateCoordinator(runtime);

        await coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        runtime.CreateCallCount.Should().Be(1);
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var actor = runtime.GetOwnershipActor(actorId);
        actor.HandledEnvelopes.Should().ContainSingle();

        var envelope = actor.HandledEnvelopes.Single();
        var evt = envelope.Payload.Unpack<ProjectionOwnershipAcquireEvent>();
        evt.ScopeId.Should().Be("scope-1");
        evt.SessionId.Should().Be("session-1");
        evt.LeaseTtlMs.Should().Be(ProjectionOwnershipCoordinatorOptions.DefaultLeaseTtlMs);
        evt.OccurredAtUtc.Should().NotBeNull();
        envelope.Propagation!.CorrelationId.Should().Be("session-1");
        envelope.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
    }

    [Fact]
    public async Task AcquireAsync_ShouldUseConfiguredLeaseTtl()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var coordinator = CreateCoordinator(
            runtime,
            new ProjectionOwnershipCoordinatorOptions
            {
                LeaseTtlMs = 45_000,
            });

        await coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var actor = runtime.GetOwnershipActor(actorId);
        var envelope = actor.HandledEnvelopes.Single();
        var evt = envelope.Payload.Unpack<ProjectionOwnershipAcquireEvent>();
        evt.LeaseTtlMs.Should().Be(45_000);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldReuseExistingCoordinatorActor()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var actor = new RuntimeActor(actorId, new ProjectionOwnershipCoordinatorGAgent());
        runtime.SetActor(actorId, actor);
        var coordinator = CreateCoordinator(runtime);

        await coordinator.ReleaseAsync("scope-1", "session-1", CancellationToken.None);

        runtime.CreateCallCount.Should().Be(0);
        actor.HandledEnvelopes.Should().ContainSingle();
        var envelope = actor.HandledEnvelopes.Single();
        var evt = envelope.Payload.Unpack<ProjectionOwnershipReleaseEvent>();
        evt.ScopeId.Should().Be("scope-1");
        evt.SessionId.Should().Be("session-1");
        evt.OccurredAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task HasActiveLeaseAsync_ShouldReturnTrue_WhenLeaseMatchesAndNotExpired()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var store = new TestInMemoryEventStore();
        var coordinator = CreateCoordinator(runtime, eventStore: store);
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");

        await AppendStateEventAsync(
            store,
            actorId,
            1,
            new ProjectionOwnershipAcquireEvent
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                LeaseTtlMs = 60_000,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        var hasActiveLease = await coordinator.HasActiveLeaseAsync("scope-1", "session-1", CancellationToken.None);

        hasActiveLease.Should().BeTrue();
    }

    [Fact]
    public async Task HasActiveLeaseAsync_ShouldReturnFalse_WhenLeaseExpired()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var store = new TestInMemoryEventStore();
        var coordinator = CreateCoordinator(runtime, eventStore: store);
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");

        await AppendStateEventAsync(
            store,
            actorId,
            1,
            new ProjectionOwnershipAcquireEvent
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                LeaseTtlMs = 1_000,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-5)),
            });

        var hasActiveLease = await coordinator.HasActiveLeaseAsync("scope-1", "session-1", CancellationToken.None);

        hasActiveLease.Should().BeFalse();
    }

    [Fact]
    public async Task HasActiveLeaseAsync_ShouldReturnFalse_WhenSessionDoesNotMatch()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var store = new TestInMemoryEventStore();
        var coordinator = CreateCoordinator(runtime, eventStore: store);
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");

        await AppendStateEventAsync(
            store,
            actorId,
            1,
            new ProjectionOwnershipAcquireEvent
            {
                ScopeId = "scope-1",
                SessionId = "session-1",
                LeaseTtlMs = 60_000,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });

        var hasActiveLease = await coordinator.HasActiveLeaseAsync("scope-1", "session-2", CancellationToken.None);

        hasActiveLease.Should().BeFalse();
    }

    [Fact]
    public async Task AcquireAsync_ShouldRecover_WhenCreateRaces()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var racedActor = new RuntimeActor(actorId, new ProjectionOwnershipCoordinatorGAgent());
        runtime.SetCreateRaceActor(actorId, racedActor);
        var coordinator = CreateCoordinator(runtime);

        await coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        runtime.CreateCallCount.Should().Be(1);
        racedActor.HandledEnvelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task AcquireAsync_ShouldThrow_WhenResolvedActorTypeIsInvalid()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        runtime.SetActor(actorId, new RuntimeActor(actorId, new PlainTestAgent("agent-1")));
        var coordinator = CreateCoordinator(runtime);

        Func<Task> act = () => coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AcquireAsync_ShouldThrow_WhenRuntimeTypeCannotBeProven()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        runtime.SetActor(actorId, new RuntimeActor(actorId, new PlainTestAgent("agent-1")));
        var coordinator = new ActorProjectionOwnershipCoordinator(
            runtime,
            runtime,
            new DefaultAgentTypeVerifier(new NullActorTypeProbe()),
            new TestInMemoryEventStore());

        Func<Task> act = () => coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AcquireAsync_ShouldThrow_WhenRuntimeProbeReturnsNoType()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        runtime.SetActor(actorId, new RuntimeActor(actorId, new ProjectionOwnershipCoordinatorGAgent()));
        var verifier = new DefaultAgentTypeVerifier(new NullActorTypeProbe());
        var coordinator = new ActorProjectionOwnershipCoordinator(runtime, runtime, verifier, new TestInMemoryEventStore());

        Func<Task> act = () => coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static ActorProjectionOwnershipCoordinator CreateCoordinator(
        IActorRuntime runtime,
        ProjectionOwnershipCoordinatorOptions? options = null,
        IEventStore? eventStore = null)
    {
        var verifier = new DefaultAgentTypeVerifier(new RuntimeActorTypeProbe(runtime));
        return new ActorProjectionOwnershipCoordinator(
            runtime,
            (IActorDispatchPort)runtime,
            verifier,
            eventStore ?? new TestInMemoryEventStore(),
            options);
    }

    private static Task AppendStateEventAsync(
        IEventStore store,
        string agentId,
        long version,
        IMessage payload) =>
        store.AppendAsync(
            agentId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = version,
                    EventType = payload.Descriptor.FullName,
                    EventData = Any.Pack(payload),
                    AgentId = agentId,
                },
            ],
            expectedVersion: version - 1,
            CancellationToken.None);
}

public class ProjectionOwnershipCoordinatorGAgentTests
{
    private static IServiceProvider CreateStatefulAgentServices(IEventStore? eventStore = null)
    {
        var services = new ServiceCollection();
        if (eventStore != null)
            services.AddSingleton(eventStore);
        else
            services.AddSingleton<IEventStore, TestInMemoryEventStore>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        return services.BuildServiceProvider();
    }

    private static ProjectionOwnershipCoordinatorGAgent CreateStatefulAgent(IServiceProvider services) =>
        new()
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ProjectionOwnershipCoordinatorState>>(),
        };

    [Fact]
    public async Task HandleAcquireAsync_ShouldActivateOwnershipState()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());

        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
        });

        agent.State.Active.Should().BeTrue();
        agent.State.ScopeId.Should().Be("scope-1");
        agent.State.SessionId.Should().Be("session-1");
        agent.State.LastUpdatedAtUtc.Should().NotBeNull();
        agent.State.LeaseTtlMs.Should().Be(ProjectionOwnershipCoordinatorOptions.DefaultLeaseTtlMs);
    }

    [Fact]
    public async Task HandleAcquireAsync_ShouldThrow_WhenOwnershipAlreadyActive()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());
        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
        });

        Func<Task> act = () => agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-2",
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleAcquireAsync_ShouldRenewLease_WhenSessionMatches()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());
        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            LeaseTtlMs = 30_000,
        });
        var firstUpdatedAt = agent.State.LastUpdatedAtUtc;

        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            LeaseTtlMs = 90_000,
        });

        agent.State.Active.Should().BeTrue();
        agent.State.SessionId.Should().Be("session-1");
        agent.State.LeaseTtlMs.Should().Be(90_000);
        agent.State.LastUpdatedAtUtc.Should().NotBeNull();
        agent.State.LastUpdatedAtUtc.Seconds.Should().BeGreaterThanOrEqualTo(firstUpdatedAt.Seconds);
    }

    [Fact]
    public async Task HandleAcquireAsync_ShouldUseProvidedOccurredAtUtc()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());
        var occurredAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2));

        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            LeaseTtlMs = 30_000,
            OccurredAtUtc = occurredAtUtc,
        });

        agent.State.LastUpdatedAtUtc.Should().BeEquivalentTo(occurredAtUtc);
    }

    [Fact]
    public async Task HandleAcquireAsync_ShouldAllowTakeover_WhenExistingLeaseExpired()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());
        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            LeaseTtlMs = 1_000,
        });
        agent.State.LastUpdatedAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-5));

        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-2",
            LeaseTtlMs = 120_000,
        });

        agent.State.Active.Should().BeTrue();
        agent.State.SessionId.Should().Be("session-2");
        agent.State.LeaseTtlMs.Should().Be(120_000);
    }

    [Fact]
    public async Task HandleReleaseAsync_ShouldDeactivate_WhenScopeAndSessionMatch()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());
        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
        });

        await agent.HandleReleaseAsync(new ProjectionOwnershipReleaseEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
        });

        agent.State.Active.Should().BeFalse();
        agent.State.SessionId.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReleaseAsync_ShouldThrow_WhenScopeDoesNotMatch()
    {
        var agent = CreateStatefulAgent(CreateStatefulAgentServices());
        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
        });

        Func<Task> act = () => agent.HandleReleaseAsync(new ProjectionOwnershipReleaseEvent
        {
            ScopeId = "scope-2",
            SessionId = "session-1",
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AcquireRelease_ShouldPersistEvents_AndReplayStateAfterReactivate()
    {
        var store = new TestInMemoryEventStore();
        var services = CreateStatefulAgentServices(store);

        var agent1 = CreateStatefulAgent(services);
        await agent1.ActivateAsync();
        await agent1.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-replay",
            SessionId = "session-replay",
        });
        await agent1.HandleReleaseAsync(new ProjectionOwnershipReleaseEvent
        {
            ScopeId = "scope-replay",
            SessionId = "session-replay",
        });
        await agent1.DeactivateAsync();

        var persisted = await store.GetEventsAsync(agent1.Id);
        persisted.Should().HaveCount(2);
        persisted.Should().Contain(x => x.EventType.Contains(nameof(ProjectionOwnershipAcquireEvent), StringComparison.Ordinal));
        persisted.Should().Contain(x => x.EventType.Contains(nameof(ProjectionOwnershipReleaseEvent), StringComparison.Ordinal));

        var agent2 = CreateStatefulAgent(services);
        await agent2.ActivateAsync();

        agent2.State.Active.Should().BeFalse();
        agent2.State.ScopeId.Should().Be("scope-replay");
        agent2.State.SessionId.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_ShouldPreserveLeaseTimestamp_AndAllowExpiredTakeover()
    {
        var store = new TestInMemoryEventStore();
        var services = CreateStatefulAgentServices(store);
        var occurredAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-10));

        var agent1 = CreateStatefulAgent(services);
        await agent1.ActivateAsync();
        await agent1.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-replay-active",
            SessionId = "session-1",
            LeaseTtlMs = 1_000,
            OccurredAtUtc = occurredAtUtc,
        });
        await agent1.DeactivateAsync();

        var agent2 = CreateStatefulAgent(services);
        await agent2.ActivateAsync();
        agent2.State.Active.Should().BeTrue();
        agent2.State.ScopeId.Should().Be("scope-replay-active");
        agent2.State.SessionId.Should().Be("session-1");
        agent2.State.LastUpdatedAtUtc.Should().BeEquivalentTo(occurredAtUtc);

        await agent2.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-replay-active",
            SessionId = "session-2",
            LeaseTtlMs = 1_000,
        });

        agent2.State.Active.Should().BeTrue();
        agent2.State.SessionId.Should().Be("session-2");
    }

    [Fact]
    public async Task Replay_ShouldFailFast_WhenAcquireEventMissingOccurredAtUtc()
    {
        var store = new TestInMemoryEventStore();
        var services = CreateStatefulAgentServices(store);
        var agent = CreateStatefulAgent(services);
        var agentId = agent.Id;
        await store.AppendAsync(
            agentId,
            [
                new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
                    Version = 1,
                    EventType = ProjectionOwnershipAcquireEvent.Descriptor.FullName,
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new ProjectionOwnershipAcquireEvent
                    {
                        ScopeId = "scope-legacy",
                        SessionId = "session-legacy",
                        LeaseTtlMs = 1_000,
                    }),
                    AgentId = agentId,
                },
            ],
            expectedVersion: 0,
            CancellationToken.None);

        Func<Task> act = () => agent.ActivateAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*occurred_at_utc*");
    }
}

public class ProjectionSessionEventHubTests
{
    [Fact]
    public async Task PublishAsync_ShouldWriteTransportMessageToScopedStream()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var hub = new ProjectionSessionEventHub<string>(provider, codec);

        await hub.PublishAsync("scope-1", "session-1", "hello", CancellationToken.None);

        var stream = provider.GetStream("projection.session:scope-1:session-1");
        stream.ProducedMessages.Should().ContainSingle();
        var message = stream.ProducedMessages.Single().Should().BeOfType<ProjectionSessionEventTransportMessage>().Subject;
        message.ScopeId.Should().Be("scope-1");
        message.SessionId.Should().Be("session-1");
        message.EventType.Should().Be("string");
        message.LegacyPayload.Should().Be("hello");
        message.Payload.Should().NotBeNull();
        message.Payload.IsEmpty.Should().BeFalse();
        var payload = Any.Parser.ParseFrom(message.Payload);
        payload.Is(StringValue.Descriptor).Should().BeTrue();
        payload.Unpack<StringValue>().Value.Should().Be("hello");
    }

    [Fact]
    public async Task SubscribeAsync_ShouldFilterByScopeAndSession_AndIgnoreUnknownEventTypes()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var hub = new ProjectionSessionEventHub<string>(provider, codec);
        var received = new List<string>();

        var subscription = await hub.SubscribeAsync(
            "scope-1",
            "session-1",
            evt =>
            {
                received.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var stream = provider.GetStream("projection.session:scope-1:session-1");
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-2",
            EventType = "string",
            Payload = Any.Pack(new StringValue { Value = "ignored-by-session" }).ToByteString(),
        });
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "unknown",
            Payload = Any.Pack(new StringValue { Value = "ignored-by-type" }).ToByteString(),
        });
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "string",
            Payload = Any.Pack(new StringValue { Value = "accepted" }).ToByteString(),
        });

        received.Should().Equal("accepted");

        await subscription.DisposeAsync();
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "string",
            Payload = Any.Pack(new StringValue { Value = "after-dispose" }).ToByteString(),
        });
        received.Should().Equal("accepted");
    }

    [Fact]
    public async Task SubscribeAsync_ShouldFallbackToLegacyPayload_WhenBinaryPayloadMissing()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var hub = new ProjectionSessionEventHub<string>(provider, codec);
        var received = new List<string>();

        await using var subscription = await hub.SubscribeAsync(
            "scope-1",
            "session-1",
            evt =>
            {
                received.Add(evt);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        var stream = provider.GetStream("projection.session:scope-1:session-1");
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "string",
            LegacyPayload = "accepted-legacy",
            Payload = ByteString.Empty,
        });

        received.Should().Equal("accepted-legacy");
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_ShouldThrow_ForInvalidArguments_AndMissingChannel()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var hub = new ProjectionSessionEventHub<string>(provider, codec);

        Func<Task> blankScope = () => hub.PublishAsync(" ", "session-1", "hello", CancellationToken.None);
        Func<Task> blankSession = () => hub.PublishAsync("scope-1", " ", "hello", CancellationToken.None);
        Func<Task> nullEvent = () => hub.PublishAsync("scope-1", "session-1", null!, CancellationToken.None);

        var emptyChannelHub = new ProjectionSessionEventHub<string>(provider, new EmptyChannelSessionEventCodec());
        Func<Task> emptyChannel = () => emptyChannelHub.PublishAsync("scope-1", "session-1", "hello", CancellationToken.None);

        await blankScope.Should().ThrowAsync<ArgumentException>();
        await blankSession.Should().ThrowAsync<ArgumentException>();
        await nullEvent.Should().ThrowAsync<ArgumentNullException>();
        await emptyChannel.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldThrow_ForInvalidArguments_AndMissingChannel()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var hub = new ProjectionSessionEventHub<string>(provider, codec);

        Func<Task> blankScope = async () => await hub.SubscribeAsync(" ", "session-1", _ => ValueTask.CompletedTask, CancellationToken.None);
        Func<Task> blankSession = async () => await hub.SubscribeAsync("scope-1", " ", _ => ValueTask.CompletedTask, CancellationToken.None);
        Func<Task> nullHandler = async () => await hub.SubscribeAsync("scope-1", "session-1", null!, CancellationToken.None);

        var emptyChannelHub = new ProjectionSessionEventHub<string>(provider, new EmptyChannelSessionEventCodec());
        Func<Task> emptyChannel = async () => await emptyChannelHub.SubscribeAsync(
            "scope-1",
            "session-1",
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        await blankScope.Should().ThrowAsync<ArgumentException>();
        await blankSession.Should().ThrowAsync<ArgumentException>();
        await nullHandler.Should().ThrowAsync<ArgumentNullException>();
        await emptyChannel.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PublishAsync_ShouldTrimScopeAndSession_WhenResolvingStreamId()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var hub = new ProjectionSessionEventHub<string>(provider, codec);

        await hub.PublishAsync(" scope-1 ", " session-1 ", "hello", CancellationToken.None);

        provider.GetStream("projection.session:scope-1:session-1")
            .ProducedMessages
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldLogWarning_WhenCodecCannotDecodeMessage()
    {
        var provider = new SessionHubStreamProvider();
        var codec = new StringSessionEventCodec();
        var logger = new RecordingLogger<ProjectionSessionEventHub<string>>();
        var hub = new ProjectionSessionEventHub<string>(provider, codec, logger);

        var subscription = await hub.SubscribeAsync(
            "scope-1",
            "session-1",
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        var stream = provider.GetStream("projection.session:scope-1:session-1");
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "unknown",
            Payload = Any.Pack(new StringValue { Value = "ignored" }).ToByteString(),
        });

        logger.Messages.Should().ContainSingle();
        logger.Messages[0].Should().Contain("Dropping undecodable projection session event");
        logger.Messages[0].Should().Contain("unknown");

        await subscription.DisposeAsync();
    }
}

internal sealed class OwnershipCoordinatorRuntime : IActorRuntime, IActorDispatchPort
{
    private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RuntimeActor> _raceCreateActors = new(StringComparer.Ordinal);

    public int CreateCallCount { get; private set; }

    public void SetActor(string actorId, IActor actor) => _actors[actorId] = actor;

    public void SetCreateRaceActor(string actorId, RuntimeActor actor) => _raceCreateActors[actorId] = actor;

    public RuntimeActor GetOwnershipActor(string actorId) => (RuntimeActor)_actors[actorId];

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("Actor id is required in tests.");

        CreateCallCount++;

        if (_raceCreateActors.TryGetValue(id, out var racedActor))
        {
            _actors[id] = racedActor;
            _raceCreateActors.Remove(id);
            throw new InvalidOperationException("Simulated create race.");
        }

        if (typeof(TAgent) != typeof(ProjectionOwnershipCoordinatorGAgent))
            throw new InvalidOperationException($"Unexpected agent type: {typeof(TAgent).Name}");

        var actor = new RuntimeActor(id, new ProjectionOwnershipCoordinatorGAgent());
        _actors[id] = actor;
        return Task.FromResult<IActor>(actor);
    }

    public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
    {
        throw new NotSupportedException();
    }

    public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IActor?> GetAsync(string id)
    {
        _actors.TryGetValue(id, out var actor);
        return Task.FromResult(actor);
    }

    public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
        await actor.HandleEventAsync(envelope, ct);
    }

    public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

}

internal sealed class RuntimeActor : IActor
{
    public RuntimeActor(string id, IAgent agent)
    {
        Id = id;
        Agent = agent;
    }

    public string Id { get; }
    public IAgent Agent { get; }
    public List<EventEnvelope> HandledEnvelopes { get; } = [];

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        HandledEnvelopes.Add(envelope);
        return Task.CompletedTask;
    }

    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class PlainTestAgent : IAgent
{
    public PlainTestAgent(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> GetDescriptionAsync() => Task.FromResult("plain");

    public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<System.Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StringSessionEventCodec
    : IProjectionSessionEventCodec<string>,
      ILegacyProjectionSessionEventCodec<string>
{
    public string Channel => "projection.session";

    public string GetEventType(string evt) => "string";

    public ByteString Serialize(string evt) => Any.Pack(new StringValue { Value = evt }).ToByteString();

    public string SerializeLegacy(string evt) => evt;

    public string? Deserialize(string eventType, ByteString payload)
    {
        if (!string.Equals(eventType, "string", StringComparison.Ordinal) ||
            payload == null ||
            payload.IsEmpty)
        {
            return null;
        }

        var envelope = Any.Parser.ParseFrom(payload);
        if (!envelope.Is(StringValue.Descriptor))
            return null;

        return envelope.Unpack<StringValue>().Value;
    }

    public string? DeserializeLegacy(string eventType, string payload) =>
        string.Equals(eventType, "string", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(payload)
            ? payload
            : null;
}

internal sealed class EmptyChannelSessionEventCodec : IProjectionSessionEventCodec<string>
{
    public string Channel => "";

    public string GetEventType(string evt) => "string";

    public ByteString Serialize(string evt) => Any.Pack(new StringValue { Value = evt }).ToByteString();

    public string? Deserialize(string eventType, ByteString payload)
    {
        _ = eventType;
        if (payload == null || payload.IsEmpty)
            return null;

        var envelope = Any.Parser.ParseFrom(payload);
        return envelope.Is(StringValue.Descriptor)
            ? envelope.Unpack<StringValue>().Value
            : null;
    }
}

internal sealed class TestInMemoryEventStore : IEventStore
{
    private readonly Dictionary<string, List<StateEvent>> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task<EventStoreCommitResult> AppendAsync(
        string agentId,
        IEnumerable<StateEvent> events,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_streams.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _streams[agentId] = stream;
                _versions[agentId] = 0;
            }

            var currentVersion = _versions.GetValueOrDefault(agentId);
            if (currentVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Version mismatch for stream '{agentId}'. expected={expectedVersion}, actual={currentVersion}.");
            }

            var appended = events.ToList();
            stream.AddRange(appended.Select(x => x.Clone()));
            var latest = appended.Count == 0 ? currentVersion : appended[^1].Version;
            _versions[agentId] = latest;
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = latest,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }
    }

    public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
        string agentId,
        long? fromVersion = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_streams.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            var filtered = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).ToList()
                : stream.ToList();
            return Task.FromResult<IReadOnlyList<StateEvent>>(filtered.Select(x => x.Clone()).ToList());
        }
    }

    public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_versions.GetValueOrDefault(agentId));
        }
    }

    public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (toVersion <= 0)
            return Task.FromResult(0L);

        lock (_sync)
        {
            if (!_streams.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }
}

internal sealed class SessionHubStreamProvider : IStreamProvider
{
    private readonly Dictionary<string, SessionHubStream> _streams = new(StringComparer.Ordinal);

    public SessionHubStream GetStream(string streamId)
    {
        if (_streams.TryGetValue(streamId, out var stream))
            return stream;

        stream = new SessionHubStream(streamId);
        _streams[streamId] = stream;
        return stream;
    }

    IStream IStreamProvider.GetStream(string actorId) => GetStream(actorId);
}

internal sealed class RuntimeActorTypeProbe : IActorTypeProbe
{
    private readonly IActorRuntime _runtime;

    public RuntimeActorTypeProbe(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
    {
        _ = ct;
        var actor = await _runtime.GetAsync(actorId);
        var type = actor?.Agent.GetType();
        return type?.AssemblyQualifiedName ?? type?.FullName;
    }
}

internal sealed class NullActorTypeProbe : IActorTypeProbe
{
    public Task<string?> GetRuntimeAgentTypeNameAsync(string actorId, CancellationToken ct = default)
    {
        _ = actorId;
        _ = ct;
        return Task.FromResult<string?>(null);
    }
}

internal sealed class SessionHubStream : IStream
{
    private readonly List<Func<ProjectionSessionEventTransportMessage, Task>> _handlers = [];

    public SessionHubStream(string streamId)
    {
        StreamId = streamId;
    }

    public string StreamId { get; }
    public List<IMessage> ProducedMessages { get; } = [];

    public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
    {
        ProducedMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        if (typeof(T) != typeof(ProjectionSessionEventTransportMessage))
            throw new InvalidOperationException($"Unexpected subscription type: {typeof(T).Name}");

        Func<ProjectionSessionEventTransportMessage, Task> typedHandler = message =>
            handler((T)(IMessage)message);

        _handlers.Add(typedHandler);
        return Task.FromResult<IAsyncDisposable>(new SessionHubSubscription(() => _handlers.Remove(typedHandler)));
    }

    public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        _ = binding;
        _ = ct;
        return Task.CompletedTask;
    }

    public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
    {
        _ = targetStreamId;
        _ = ct;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
    }

    public async Task EmitAsync(ProjectionSessionEventTransportMessage message)
    {
        foreach (var handler in _handlers.ToArray())
            await handler(message);
    }
}

internal sealed class SessionHubSubscription : IAsyncDisposable
{
    private readonly Action _disposeAction;
    private int _disposed;

    public SessionHubSubscription(Action disposeAction)
    {
        _disposeAction = disposeAction;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        _disposeAction();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _ = eventId;
        _ = exception;
        if (logLevel < LogLevel.Warning)
            return;

        Messages.Add(formatter(state, exception));
    }
}
