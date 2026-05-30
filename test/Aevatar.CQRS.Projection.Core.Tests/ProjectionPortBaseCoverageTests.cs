using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class EventSinkProjectionLifecyclePortBaseTests
{
    [Fact]
    public void Constructor_ShouldThrow_WhenDependenciesAreNull()
    {
        var release = new TestReleaseService();
        var sessionEventHub = new TestSessionEventHub();

        Action noEnabledAccessor = () => _ = new TestEventSinkProjectionLifecyclePort(
            null!,
            release,
            sessionEventHub);
        Action noRelease = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            null!,
            sessionEventHub);
        Action noHub = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            release,
            null!);

        noEnabledAccessor.Should().Throw<ArgumentNullException>().WithParameterName("projectionEnabledAccessor");
        noRelease.Should().Throw<ArgumentNullException>().WithParameterName("releaseService");
        noHub.Should().Throw<ArgumentNullException>().WithParameterName("sessionEventHub");
    }

    [Fact]
    public void LifecyclePortBase_ShouldNotExposeEnsureProjectionSurface()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs"));
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain("Refactor (iter367/cluster-issue377):");
        source.Should().NotContain("protected async Task<TLeaseContract?> EnsureProjectionAsync");
        source.Should().NotContain("_activationService");
        source.Should().NotContain("IProjectionScopeActivationService<TRuntimeLease>");
    }

    [Fact]
    public async Task AttachLiveSinkAsync_ShouldSubscribeThroughSessionHub()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");
        var sink = new TestEventSink();

        var liveSinkLease = await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        await fixture.SessionEventHub.PublishHandler!("evt-1");

        liveSinkLease.Should().BeSameAs(fixture.SessionEventHub.LastSubscription);
        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        fixture.SessionEventHub.SubscribeCalls.Should().Be(1);
        fixture.SessionEventHub.LastRootActorId.Should().Be("actor-1");
        fixture.SessionEventHub.LastSessionId.Should().Be("session-1");
        sink.PushedEvents.Should().Equal("evt-1");
    }

    [Fact]
    public async Task AttachLiveSinkAsync_ShouldReturnIndependentExplicitLeases_ForSameSink()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");
        var sink = new TestEventSink();

        var firstLease = await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        var firstSubscription = fixture.SessionEventHub.LastSubscription!;

        var secondLease = await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        var secondSubscription = fixture.SessionEventHub.LastSubscription!;

        firstLease.Should().BeSameAs(firstSubscription);
        secondLease.Should().BeSameAs(secondSubscription);
        firstSubscription.DisposeCalls.Should().Be(0);
        secondSubscription.DisposeCalls.Should().Be(0);
        fixture.SessionEventHub.SubscribeCalls.Should().Be(2);

        await fixture.Service.DetachSinkPublicAsync(firstLease, CancellationToken.None);
        await fixture.Service.DetachSinkPublicAsync(secondLease, CancellationToken.None);

        firstSubscription.DisposeCalls.Should().Be(1);
        secondSubscription.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachLiveSinkAsync_ShouldDisposeStoredSubscription()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");
        var sink = new TestEventSink();

        var liveSinkLease = await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        var subscription = fixture.SessionEventHub.LastSubscription!;

        await fixture.Service.DetachSinkPublicAsync(liveSinkLease, CancellationToken.None);

        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        subscription.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task ReleaseActorProjectionAsync_ShouldDelegateToReleaseService()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");

        await fixture.Service.ReleaseProjectionPublicAsync(lease, CancellationToken.None);

        fixture.Release.Calls.Should().Be(1);
        fixture.Release.LastLease.Should().BeSameAs(lease);
    }

    [Fact]
    public void EventSinkProjectionLifecyclePortBase_Source_ShouldNotKeepSinkSubscriptionRegistry()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs"));
        var source = File.ReadAllText(sourcePath);

        source.Should().Contain(
            "Refactor (iter17/cluster-035): Old: ConcurrentDictionary registry. New: explicit IAsyncDisposable lease per attach.");
        source.Should().NotContain("ConcurrentDictionary<");
        source.Should().NotContain("_sinkSubscriptions");
    }

    private static Fixture CreateFixture(bool enabled)
    {
        var release = new TestReleaseService();
        var sessionEventHub = new TestSessionEventHub();
        var service = new TestEventSinkProjectionLifecyclePort(
            () => enabled,
            release,
            sessionEventHub);
        return new Fixture(service, release, sessionEventHub);
    }

    private sealed record Fixture(
        TestEventSinkProjectionLifecyclePort Service,
        TestReleaseService Release,
        TestSessionEventHub SessionEventHub);
}

internal sealed class TestEventSinkProjectionLifecyclePort
    : EventSinkProjectionLifecyclePortBase<TestPortRuntimeLease, TestPortRuntimeLease, string>
{
    public TestEventSinkProjectionLifecyclePort(
        Func<bool> projectionEnabledAccessor,
        IProjectionScopeReleaseService<TestPortRuntimeLease> releaseService,
        IProjectionSessionEventHub<string> sessionEventHub)
        : base(
            projectionEnabledAccessor,
            releaseService,
            sessionEventHub)
    {
    }

    public int ResolveRuntimeLeaseCalls { get; private set; }

    public Task<IAsyncDisposable?> AttachSinkPublicAsync(
        TestPortRuntimeLease lease,
        IEventSink<string> sink,
        CancellationToken ct = default) =>
        AttachLiveSinkAsync(lease, sink, ct);

    public Task DetachSinkPublicAsync(
        IAsyncDisposable? liveSinkLease,
        CancellationToken ct = default) =>
        DetachLiveSinkAsync(liveSinkLease, ct);

    public Task ReleaseProjectionPublicAsync(
        TestPortRuntimeLease lease,
        CancellationToken ct = default) =>
        ReleaseActorProjectionAsync(lease, ct);

    protected override TestPortRuntimeLease ResolveRuntimeLease(TestPortRuntimeLease lease)
    {
        ResolveRuntimeLeaseCalls++;
        return lease;
    }
}

// Refactor (iter367/cluster-issue377): Old pattern: test runtime lease implemented IProjectionPortSessionLease.
// Refactor (iter367/cluster-issue377): Old pattern: ScopeId aliased RootActorId for subscription routing.
// Refactor (iter367/cluster-issue377): New principle: test lease carries an IProjectionSessionContext.
// Refactor (iter367/cluster-issue377): New principle: lifecycle base reads RootActorId + SessionId from Context.
internal sealed class TestPortRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<string>,
      IProjectionContextRuntimeLease<TestPortSessionContext>
{
    public TestPortRuntimeLease(string rootActorId, string sessionId)
        : base(rootActorId)
    {
        Context = new TestPortSessionContext(rootActorId, "test-session", sessionId);
    }

    public string RootActorId => Context.RootActorId;

    public string SessionId => Context.SessionId;

    public TestPortSessionContext Context { get; }
}

internal sealed record TestPortSessionContext(
    string RootActorId,
    string ProjectionKind,
    string SessionId) : IProjectionSessionContext;

internal sealed class TestReleaseService : IProjectionScopeReleaseService<TestPortRuntimeLease>
{
    public int Calls { get; private set; }

    public TestPortRuntimeLease? LastLease { get; private set; }

    public Task ReleaseIfIdleAsync(TestPortRuntimeLease lease, CancellationToken ct = default)
    {
        Calls++;
        LastLease = lease;
        return Task.CompletedTask;
    }
}

internal sealed class TestSessionEventHub : IProjectionSessionEventHub<string>
{
    public int SubscribeCalls { get; private set; }

    public string? LastRootActorId { get; private set; }

    public string? LastSessionId { get; private set; }

    public Func<string, ValueTask>? PublishHandler { get; private set; }

    public TestSubscription? LastSubscription { get; private set; }

    public Task PublishAsync(string rootActorId, string sessionId, string evt, CancellationToken ct = default)
    {
        _ = rootActorId;
        _ = sessionId;
        _ = evt;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string rootActorId,
        string sessionId,
        Func<string, ValueTask> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubscribeCalls++;
        LastRootActorId = rootActorId;
        LastSessionId = sessionId;
        PublishHandler = handler;
        LastSubscription = new TestSubscription();
        return Task.FromResult<IAsyncDisposable>(LastSubscription);
    }
}

internal sealed class TestSubscription : IAsyncDisposable
{
    public int DisposeCalls { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestEventSink : IEventSink<string>
{
    public List<string> PushedEvents { get; } = [];

    public void Push(string evt) => PushedEvents.Add(evt);

    public ValueTask PushAsync(string evt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PushedEvents.Add(evt);
        return ValueTask.CompletedTask;
    }

    public void Complete()
    {
    }

    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = ct;
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
