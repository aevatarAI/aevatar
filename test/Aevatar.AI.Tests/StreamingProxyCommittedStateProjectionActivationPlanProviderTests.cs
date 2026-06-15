using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;
using Aevatar.GAgents.StreamingProxy;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyCommittedStateProjectionActivationPlanProviderTests
{
    [Theory]
    [MemberData(nameof(RoomStateEvents))]
    public void GetPlans_ShouldMapRoomCommittedStateEventsToCurrentStateMaterialization(IMessage stateEvent)
    {
        var provider = new StreamingProxyCommittedStateProjectionActivationPlanProvider();

        var plans = provider.GetPlans(BuildContext("room-a", typeof(StreamingProxyGAgent), stateEvent))
            .ToArray();

        plans.Should().ContainSingle();
        plans[0].LeaseType.Should().Be(typeof(StreamingProxyCurrentStateRuntimeLease));
        plans[0].StartRequest.RootActorId.Should().Be("room-a");
        plans[0].StartRequest.ProjectionKind.Should().Be(StreamingProxyGAgent.ProjectionKind);
        plans[0].StartRequest.Mode.Should().Be(ProjectionRuntimeMode.DurableMaterialization);
    }

    [Fact]
    public void AddStreamingProxy_ShouldRegisterCommittedStateActivationProviderInDispatcherChain()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddAevatarRuntime()
            .AddStreamingProxy(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        provider.GetService<ProjectionActivationPlanDispatcher>()
            .Should().NotBeNull("the committed-state hook dispatches provider plans through the shared dispatcher");
        provider.GetServices<ICommittedStatePublicationHook>()
            .Should().ContainSingle(hook => hook is CommittedStateProjectionActivationHook);
        provider.GetServices<IProjectionActivationPlanProvider>()
            .Should().ContainSingle(planProvider =>
                planProvider is StreamingProxyCommittedStateProjectionActivationPlanProvider);
        provider.GetService<IProjectionScopeActivationService<StreamingProxyCurrentStateRuntimeLease>>()
            .Should().NotBeNull("the dispatcher must be able to activate the current-state materialization scope");
    }

    [Fact]
    public async Task CommittedRoomParticipantStateEvent_ShouldActivateProjectionAndPopulateReadModelSnapshot()
    {
        var observingStore = new ObservingRoomParticipantsStore();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddAevatarRuntime()
            .AddStreamingProxy(new ConfigurationBuilder().Build())
            .AddSingleton<IProjectionDocumentWriter<StreamingProxyRoomParticipantsSnapshot>>(observingStore)
            .AddSingleton<IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string>>(observingStore)
            .BuildServiceProvider();

        var runtime = provider.GetRequiredService<IActorRuntime>();
        var room = await runtime.CreateAsync<StreamingProxyGAgent>("room-e2e");

        await room.HandleEventAsync(new EventEnvelope
        {
            Id = "join-command-1",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new GroupChatParticipantJoinedEvent
            {
                AgentId = "agent-1",
                DisplayName = "Alice",
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("test", "room-e2e"),
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var snapshot = await observingStore.WaitForUpsertAsync(timeout.Token);

        snapshot.Id.Should().Be("room-e2e");
        snapshot.ActorId.Should().Be("room-e2e");
        snapshot.RootActorId.Should().Be("room-e2e");
        snapshot.StateVersion.Should().Be(1);
        snapshot.LastEventId.Should().NotBeNullOrWhiteSpace();
        snapshot.Participants.Should().ContainSingle(participant =>
            participant.AgentId == "agent-1" && participant.DisplayName == "Alice");

        var queried = await observingStore.GetAsync("room-e2e", CancellationToken.None);
        queried.Should().NotBeNull();
        queried!.Participants.Should().ContainSingle(participant =>
            participant.AgentId == "agent-1" && participant.DisplayName == "Alice");
    }

    public static IEnumerable<object[]> RoomStateEvents()
    {
        yield return
        [
            new GroupChatRoomInitializedEvent
            {
                RoomName = "Room A",
            },
        ];
        yield return
        [
            new GroupChatTopicEvent
            {
                Prompt = "Review this design.",
                SessionId = "session-1",
            },
        ];
        yield return
        [
            new GroupChatMessageEvent
            {
                AgentId = "agent-1",
                AgentName = "Alice",
                Content = "Looks good.",
                SessionId = "session-1",
            },
        ];
        yield return
        [
            new GroupChatParticipantJoinedEvent
            {
                AgentId = "agent-1",
                DisplayName = "Alice",
            },
        ];
        yield return
        [
            new GroupChatParticipantLeftEvent
            {
                AgentId = "agent-1",
            },
        ];
        yield return
        [
            new StreamingProxyChatSessionTerminalStateChanged
            {
                SessionId = "session-1",
                Status = StreamingProxyChatSessionTerminalStatus.Completed,
                TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        ];
    }

    private static CommittedStatePublicationContext BuildContext(
        string actorId,
        System.Type actorType,
        IMessage stateEvent) =>
        new()
        {
            ActorId = actorId,
            ActorType = actorType,
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = actorId,
                    EventId = "evt-1",
                    Version = 1,
                    EventType = stateEvent.Descriptor.FullName,
                    EventData = Any.Pack(stateEvent),
                },
                StateRoot = Any.Pack(new StreamingProxyGAgentState()),
            },
        };

    private sealed class ObservingRoomParticipantsStore :
        IProjectionDocumentWriter<StreamingProxyRoomParticipantsSnapshot>,
        IProjectionDocumentReader<StreamingProxyRoomParticipantsSnapshot, string>
    {
        private readonly TaskCompletionSource<StreamingProxyRoomParticipantsSnapshot> _upserted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private StreamingProxyRoomParticipantsSnapshot? _snapshot;

        public Task<ProjectionWriteResult> UpsertAsync(
            StreamingProxyRoomParticipantsSnapshot readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _snapshot = readModel.Clone();
            _upserted.TrySetResult(_snapshot.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(_snapshot?.Id, id, StringComparison.Ordinal))
                _snapshot = null;

            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<StreamingProxyRoomParticipantsSnapshot?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var current = _snapshot;
            if (current == null || !string.Equals(current.Id, key, StringComparison.Ordinal))
                return Task.FromResult<StreamingProxyRoomParticipantsSnapshot?>(null);

            return Task.FromResult<StreamingProxyRoomParticipantsSnapshot?>(current.Clone());
        }

        public Task<ProjectionDocumentQueryResult<StreamingProxyRoomParticipantsSnapshot>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            var items = _snapshot == null
                ? []
                : new[] { _snapshot.Clone() };
            return Task.FromResult(new ProjectionDocumentQueryResult<StreamingProxyRoomParticipantsSnapshot>
            {
                Items = items,
            });
        }

        public async Task<StreamingProxyRoomParticipantsSnapshot> WaitForUpsertAsync(CancellationToken ct) =>
            await _upserted.Task.WaitAsync(ct);
    }
}
