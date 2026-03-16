using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionCoordinatorTests
{
    [Fact]
    public async Task ShouldExecuteProjectorsInRegistrationOrder()
    {
        var trace = new List<string>();
        var coordinator = new ProjectionCoordinator<TestProjectionContext>(
        [
            new RecordingProjector("p1", trace),
            new RecordingProjector("p2", trace),
        ]);
        var context = new TestProjectionContext("session-1", "actor-1");

        await coordinator.ProjectAsync(context, new EventEnvelope { Id = "evt-1" }, CancellationToken.None);

        trace.Should().Equal("p1:project", "p2:project");
    }

    [Fact]
    public async Task ProjectAsync_WhenProjectorFails_ShouldContinueOtherProjectorsAndThrowAggregate()
    {
        var trace = new List<string>();
        var coordinator = new ProjectionCoordinator<TestProjectionContext>(
        [
            new RecordingProjector("p1", trace),
            new ThrowingProjector("p2", trace, new InvalidOperationException("boom")),
            new RecordingProjector("p3", trace),
        ]);
        var context = new TestProjectionContext("session-1", "actor-1");

        var act = () => coordinator.ProjectAsync(context, new EventEnvelope { Id = "evt-1" }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ProjectionDispatchAggregateException>();
        ex.Which.Failures.Should().ContainSingle();
        ex.Which.Failures[0].ProjectorName.Should().Be(nameof(ThrowingProjector));
        ex.Which.Failures[0].ProjectorOrder.Should().Be(2);
        trace.Should().Equal("p1:project", "p2:project", "p3:project");
    }

    [Fact]
    public void ProjectionDispatchAggregateException_ShouldUseDefaultMessage_WhenNoFailures()
    {
        var ex = new ProjectionDispatchAggregateException([]);
        ex.Message.Should().Be("Projection dispatch failed.");
        ex.Failures.Should().BeEmpty();
    }
}

public class ProjectionLifecycleServiceTests
{
    [Fact]
    public async Task ShouldOrchestrateStartProjectAndStopInExpectedOrder()
    {
        var trace = new List<string>();
        var dispatcher = new FakeDispatcher(trace);
        var registry = new FakeRegistry(trace);
        var lifecycle = new ProjectionLifecycleService<TestProjectionContext, TestRuntimeLease>(dispatcher, registry);
        var context = new TestProjectionContext("session-1", "actor-1");
        var runtimeLease = new TestRuntimeLease(context);

        await lifecycle.StartAsync(runtimeLease, CancellationToken.None);
        await lifecycle.ProjectAsync(context, new EventEnvelope { Id = "evt-1" }, CancellationToken.None);
        await lifecycle.StopAsync(runtimeLease, CancellationToken.None);

        trace.Should().Equal("registry:register", "dispatcher:dispatch", "registry:unregister");
    }
}

public class ProjectionSubscriptionRegistryTests
{
    [Fact]
    public async Task RegisterAndUnregister_ShouldManageLeaseAndForwardEvents()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(dispatcher, hub);
        var runtimeLease = new TestRuntimeLease(new TestProjectionContext("session-1", "actor-1"));

        await registry.RegisterAsync(runtimeLease, CancellationToken.None);

        runtimeLease.ActorStreamSubscriptionLease.Should().NotBeNull();
        hub.LastActorId.Should().Be("actor-1");

        await hub.EmitAsync(CreateObservedEnvelope("evt-1"));
        dispatcher.DispatchCount.Should().Be(1);

        await registry.UnregisterAsync(runtimeLease, CancellationToken.None);

        runtimeLease.ActorStreamSubscriptionLease.Should().BeNull();
        hub.LastLease.Should().NotBeNull();
        hub.LastLease!.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrow_WhenAlreadyRegistered()
    {
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(
            new CountingDispatcher(),
            new FakeSubscriptionHub());
        var runtimeLease = new TestRuntimeLease(new TestProjectionContext("session-1", "actor-1"));

        await registry.RegisterAsync(runtimeLease, CancellationToken.None);

        await FluentActions.Invoking(() => registry.RegisterAsync(runtimeLease, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RegisterAsync_ShouldReportFailure_WhenDispatcherThrows()
    {
        var reporter = new CapturingFailureReporter();
        var envelope = CreateObservedEnvelope("evt-1");
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(
            new ThrowingDispatcher(new InvalidOperationException("boom")),
            hub,
            reporter);
        var runtimeLease = new TestRuntimeLease(new TestProjectionContext("session-1", "actor-1"));

        await registry.RegisterAsync(runtimeLease, CancellationToken.None);
        await hub.EmitAsync(envelope);

        reporter.Calls.Should().ContainSingle();
        reporter.Calls[0].Context.Should().BeSameAs(runtimeLease.Context);
        reporter.Calls[0].Envelope.Should().BeSameAs(envelope);
        reporter.Calls[0].Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task RegisterAsync_ShouldIgnoreDispatchFailures_WhenReporterIsMissing()
    {
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(
            new ThrowingDispatcher(new InvalidOperationException("boom")),
            hub);
        var runtimeLease = new TestRuntimeLease(new TestProjectionContext("session-1", "actor-1"));

        await registry.RegisterAsync(runtimeLease, CancellationToken.None);
        await FluentActions.Invoking(() => hub.EmitAsync(CreateObservedEnvelope("evt-1")))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrow_WhenHubSubscriptionFails()
    {
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(
            new CountingDispatcher(),
            new ThrowingSubscriptionHub(new InvalidOperationException("subscribe-failed")));
        var runtimeLease = new TestRuntimeLease(new TestProjectionContext("session-1", "actor-1"));

        await FluentActions.Invoking(() => registry.RegisterAsync(runtimeLease, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("subscribe-failed");

        runtimeLease.ActorStreamSubscriptionLease.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_ShouldSkipDispatch_WhenLinkedTokenIsCancelled()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(dispatcher, hub);
        var runtimeLease = new TestRuntimeLease(new TestProjectionContext("session-1", "actor-1"));
        using var cts = new CancellationTokenSource();

        await registry.RegisterAsync(runtimeLease, cts.Token);
        cts.Cancel();
        await hub.EmitAsync(CreateObservedEnvelope("evt-late"));
        await registry.UnregisterAsync(runtimeLease, CancellationToken.None);

        dispatcher.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task RegisterAndUnregister_ShouldValidateArgumentsAndDisposedState()
    {
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>(
            new CountingDispatcher(),
            new FakeSubscriptionHub());
        await registry.DisposeAsync();

        await FluentActions.Invoking(() => registry.RegisterAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Invoking(() => registry.UnregisterAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private static EventEnvelope CreateObservedEnvelope(string eventId) =>
        new()
        {
            Id = eventId,
            Payload = Any.Pack(new StringValue { Value = eventId }),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
        };
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
    public async Task SubscribeAsync_ShouldValidateInputs()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        await FluentActions.Invoking(() => hub.SubscribeAsync("   ", _ => ValueTask.CompletedTask, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => hub.SubscribeAsync("actor-1", null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
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

public sealed class TestProjectionContext : IProjectionSessionContext
{
    public TestProjectionContext(string sessionId, string rootActorId, string projectionKind = "projection-test")
    {
        SessionId = sessionId;
        RootActorId = rootActorId;
        ProjectionKind = projectionKind;
    }

    public string SessionId { get; }
    public string RootActorId { get; }
    public string ProjectionKind { get; }
}

internal sealed class TestRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<TestProjectionContext>
{
    public TestRuntimeLease(TestProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context;
    }

    public TestProjectionContext Context { get; }
}

internal sealed class RecordingProjector : IProjectionProjector<TestProjectionContext>
{
    private readonly string _name;
    private readonly IList<string> _trace;

    public RecordingProjector(string name, IList<string> trace)
    {
        _name = name;
        _trace = trace;
    }

    public ValueTask ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add($"{_name}:project");
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingProjector : IProjectionProjector<TestProjectionContext>
{
    private readonly string _name;
    private readonly IList<string> _trace;
    private readonly Exception _exception;

    public ThrowingProjector(string name, IList<string> trace, Exception exception)
    {
        _name = name;
        _trace = trace;
        _exception = exception;
    }

    public ValueTask ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add($"{_name}:project");
        throw _exception;
    }
}

internal sealed class FakeDispatcher : IProjectionDispatcher<TestProjectionContext>
{
    private readonly IList<string> _trace;

    public FakeDispatcher(IList<string> trace) => _trace = trace;

    public Task DispatchAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add("dispatcher:dispatch");
        return Task.CompletedTask;
    }
}

internal sealed class FakeRegistry : IProjectionSubscriptionRegistry<TestProjectionContext, TestRuntimeLease>
{
    private readonly IList<string> _trace;

    public FakeRegistry(IList<string> trace) => _trace = trace;

    public Task RegisterAsync(TestRuntimeLease runtimeLease, CancellationToken ct = default)
    {
        _trace.Add("registry:register");
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(TestRuntimeLease runtimeLease, CancellationToken ct = default)
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

internal sealed class ThrowingDispatcher : IProjectionDispatcher<TestProjectionContext>
{
    private readonly Exception _exception;

    public ThrowingDispatcher(Exception exception) => _exception = exception;

    public Task DispatchAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        Task.FromException(_exception);
}

internal sealed class CapturingFailureReporter : IProjectionDispatchFailureReporter<TestProjectionContext>
{
    public List<(TestProjectionContext Context, EventEnvelope Envelope, Exception Exception, CancellationToken Token)> Calls { get; } = [];

    public ValueTask ReportAsync(TestProjectionContext context, EventEnvelope envelope, Exception exception, CancellationToken ct = default)
    {
        Calls.Add((context, envelope, exception, ct));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSubscriptionHub : IActorStreamSubscriptionHub<EventEnvelope>
{
    public string? LastActorId { get; private set; }
    public Func<EventEnvelope, ValueTask>? Handler { get; private set; }
    public FakeLease? LastLease { get; private set; }

    public Task<IActorStreamSubscriptionLease> SubscribeAsync(
        string actorId,
        Func<EventEnvelope, ValueTask> handler,
        CancellationToken ct = default)
    {
        LastActorId = actorId;
        Handler = handler;
        LastLease = new FakeLease(actorId);
        return Task.FromResult<IActorStreamSubscriptionLease>(LastLease);
    }

    public async Task EmitAsync(EventEnvelope envelope)
    {
        Handler.Should().NotBeNull();
        await Handler!(envelope);
    }
}

internal sealed class ThrowingSubscriptionHub : IActorStreamSubscriptionHub<EventEnvelope>
{
    private readonly Exception _exception;

    public ThrowingSubscriptionHub(Exception exception) => _exception = exception;

    public Task<IActorStreamSubscriptionLease> SubscribeAsync(
        string actorId,
        Func<EventEnvelope, ValueTask> handler,
        CancellationToken ct = default) =>
        Task.FromException<IActorStreamSubscriptionLease>(_exception);
}

internal sealed class FakeLease : IActorStreamSubscriptionLease
{
    public FakeLease(string actorId) => ActorId = actorId;

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

    public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);

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

public interface ITestReducerMarker;
public interface ITestProjectorMarker;
public interface ITestReducerGeneric<TContext, TTopology>;
public interface ITestProjectorGeneric<TContext, TTopology>;

public sealed class TestReducerExtension :
    ITestReducerMarker,
    ITestProjectorMarker,
    ITestReducerGeneric<TestProjectionContext, string>,
    ITestProjectorGeneric<TestProjectionContext, string>;
