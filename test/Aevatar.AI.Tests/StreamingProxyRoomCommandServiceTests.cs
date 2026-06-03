using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.StreamingProxy;
using Aevatar.GAgents.StreamingProxy.Application.Rooms;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class StreamingProxyRoomCommandServiceTests
{
    [Fact]
    public async Task CreateRoomAsync_ShouldCreateInitializeAndRegisterRoom()
    {
        var operations = new List<string>();
        var actor = new RecordingActor("room-created", operations);
        var runtime = new RecordingActorRuntime(operations, actor);
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Summary Standup"),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.Created);
        result.RoomName.Should().Be("Summary Standup");
        result.RoomId.Should().NotBeNullOrWhiteSpace();
        registry.RegisteredActors.Should().ContainSingle();
        registry.RegisteredActors[0].Should().Be(new GAgentActorRegistration(
            "scope-a",
            StreamingProxyDefaults.GAgentTypeName,
            result.RoomId!));
        runtime.LastCreatedActor.Should().NotBeNull();
        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches[0].ActorId.Should().Be(result.RoomId);
        runtime.LastCreatedActor!.ReceivedEnvelopes.Should().ContainSingle();
        var envelope = runtime.LastCreatedActor.ReceivedEnvelopes[0];
        envelope.Route.Direct.TargetActorId.Should().Be(result.RoomId);
        envelope
            .Payload
            .Unpack<GroupChatRoomInitializedEvent>()
            .RoomName
            .Should()
            .Be("Summary Standup");
        operations.Should().ContainInOrder(
            $"runtime:create:{result.RoomId}",
            $"dispatch:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRejectBlankScopeBeforeCreatingActor()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var act = async () => await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("  ", "Incident Room"),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("ScopeId");
        operations.Should().BeEmpty();
        registry.RegisteredActors.Should().BeEmpty();
        registry.UnregisteredActors.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldDefaultBlankRoomNameInApplicationLayer()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "  "),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.Created);
        result.RoomName.Should().Be("Group Chat");
        runtime.LastCreatedActor!.ReceivedEnvelopes[0]
            .Payload
            .Unpack<GroupChatRoomInitializedEvent>()
            .RoomName
            .Should()
            .Be("Group Chat");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRollbackCreatedRoom_WhenRegistrationIsNotAdmissionVisible()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Incident Room"),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.AdmissionUnavailable);
        registry.UnregisteredActors.Should().ContainSingle();
        runtime.DestroyedActorIds.Should().ContainSingle(result.RoomId);
        operations.Should().ContainInOrder(
            $"runtime:create:{result.RoomId}",
            $"dispatch:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}",
            $"registry:unregister:{result.RoomId}",
            $"runtime:destroy:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldNotUseSubscriptionProjectionAsCommandAdmission()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Incident Room"),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.Created);
        registry.RegisteredActors.Should().ContainSingle();
        registry.UnregisteredActors.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().BeEmpty();
        operations.Should().ContainInOrder(
            $"runtime:create:{result.RoomId}",
            $"dispatch:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}");
        operations.Should().NotContain(x => x.StartsWith("projection:ensure-subscription:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldNotDestroyRoom_WhenRollbackUnregisterFails()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnRegister = new InvalidOperationException("registry unavailable"),
            ThrowOnUnregister = new InvalidOperationException("registry unregister unavailable"),
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Incident Room"),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.Failed);
        registry.UnregisteredActors.Should().ContainSingle();
        runtime.DestroyedActorIds.Should().BeEmpty();
        operations.Should().ContainInOrder(
            $"runtime:create:{result.RoomId}",
            $"dispatch:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}",
            $"registry:unregister:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRollbackCreatedRoomAndRethrow_WhenRegistrationIsCanceled()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnRegister = new OperationCanceledException("client disconnected"),
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var act = async () => await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Incident Room"),
            CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        registry.UnregisteredActors.Should().ContainSingle();
        runtime.DestroyedActorIds.Should().ContainSingle();
        var unregisterIndex = operations.FindIndex(x => x.StartsWith("registry:unregister:", StringComparison.Ordinal));
        var destroyIndex = operations.FindIndex(x => x.StartsWith("runtime:destroy:", StringComparison.Ordinal));
        unregisterIndex.Should().BeGreaterThanOrEqualTo(0);
        destroyIndex.Should().BeGreaterThan(unregisterIndex);
    }

    [Fact]
    public async Task PostMessageAsync_ShouldLookupRoomAndDispatchTypedMessage()
    {
        var operations = new List<string>();
        var actor = new RecordingActor("room-a", operations);
        var runtime = new RecordingActorRuntime(operations, actor);
        await runtime.CreateAsync<StreamingProxyGAgent>("room-a");
        operations.Clear();
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.PostMessageAsync(
            new StreamingProxyRoomPostMessageCommand("room-a", " agent-1 ", null, " hello ", null),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomPostMessageStatus.Accepted);
        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var message = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyParticipantMessageRequested>();
        message.AgentId.Should().Be("agent-1");
        message.AgentName.Should().Be("agent-1");
        message.Content.Should().Be("hello");
        message.SessionId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PostMessageAsync_ShouldReturnRoomNotFound_WhenRoomIsMissing()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-a", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.PostMessageAsync(
            new StreamingProxyRoomPostMessageCommand("missing-room", "agent-1", null, "hello", null),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomPostMessageStatus.RoomNotFound);
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task JoinAsync_ShouldLookupRoomAndDispatchTypedJoin()
    {
        var operations = new List<string>();
        var actor = new RecordingActor("room-a", operations);
        var runtime = new RecordingActorRuntime(operations, actor);
        await runtime.CreateAsync<StreamingProxyGAgent>("room-a");
        operations.Clear();
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.JoinAsync(
            new StreamingProxyRoomJoinCommand("room-a", " agent-1 ", " Alice "),
            CancellationToken.None);

        result.Should().Be(new StreamingProxyRoomJoinResult(
            StreamingProxyRoomJoinStatus.Accepted,
            "agent-1",
            "Alice"));
        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var joined = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyParticipantJoinRequested>();
        joined.AgentId.Should().Be("agent-1");
        joined.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task JoinAsync_ShouldReturnRoomNotFound_WhenRoomIsMissing()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-a", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.JoinAsync(
            new StreamingProxyRoomJoinCommand("missing-room", "agent-1", "Alice"),
            CancellationToken.None);

        result.Should().Be(new StreamingProxyRoomJoinResult(
            StreamingProxyRoomJoinStatus.RoomNotFound,
            null,
            null));
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task LeaveAsync_ShouldLookupRoomAndDispatchTypedLeaveRequest()
    {
        var operations = new List<string>();
        var actor = new RecordingActor("room-a", operations);
        var runtime = new RecordingActorRuntime(operations, actor);
        await runtime.CreateAsync<StreamingProxyGAgent>("room-a");
        operations.Clear();
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.LeaveAsync(
            new StreamingProxyRoomLeaveCommand("room-a", " agent-1 ", " unavailable "),
            CancellationToken.None);

        result.Should().Be(new StreamingProxyRoomLeaveResult(
            StreamingProxyRoomLeaveStatus.Accepted,
            "agent-1"));
        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var left = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyParticipantLeaveRequested>();
        left.AgentId.Should().Be("agent-1");
        left.Reason.Should().Be("unavailable");
    }

    [Fact]
    public async Task LeaveAsync_ShouldReturnRoomNotFound_WhenRoomIsMissing()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-a", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.LeaveAsync(
            new StreamingProxyRoomLeaveCommand("missing-room", "agent-1", "unavailable"),
            CancellationToken.None);

        result.Should().Be(new StreamingProxyRoomLeaveResult(
            StreamingProxyRoomLeaveStatus.RoomNotFound,
            null));
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishTerminalStateAsync_ShouldDispatchTypedTerminalStateWithoutRuntimeLookup()
    {
        var operations = new List<string>();
        var actor = new RecordingActor("room-a", operations);
        var runtime = new RecordingActorRuntime(operations, actor);
        await runtime.CreateAsync<StreamingProxyGAgent>("room-a");
        operations.Clear();
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        await service.PublishTerminalStateAsync(
            new StreamingProxyRoomTerminalStateCommand(
                " room-a ",
                " session-1 ",
                StreamingProxyChatSessionTerminalStatus.Failed,
                "failed"),
            CancellationToken.None);

        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var terminal = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxySessionTerminalStateRequested>();
        terminal.SessionId.Should().Be("session-1");
        terminal.Status.Should().Be(StreamingProxyChatSessionTerminalStatus.Failed);
        terminal.ErrorMessage.Should().Be("failed");
    }

    [Fact]
    public async Task SubmitParticipantsResolvedAsync_ShouldDispatchTypedParticipantsResolvedRequest()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-a", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        await service.SubmitParticipantsResolvedAsync(
            new StreamingProxyRoomParticipantsResolvedCommand(
                " room-a ",
                " session-1 ",
                [
                    new StreamingProxyChatLifecycleParticipant
                    {
                        ParticipantId = "participant-1",
                        DisplayName = "Participant 1",
                        RoutePreference = "route-a",
                        Model = "model-a",
                    },
                ]),
            CancellationToken.None);

        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var resolved = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyChatParticipantsResolvedRequested>();
        resolved.SessionId.Should().Be("session-1");
        resolved.Participants.Should().ContainSingle().Which.Should().BeEquivalentTo(new StreamingProxyChatLifecycleParticipant
        {
            ParticipantId = "participant-1",
            DisplayName = "Participant 1",
            RoutePreference = "route-a",
            Model = "model-a",
        });
    }

    [Fact]
    public async Task SubmitParticipantReplyObservedAsync_ShouldDispatchTrimmedReplyObservation()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-a", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        await service.SubmitParticipantReplyObservedAsync(
            new StreamingProxyRoomParticipantReplyObservedCommand(
                " room-a ",
                " session-1 ",
                " participant-1 ",
                2,
                1,
                " reply body "),
            CancellationToken.None);

        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var observed = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyChatParticipantReplyObservedRequested>();
        observed.SessionId.Should().Be("session-1");
        observed.ParticipantId.Should().Be("participant-1");
        observed.Round.Should().Be(2);
        observed.ParticipantIndex.Should().Be(1);
        observed.Content.Should().Be("reply body");
    }

    [Fact]
    public async Task SubmitParticipantReplyFailedAsync_ShouldDispatchTypedFailureObservation()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-a", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            new RecordingGAgentActorRegistryCommandPort(operations),
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        await service.SubmitParticipantReplyFailedAsync(
            new StreamingProxyRoomParticipantReplyFailedCommand(
                " room-a ",
                " session-1 ",
                " participant-1 ",
                2,
                1,
                StreamingProxyChatParticipantReplyFailureKind.Error,
                null),
            CancellationToken.None);

        dispatchPort.Dispatches.Should().ContainSingle(x => x.ActorId == "room-a");
        var failed = dispatchPort.Dispatches.Single().Envelope.Payload.Unpack<StreamingProxyChatParticipantReplyFailedRequested>();
        failed.SessionId.Should().Be("session-1");
        failed.ParticipantId.Should().Be("participant-1");
        failed.Round.Should().Be(2);
        failed.ParticipantIndex.Should().Be(1);
        failed.FailureKind.Should().Be(StreamingProxyChatParticipantReplyFailureKind.Error);
        failed.ErrorMessage.Should().BeEmpty();
    }

    private sealed class RecordingGAgentActorRegistryCommandPort(List<string> operations)
        : IGAgentActorRegistryCommandPort
    {
        public List<GAgentActorRegistration> RegisteredActors { get; } = [];
        public List<GAgentActorRegistration> UnregisteredActors { get; } = [];
        public Exception? ThrowOnRegister { get; init; }
        public Exception? ThrowOnUnregister { get; init; }
        public GAgentActorRegistryCommandStage RegisterStage { get; init; } =
            GAgentActorRegistryCommandStage.AdmissionVisible;

        public Task<GAgentActorRegistryCommandReceipt> RegisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add($"registry:register:{registration.ActorId}");
            RegisteredActors.Add(registration);
            if (ThrowOnRegister is not null)
                throw ThrowOnRegister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(registration, RegisterStage));
        }

        public Task<GAgentActorRegistryCommandReceipt> UnregisterActorAsync(
            GAgentActorRegistration registration,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"registry:unregister:{registration.ActorId}");
            UnregisteredActors.Add(registration);
            if (ThrowOnUnregister is not null)
                throw ThrowOnUnregister;

            return Task.FromResult(new GAgentActorRegistryCommandReceipt(
                registration,
                GAgentActorRegistryCommandStage.AdmissionRemoved));
        }
    }

    private sealed class RecordingActorRuntime(List<string> operations, IActor actor) : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DestroyedActorIds { get; } = [];
        public RecordingActor? LastCreatedActor { get; private set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            agentType.Should().Be(typeof(StreamingProxyGAgent));
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? throw new InvalidOperationException("Actor id is required for this test.");
            operations.Add($"runtime:create:{actorId}");
            LastCreatedActor = actor is RecordingActor recordingActor && recordingActor.Id == actorId
                ? recordingActor
                : new RecordingActor(actorId, operations);
            _actors[actorId] = LastCreatedActor;
            return Task.FromResult<IActor>(LastCreatedActor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            operations.Add($"runtime:destroy:{id}");
            DestroyedActorIds.Add(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _actors.TryGetValue(id, out var actor);
            return Task.FromResult<IActor?>(actor);
        }

        public Task<bool> ExistsAsync(string id)
        {
            _ = id;
            return Task.FromResult(false);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorDispatchPort(List<string> operations, IActorRuntime runtime)
        : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            operations.Add($"dispatch:{actorId}");
            Dispatches.Add((actorId, envelope));
            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }
    }

    private sealed class RecordingActor(string id, List<string> operations) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new StubAgent(id);
        public List<EventEnvelope> ReceivedEnvelopes { get; } = [];

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add($"actor:init:{Id}");
            ReceivedEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
