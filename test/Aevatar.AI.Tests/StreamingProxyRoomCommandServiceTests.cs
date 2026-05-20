using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
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
        var projectionPort = new RecordingRoomSessionProjectionPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
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
        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches[0].ActorId.Should().Be(result.RoomId);
        projectionPort.EnsureSubscriptionCalls.Should().ContainSingle(x =>
            x.ActorId == result.RoomId &&
            x.SubscriptionId == $"room:{result.RoomId}:subscription");
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
            $"dispatch:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"projection:ensure-subscription:{result.RoomId}:room:{result.RoomId}:subscription",
            $"registry:register:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRejectBlankScopeBeforeCreatingActor()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var projectionPort = new RecordingRoomSessionProjectionPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
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
        projectionPort.EnsureSubscriptionCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldDefaultBlankRoomNameInApplicationLayer()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var projectionPort = new RecordingRoomSessionProjectionPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
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
        projectionPort.EnsureSubscriptionCalls.Should().ContainSingle(x =>
            x.ActorId == result.RoomId &&
            x.SubscriptionId == $"room:{result.RoomId}:subscription");
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
        var projectionPort = new RecordingRoomSessionProjectionPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
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
            $"projection:ensure-subscription:{result.RoomId}:room:{result.RoomId}:subscription",
            $"registry:register:{result.RoomId}",
            $"registry:unregister:{result.RoomId}",
            $"runtime:destroy:{result.RoomId}");
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldRollbackCreatedRoom_WhenSubscriptionProjectionIsUnavailable()
    {
        var operations = new List<string>();
        var runtime = new RecordingActorRuntime(operations, new RecordingActor("room-created", operations));
        var dispatchPort = new RecordingActorDispatchPort(operations, runtime);
        var registry = new RecordingGAgentActorRegistryCommandPort(operations);
        var projectionPort = new RecordingRoomSessionProjectionPort(operations)
        {
            EnsureSubscriptionResult = null,
        };
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
            NullLogger<StreamingProxyRoomCommandService>.Instance);

        var result = await service.CreateRoomAsync(
            new StreamingProxyRoomCreateCommand("scope-a", "Incident Room"),
            CancellationToken.None);

        result.Status.Should().Be(StreamingProxyRoomCreateStatus.AdmissionUnavailable);
        projectionPort.EnsureSubscriptionCalls.Should().ContainSingle(x =>
            x.ActorId == result.RoomId &&
            x.SubscriptionId == $"room:{result.RoomId}:subscription");
        registry.RegisteredActors.Should().BeEmpty();
        registry.UnregisteredActors.Should().ContainSingle();
        runtime.DestroyedActorIds.Should().ContainSingle(result.RoomId);
        operations.Should().ContainInOrder(
            $"runtime:create:{result.RoomId}",
            $"dispatch:{result.RoomId}",
            $"actor:init:{result.RoomId}",
            $"projection:ensure-subscription:{result.RoomId}:room:{result.RoomId}:subscription",
            $"registry:unregister:{result.RoomId}",
            $"runtime:destroy:{result.RoomId}");
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
        var projectionPort = new RecordingRoomSessionProjectionPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
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
            $"projection:ensure-subscription:{result.RoomId}:room:{result.RoomId}:subscription",
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
        var projectionPort = new RecordingRoomSessionProjectionPort(operations);
        var service = new StreamingProxyRoomCommandService(
            runtime,
            dispatchPort,
            registry,
            projectionPort,
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

    private sealed class RecordingRoomSessionProjectionPort(List<string> operations)
        : IStreamingProxyRoomSessionProjectionPort
    {
        public List<(string ActorId, string SubscriptionId)> EnsureSubscriptionCalls { get; } = [];
        public IStreamingProxyRoomSessionProjectionLease? EnsureSubscriptionResult { get; init; } =
            new RecordingRoomSessionProjectionLease("pending", "pending");
        public bool ProjectionEnabled => true;

        public Task<IStreamingProxyRoomSessionProjectionLease?> EnsureChatProjectionAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default)
        {
            _ = actorId;
            _ = sessionId;
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException("Room creation should not ensure chat projections.");
        }

        public Task<IStreamingProxyRoomSessionProjectionLease?> EnsureSubscriptionProjectionAsync(
            string actorId,
            string subscriptionId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            operations.Add($"projection:ensure-subscription:{actorId}:{subscriptionId}");
            EnsureSubscriptionCalls.Add((actorId, subscriptionId));
            return Task.FromResult<IStreamingProxyRoomSessionProjectionLease?>(EnsureSubscriptionResult is null
                ? null
                : new RecordingRoomSessionProjectionLease(actorId, subscriptionId));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            IEventSink<StreamingProxyRoomSessionEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            _ = liveSinkLease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IStreamingProxyRoomSessionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed record RecordingRoomSessionProjectionLease(string ActorId, string SessionId)
        : IStreamingProxyRoomSessionProjectionLease;

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

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            operations.Add($"dispatch:{actorId}");
            Dispatches.Add((actorId, envelope));
            var actor = await runtime.GetAsync(actorId);
            if (actor is not null)
                await actor.HandleEventAsync(envelope, ct);
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
