using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionCoordinatorTests
{
    [Fact]
    public async Task ShouldExecuteProjectorsInRegistrationOrder()
    {
        var trace = new List<string>();
        var coordinator = new ProjectionCoordinator<TestProjectionContext, string>(
        [
            new RecordingProjector("p1", trace),
            new RecordingProjector("p2", trace),
        ]);
        var context = new TestProjectionContext("projection-1", "actor-1");
        var envelope = new EventEnvelope { Id = "evt-1" };

        await coordinator.InitializeAsync(context, CancellationToken.None);
        await coordinator.ProjectAsync(context, envelope, CancellationToken.None);
        await coordinator.CompleteAsync(context, "done", CancellationToken.None);

        trace.Should().Equal(
            "p1:init",
            "p2:init",
            "p1:project",
            "p2:project",
            "p1:complete:done",
            "p2:complete:done");
    }
}

public class ProjectionLifecycleServiceTests
{
    [Fact]
    public async Task ShouldOrchestrateStartProjectStopAndCompleteInExpectedOrder()
    {
        var trace = new List<string>();
        var coordinator = new FakeCoordinator(trace);
        var dispatcher = new FakeDispatcher(trace);
        var registry = new FakeRegistry(trace);
        var lifecycle = new ProjectionLifecycleService<TestProjectionContext, string>(coordinator, dispatcher, registry);

        var context = new TestProjectionContext("projection-1", "actor-1");
        var envelope = new EventEnvelope { Id = "evt-1" };

        await lifecycle.StartAsync(context, CancellationToken.None);
        await lifecycle.ProjectAsync(context, envelope, CancellationToken.None);
        await lifecycle.StopAsync(context, CancellationToken.None);
        await lifecycle.CompleteAsync(context, "topology-1", CancellationToken.None);

        trace.Should().Equal(
            "coordinator:init",
            "registry:register",
            "dispatcher:dispatch",
            "registry:unregister",
            "registry:unregister",
            "coordinator:complete:topology-1");
    }
}

public class ProjectionSubscriptionRegistryTests
{
    [Fact]
    public async Task RegisterAndUnregister_ShouldManageLeaseAndForwardEvents()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");

        await registry.RegisterAsync(context, CancellationToken.None);

        context.StreamSubscriptionLease.Should().NotBeNull();
        hub.LastActorId.Should().Be("actor-1");

        await hub.EmitAsync(new EventEnvelope { Id = "evt-1" });
        dispatcher.DispatchCount.Should().Be(1);

        var lease = (FakeLease?)context.StreamSubscriptionLease;
        await registry.UnregisterAsync(context, CancellationToken.None);

        context.StreamSubscriptionLease.Should().BeNull();
        lease.Should().NotBeNull();
        lease!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrow_WhenContextAlreadyRegistered()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");

        await registry.RegisterAsync(context, CancellationToken.None);

        Func<Task> act = () => registry.RegisterAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

public class ActorStreamSubscriptionHubTests
{
    [Fact]
    public async Task SubscribeAsync_ShouldCreateLease_AndDisposeUnderlyingSubscription()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);
        var received = new List<string>();

        var lease = await hub.SubscribeAsync(
            "actor-1",
            envelope =>
            {
                received.Add(envelope.Id);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await provider.Stream.DeliverAsync(new EventEnvelope { Id = "evt-1" });
        received.Should().Equal("evt-1");

        await lease.DisposeAsync();
        provider.Stream.Subscription.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldThrow_WhenActorIdIsBlank()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        Func<Task> act = () => hub.SubscribeAsync("   ", _ => ValueTask.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

public class ProjectionAssemblyRegistrationTests
{
    [Fact]
    public void RegisterProjectionExtensionsFromAssembly_ShouldRegisterMarkerAndGenericAbstractions()
    {
        var services = new ServiceCollection();

        ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(
            services,
            typeof(TestReducerExtension).Assembly,
            typeof(ITestReducerMarker),
            typeof(ITestProjectorMarker),
            typeof(ITestReducerGeneric<,>),
            typeof(ITestProjectorGeneric<,>));

        using var provider = services.BuildServiceProvider();

        provider.GetServices<ITestReducerMarker>().Should().ContainSingle(x => x.GetType() == typeof(TestReducerExtension));
        provider.GetServices<ITestProjectorMarker>().Should().ContainSingle(x => x.GetType() == typeof(TestReducerExtension));
        provider.GetServices<ITestReducerGeneric<TestProjectionContext, string>>().Should().ContainSingle(x => x.GetType() == typeof(TestReducerExtension));
        provider.GetServices<ITestProjectorGeneric<TestProjectionContext, string>>().Should().ContainSingle(x => x.GetType() == typeof(TestReducerExtension));
    }
}

public sealed class TestProjectionContext : IProjectionContext, IProjectionStreamSubscriptionContext
{
    public TestProjectionContext(string projectionId, string rootActorId)
    {
        ProjectionId = projectionId;
        RootActorId = rootActorId;
    }

    public string ProjectionId { get; }
    public string RootActorId { get; }
    public IActorStreamSubscriptionLease? StreamSubscriptionLease { get; set; }
}

internal sealed class RecordingProjector : IProjectionProjector<TestProjectionContext, string>
{
    private readonly string _name;
    private readonly IList<string> _trace;

    public RecordingProjector(string name, IList<string> trace)
    {
        _name = name;
        _trace = trace;
    }

    public ValueTask InitializeAsync(TestProjectionContext context, CancellationToken ct = default)
    {
        _trace.Add($"{_name}:init");
        return ValueTask.CompletedTask;
    }

    public ValueTask ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add($"{_name}:project");
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(TestProjectionContext context, string topology, CancellationToken ct = default)
    {
        _trace.Add($"{_name}:complete:{topology}");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeCoordinator : IProjectionCoordinator<TestProjectionContext, string>
{
    private readonly IList<string> _trace;

    public FakeCoordinator(IList<string> trace)
    {
        _trace = trace;
    }

    public Task InitializeAsync(TestProjectionContext context, CancellationToken ct = default)
    {
        _trace.Add("coordinator:init");
        return Task.CompletedTask;
    }

    public Task ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add("coordinator:project");
        return Task.CompletedTask;
    }

    public Task CompleteAsync(TestProjectionContext context, string topology, CancellationToken ct = default)
    {
        _trace.Add($"coordinator:complete:{topology}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeDispatcher : IProjectionDispatcher<TestProjectionContext>
{
    private readonly IList<string> _trace;

    public FakeDispatcher(IList<string> trace)
    {
        _trace = trace;
    }

    public Task DispatchAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add("dispatcher:dispatch");
        return Task.CompletedTask;
    }
}

internal sealed class FakeRegistry : IProjectionSubscriptionRegistry<TestProjectionContext>
{
    private readonly IList<string> _trace;

    public FakeRegistry(IList<string> trace)
    {
        _trace = trace;
    }

    public Task RegisterAsync(TestProjectionContext context, CancellationToken ct = default)
    {
        _trace.Add("registry:register");
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(TestProjectionContext context, CancellationToken ct = default)
    {
        _trace.Add("registry:unregister");
        return Task.CompletedTask;
    }
}

internal sealed class CountingDispatcher : IProjectionDispatcher<TestProjectionContext>
{
    public int DispatchCount { get; private set; }

    public Task DispatchAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        DispatchCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeSubscriptionHub : IActorStreamSubscriptionHub<EventEnvelope>
{
    public string? LastActorId { get; private set; }
    public Func<EventEnvelope, ValueTask>? Handler { get; private set; }

    public Task<IActorStreamSubscriptionLease> SubscribeAsync(
        string actorId,
        Func<EventEnvelope, ValueTask> handler,
        CancellationToken ct = default)
    {
        LastActorId = actorId;
        Handler = handler;
        return Task.FromResult<IActorStreamSubscriptionLease>(new FakeLease(actorId));
    }

    public async Task EmitAsync(EventEnvelope envelope)
    {
        Handler.Should().NotBeNull();
        await Handler!(envelope);
    }
}

internal sealed class FakeLease : IActorStreamSubscriptionLease
{
    public FakeLease(string actorId)
    {
        ActorId = actorId;
    }

    public string ActorId { get; }
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeStreamProvider : IStreamProvider
{
    public FakeStream Stream { get; } = new();

    public IStream GetStream(string actorId) => Stream;
}

internal sealed class FakeStream : IStream
{
    private Func<EventEnvelope, Task>? _eventEnvelopeHandler;

    public string StreamId => "fake-stream";
    public FakeAsyncSubscription Subscription { get; } = new();

    public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage => Task.CompletedTask;

    public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        if (typeof(T) != typeof(EventEnvelope))
            throw new InvalidOperationException($"Unexpected subscription type: {typeof(T).Name}");

        _eventEnvelopeHandler = envelope => handler((T)(IMessage)envelope);
        return Task.FromResult<IAsyncDisposable>(Subscription);
    }

    public async Task DeliverAsync(EventEnvelope envelope)
    {
        _eventEnvelopeHandler.Should().NotBeNull();
        await _eventEnvelopeHandler!(envelope);
    }
}

internal sealed class FakeAsyncSubscription : IAsyncDisposable
{
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public interface ITestReducerMarker
{
}

public interface ITestProjectorMarker
{
}

public interface ITestReducerGeneric<TContext, TTopology>
{
}

public interface ITestProjectorGeneric<TContext, TTopology>
{
}

public sealed class TestReducerExtension :
    ITestReducerMarker,
    ITestProjectorMarker,
    ITestReducerGeneric<TestProjectionContext, string>,
    ITestProjectorGeneric<TestProjectionContext, string>
{
}
