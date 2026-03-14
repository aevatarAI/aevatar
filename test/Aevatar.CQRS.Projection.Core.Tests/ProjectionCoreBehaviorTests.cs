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

    [Fact]
    public async Task ProjectAsync_WhenProjectorFails_ShouldContinueOtherProjectorsAndThrowAggregate()
    {
        var trace = new List<string>();
        var coordinator = new ProjectionCoordinator<TestProjectionContext, string>(
        [
            new RecordingProjector("p1", trace),
            new ThrowingProjector("p2", trace, new InvalidOperationException("boom")),
            new RecordingProjector("p3", trace),
        ]);
        var context = new TestProjectionContext("projection-1", "actor-1");
        var envelope = new EventEnvelope { Id = "evt-1" };

        var act = () => coordinator.ProjectAsync(context, envelope, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ProjectionDispatchAggregateException>();
        ex.Which.Failures.Should().ContainSingle();
        ex.Which.Failures[0].ProjectorName.Should().Be(nameof(ThrowingProjector));
        ex.Which.Failures[0].ProjectorOrder.Should().Be(2);

        trace.Should().Equal(
            "p1:project",
            "p2:project",
            "p3:project");
    }

    [Fact]
    public async Task ProjectAsync_WhenMultipleProjectorsFail_ShouldAggregateAllFailuresWithOrder()
    {
        var trace = new List<string>();
        var coordinator = new ProjectionCoordinator<TestProjectionContext, string>(
        [
            new ThrowingProjector("p1", trace, new InvalidOperationException("boom-1")),
            new RecordingProjector("p2", trace),
            new ThrowingProjector("p3", trace, new ArgumentException("boom-2")),
        ]);
        var context = new TestProjectionContext("projection-1", "actor-1");
        var envelope = new EventEnvelope { Id = "evt-1" };

        var act = () => coordinator.ProjectAsync(context, envelope, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ProjectionDispatchAggregateException>();
        ex.Which.Failures.Should().HaveCount(2);
        ex.Which.Failures.Should().OnlyContain(x => x.ProjectorName == nameof(ThrowingProjector));
        ex.Which.Failures.Select(x => x.ProjectorOrder).Should().Equal(1, 3);

        trace.Should().Equal(
            "p1:project",
            "p2:project",
            "p3:project");
    }

    [Fact]
    public void ProjectionDispatchAggregateException_ShouldUseDefaultMessage_WhenNoFailures()
    {
        var ex = new ProjectionDispatchAggregateException([]);

        ex.Message.Should().Be("Projection dispatch failed.");
        ex.InnerException.Should().BeNull();
        ex.Failures.Should().BeEmpty();
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

        await hub.EmitAsync(CreateObservedEnvelope("evt-1"));
        dispatcher.DispatchCount.Should().Be(1);

        await registry.UnregisterAsync(context, CancellationToken.None);

        context.StreamSubscriptionLease.Should().BeNull();
        hub.LastLease.Should().NotBeNull();
        hub.LastLease!.Disposed.Should().BeTrue();
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

    [Fact]
    public async Task RegisterAsync_ShouldForwardCancelableDispatchToken()
    {
        var dispatcher = new TokenCapturingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");

        await registry.RegisterAsync(context, CancellationToken.None);
        await hub.EmitAsync(CreateObservedEnvelope("evt-1"));

        dispatcher.LastToken.Should().NotBeNull();
        dispatcher.LastToken!.Value.CanBeCanceled.Should().BeTrue();

        await registry.UnregisterAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task RegisterAsync_ShouldReportFailure_WhenDispatcherThrows()
    {
        var dispatcher = new ThrowingDispatcher(new InvalidOperationException("boom"));
        var reporter = new CapturingFailureReporter();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(
            dispatcher,
            hub,
            reporter);
        var context = new TestProjectionContext("projection-1", "actor-1");
        var envelope = CreateObservedEnvelope("evt-1");

        await registry.RegisterAsync(context, CancellationToken.None);
        await hub.EmitAsync(envelope);

        reporter.Calls.Should().ContainSingle();
        reporter.Calls[0].Context.Should().BeSameAs(context);
        reporter.Calls[0].Envelope.Should().BeSameAs(envelope);
        reporter.Calls[0].Exception.Should().BeOfType<InvalidOperationException>();
        reporter.Calls[0].Token.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrow_WhenRegistryDisposed()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");
        await registry.DisposeAsync();

        Func<Task> act = () => registry.RegisterAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task RegisterAsync_ShouldRethrow_WhenHubSubscriptionFails()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new ThrowingSubscriptionHub(new InvalidOperationException("subscribe-failed"));
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");

        Func<Task> act = () => registry.RegisterAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("subscribe-failed");
        context.StreamSubscriptionLease.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_ShouldIgnoreDispatchFailures_WhenReporterIsMissing()
    {
        var dispatcher = new ThrowingDispatcher(new InvalidOperationException("boom"));
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");

        await registry.RegisterAsync(context, CancellationToken.None);

        Func<Task> act = () => hub.EmitAsync(CreateObservedEnvelope("evt-1"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RegisterAsync_ShouldIgnoreReporterFailures()
    {
        var dispatcher = new ThrowingDispatcher(new InvalidOperationException("boom"));
        var reporter = new ThrowingFailureReporter(new InvalidOperationException("report-failed"));
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub, reporter);
        var context = new TestProjectionContext("projection-1", "actor-1");

        await registry.RegisterAsync(context, CancellationToken.None);

        Func<Task> act = () => hub.EmitAsync(new EventEnvelope { Id = "evt-1" });
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UnregisterAsync_ShouldThrow_WhenCancellationRequested()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => registry.UnregisterAsync(context, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task UnregisterAsync_ShouldThrow_WhenDisposed()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");
        await registry.DisposeAsync();

        Func<Task> act = () => registry.UnregisterAsync(context, CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task UnregisterAsync_ShouldReturn_WhenContextIdentityIsIncomplete()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext(" ", "actor-1");

        Func<Task> act = () => registry.UnregisterAsync(context, CancellationToken.None);

        await act.Should().NotThrowAsync();
        hub.LastLease.Should().BeNull();
    }

    [Fact]
    public async Task UnregisterAsync_ShouldReturn_WhenLeaseMissing()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");

        Func<Task> act = () => registry.UnregisterAsync(context, CancellationToken.None);

        await act.Should().NotThrowAsync();
        hub.LastLease.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_ShouldSkipDispatch_WhenLinkedTokenIsCancelled()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);
        var context = new TestProjectionContext("projection-1", "actor-1");
        using var cts = new CancellationTokenSource();
        await registry.RegisterAsync(context, cts.Token);
        cts.Cancel();

        await hub.EmitAsync(CreateObservedEnvelope("evt-late"));
        await registry.UnregisterAsync(context, CancellationToken.None);

        dispatcher.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_ShouldThrow_WhenContextIsNull()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);

        Func<Task> act = () => registry.RegisterAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task UnregisterAsync_ShouldThrow_WhenContextIsNull()
    {
        var dispatcher = new CountingDispatcher();
        var hub = new FakeSubscriptionHub();
        var registry = new ProjectionSubscriptionRegistry<TestProjectionContext>(dispatcher, hub);

        Func<Task> act = () => registry.UnregisterAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
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
    public async Task SubscribeAsync_ShouldThrow_WhenActorIdIsBlank()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        Func<Task> act = () => hub.SubscribeAsync("   ", _ => ValueTask.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldThrow_WhenHandlerIsNull()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        Func<Task> act = () => hub.SubscribeAsync("actor-1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldThrow_WhenHubDisposed()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);
        await hub.DisposeAsync();

        Func<Task> act = () => hub.SubscribeAsync("actor-1", _ => ValueTask.CompletedTask, CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task SubscribeAsync_ShouldSwallowHandlerExceptions()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        await hub.SubscribeAsync(
            "actor-1",
            _ => throw new InvalidOperationException("handler-failed"),
            CancellationToken.None);

        Func<Task> act = () => provider.Stream.DeliverAsync(new EventEnvelope { Id = "evt-1" });

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LeaseDispose_ShouldSwallowUnderlyingDisposeExceptions()
    {
        var provider = new FakeStreamProvider();
        provider.Stream.Subscription.ThrowOnDispose = true;
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        var lease = await hub.SubscribeAsync("actor-1", _ => ValueTask.CompletedTask, CancellationToken.None);
        Func<Task> act = async () => await lease.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        var provider = new FakeStreamProvider();
        var hub = new ActorStreamSubscriptionHub<EventEnvelope>(provider);

        await hub.DisposeAsync();
        Func<Task> act = async () => await hub.DisposeAsync();

        await act.Should().NotThrowAsync();
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

internal sealed class ThrowingProjector : IProjectionProjector<TestProjectionContext, string>
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

    public ValueTask InitializeAsync(TestProjectionContext context, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask ProjectAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        _trace.Add($"{_name}:project");
        throw _exception;
    }

    public ValueTask CompleteAsync(TestProjectionContext context, string topology, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
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

internal sealed class TokenCapturingDispatcher : IProjectionDispatcher<TestProjectionContext>
{
    public CancellationToken? LastToken { get; private set; }

    public Task DispatchAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        LastToken = ct;
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingDispatcher : IProjectionDispatcher<TestProjectionContext>
{
    private readonly Exception _exception;

    public ThrowingDispatcher(Exception exception)
    {
        _exception = exception;
    }

    public Task DispatchAsync(TestProjectionContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        throw _exception;
    }
}

internal sealed class CapturingFailureReporter : IProjectionDispatchFailureReporter<TestProjectionContext>
{
    public List<(TestProjectionContext Context, EventEnvelope Envelope, Exception Exception, CancellationToken Token)> Calls { get; } = [];

    public ValueTask ReportAsync(
        TestProjectionContext context,
        EventEnvelope envelope,
        Exception exception,
        CancellationToken ct = default)
    {
        Calls.Add((context, envelope, exception, ct));
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingFailureReporter : IProjectionDispatchFailureReporter<TestProjectionContext>
{
    private readonly Exception _exception;

    public ThrowingFailureReporter(Exception exception)
    {
        _exception = exception;
    }

    public ValueTask ReportAsync(
        TestProjectionContext context,
        EventEnvelope envelope,
        Exception exception,
        CancellationToken ct = default)
    {
        throw _exception;
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

    public ThrowingSubscriptionHub(Exception exception)
    {
        _exception = exception;
    }

    public Task<IActorStreamSubscriptionLease> SubscribeAsync(
        string actorId,
        Func<EventEnvelope, ValueTask> handler,
        CancellationToken ct = default)
    {
        _ = actorId;
        _ = handler;
        _ = ct;
        throw _exception;
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

    public async Task DeliverAsync(EventEnvelope envelope)
    {
        _eventEnvelopeHandler.Should().NotBeNull();
        await _eventEnvelopeHandler!(envelope);
    }
}

internal sealed class FakeAsyncSubscription : IAsyncDisposable
{
    public bool Disposed { get; private set; }
    public bool ThrowOnDispose { get; set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        if (ThrowOnDispose)
            throw new InvalidOperationException("Subscription dispose failure.");
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
