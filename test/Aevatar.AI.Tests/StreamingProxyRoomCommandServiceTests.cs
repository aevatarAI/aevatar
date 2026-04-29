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
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            registry,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Daily Standup"),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.Created);
        result.RoomName.Should().Be("Daily Standup");
        result.RoomId.Should().NotBeNullOrWhiteSpace();
        registry.RegisteredActors.Should().ContainSingle();
        registry.RegisteredActors[0].Should().Be(new GAgentActorRegistration(
            "scope-a",
            StreamingProxyDefaults.GAgentTypeName,
            result.RoomId!));
        runtime.LastCreatedActor.Should().NotBeNull();
        runtime.LastCreatedActor!.ReceivedEnvelopes.Should().ContainSingle();
        var envelope = runtime.LastCreatedActor.ReceivedEnvelopes[0];
        envelope.Route.Direct.TargetActorId.Should().Be(result.RoomId);
        envelope
            .Payload
            .Unpack<GroupChatRoomInitializedEvent>()
            .RoomName
            .Should()
            .Be("Daily Standup");
        operations.Should().ContainInOrder(
            $"runtime:create:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRejectBlankScopeBeforeCreatingActor()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
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
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
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
        var registry = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            RegisterStage = GAgentActorRegistryCommandStage.AcceptedForDispatch,
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
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
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}",
            $"registry:unregister:{result.RoomId}",
            $"runtime:destroy:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldNotDestroyRoom_WhenRollbackUnregisterFails()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var registry = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnRegister = new InvalidOperationException("registry unavailable"),
            ThrowOnUnregister = new InvalidOperationException("registry unregister unavailable"),
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
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
            $"actor:init:{result.RoomId}",
            $"registry:register:{result.RoomId}",
            $"registry:unregister:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRollbackCreatedRoomAndRethrow_WhenRegistrationIsCanceled()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var registry = new RecordingGAgentActorRegistryCommandPort(operations)
        {
            ThrowOnRegister = new OperationCanceledException("client disconnected"),
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
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
            _ = id;
            return Task.FromResult<IActor?>(null);
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
