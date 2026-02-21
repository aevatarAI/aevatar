using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ActorProjectionOwnershipCoordinatorTests
{
    [Fact]
    public async Task AcquireAsync_ShouldCreateCoordinatorActor_AndDispatchAcquireEvent()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var coordinator = new ActorProjectionOwnershipCoordinator(runtime);

        await coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        runtime.CreateCallCount.Should().Be(1);
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var actor = runtime.GetOwnershipActor(actorId);
        actor.HandledEnvelopes.Should().ContainSingle();

        var envelope = actor.HandledEnvelopes.Single();
        var evt = envelope.Payload.Unpack<ProjectionOwnershipAcquireEvent>();
        evt.ScopeId.Should().Be("scope-1");
        evt.SessionId.Should().Be("session-1");
        envelope.CorrelationId.Should().Be("session-1");
        envelope.Direction.Should().Be(EventDirection.Self);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldReuseExistingCoordinatorActor()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var actor = new RuntimeActor(actorId, new ProjectionOwnershipCoordinatorGAgent());
        runtime.SetActor(actorId, actor);
        var coordinator = new ActorProjectionOwnershipCoordinator(runtime);

        await coordinator.ReleaseAsync("scope-1", "session-1", CancellationToken.None);

        runtime.CreateCallCount.Should().Be(0);
        actor.HandledEnvelopes.Should().ContainSingle();
        var envelope = actor.HandledEnvelopes.Single();
        var evt = envelope.Payload.Unpack<ProjectionOwnershipReleaseEvent>();
        evt.ScopeId.Should().Be("scope-1");
        evt.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task AcquireAsync_ShouldRecover_WhenCreateRaces()
    {
        var runtime = new OwnershipCoordinatorRuntime();
        var actorId = ProjectionOwnershipCoordinatorGAgent.BuildActorId("scope-1");
        var racedActor = new RuntimeActor(actorId, new ProjectionOwnershipCoordinatorGAgent());
        runtime.SetCreateRaceActor(actorId, racedActor);
        var coordinator = new ActorProjectionOwnershipCoordinator(runtime);

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
        var coordinator = new ActorProjectionOwnershipCoordinator(runtime);

        Func<Task> act = () => coordinator.AcquireAsync("scope-1", "session-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

public class ProjectionOwnershipCoordinatorGAgentTests
{
    [Fact]
    public async Task HandleAcquireAsync_ShouldActivateOwnershipState()
    {
        var agent = new ProjectionOwnershipCoordinatorGAgent();

        await agent.HandleAcquireAsync(new ProjectionOwnershipAcquireEvent
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
        });

        agent.State.Active.Should().BeTrue();
        agent.State.ScopeId.Should().Be("scope-1");
        agent.State.SessionId.Should().Be("session-1");
        agent.State.LastUpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAcquireAsync_ShouldThrow_WhenOwnershipAlreadyActive()
    {
        var agent = new ProjectionOwnershipCoordinatorGAgent();
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
    public async Task HandleReleaseAsync_ShouldDeactivate_WhenScopeAndSessionMatch()
    {
        var agent = new ProjectionOwnershipCoordinatorGAgent();
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
        var agent = new ProjectionOwnershipCoordinatorGAgent();
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
        message.Payload.Should().Be("hello");
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
            Payload = "ignored-by-session",
        });
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "unknown",
            Payload = "ignored-by-type",
        });
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "string",
            Payload = "accepted",
        });

        received.Should().Equal("accepted");

        await subscription.DisposeAsync();
        await stream.EmitAsync(new ProjectionSessionEventTransportMessage
        {
            ScopeId = "scope-1",
            SessionId = "session-1",
            EventType = "string",
            Payload = "after-dispose",
        });
        received.Should().Equal("accepted");
    }
}

internal sealed class OwnershipCoordinatorRuntime : IActorRuntime
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

    public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
    {
        throw new NotSupportedException();
    }

    public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IActor?> GetAsync(string id)
    {
        _actors.TryGetValue(id, out var actor);
        return Task.FromResult(actor);
    }

    public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

    public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
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

    public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class StringSessionEventCodec : IProjectionSessionEventCodec<string>
{
    public string Channel => "projection.session";

    public string GetEventType(string evt) => "string";

    public string Serialize(string evt) => evt;

    public string? Deserialize(string eventType, string payload) =>
        string.Equals(eventType, "string", StringComparison.Ordinal) ? payload : null;
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
