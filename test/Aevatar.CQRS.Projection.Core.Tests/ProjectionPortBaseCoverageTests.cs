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
        var activation = new TestActivationService();
        var release = new TestReleaseService();
        var sessionEventHub = new TestSessionEventHub();

        Action noEnabledAccessor = () => _ = new TestEventSinkProjectionLifecyclePort(
            null!,
            activation,
            release,
            sessionEventHub);
        Action noActivation = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            null!,
            release,
            sessionEventHub);
        Action noRelease = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            activation,
            null!,
            sessionEventHub);
        Action noHub = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            activation,
            release,
            null!);

        noEnabledAccessor.Should().Throw<ArgumentNullException>().WithParameterName("projectionEnabledAccessor");
        noActivation.Should().Throw<ArgumentNullException>().WithParameterName("activationService");
        noRelease.Should().Throw<ArgumentNullException>().WithParameterName("releaseService");
        noHub.Should().Throw<ArgumentNullException>().WithParameterName("sessionEventHub");
    }

    [Fact]
    public async Task EnsureProjectionAsync_ShouldReturnNull_WhenDisabledOrRootActorIdInvalid()
    {
        var disabledFixture = CreateFixture(enabled: false);
        var disabledResult = await disabledFixture.Service.EnsureProjectionPublicAsync(
            "actor-1",
            "projection",
            "session-1",
            CancellationToken.None);
        disabledResult.Should().BeNull();
        disabledFixture.Activation.Calls.Should().Be(0);

        var blankRootFixture = CreateFixture(enabled: true);
        var blankRootResult = await blankRootFixture.Service.EnsureProjectionPublicAsync(
            "   ",
            "projection",
            "session-1",
            CancellationToken.None);
        blankRootResult.Should().BeNull();
        blankRootFixture.Activation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureProjectionAsync_ShouldCallActivation_WhenEnabled()
    {
        var fixture = CreateFixture(enabled: true);

        var lease = await fixture.Service.EnsureProjectionPublicAsync(
            "actor-1",
            "projection-1",
            "session-1",
            CancellationToken.None);

        lease.Should().BeSameAs(fixture.Activation.LeaseToReturn);
        fixture.Activation.Calls.Should().Be(1);
        fixture.Activation.LastRequest.Should().NotBeNull();
        fixture.Activation.LastRequest!.RootActorId.Should().Be("actor-1");
        fixture.Activation.LastRequest.ProjectionKind.Should().Be("projection-1");
        fixture.Activation.LastRequest.SessionId.Should().Be("session-1");
    }

    [Fact]
    public async Task AttachLiveSinkAsync_ShouldSubscribeThroughSessionHub()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");
        var sink = new TestEventSink();

        await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        await fixture.SessionEventHub.PublishHandler!("evt-1");

        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        fixture.SessionEventHub.SubscribeCalls.Should().Be(1);
        fixture.SessionEventHub.LastScopeId.Should().Be("actor-1");
        fixture.SessionEventHub.LastSessionId.Should().Be("session-1");
        sink.PushedEvents.Should().Equal("evt-1");
    }

    [Fact]
    public async Task AttachLiveSinkAsync_ShouldReplacePreviousSubscription_ForSameSink()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");
        var sink = new TestEventSink();

        await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        var firstSubscription = fixture.SessionEventHub.LastSubscription!;

        await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);

        firstSubscription.DisposeCalls.Should().Be(1);
        fixture.SessionEventHub.SubscribeCalls.Should().Be(2);
    }

    [Fact]
    public async Task DetachLiveSinkAsync_ShouldDisposeStoredSubscription()
    {
        var fixture = CreateFixture(enabled: true);
        var lease = new TestPortRuntimeLease("actor-1", "session-1");
        var sink = new TestEventSink();

        await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        var subscription = fixture.SessionEventHub.LastSubscription!;

        await fixture.Service.DetachSinkPublicAsync(lease, sink, CancellationToken.None);

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

    private static Fixture CreateFixture(bool enabled)
    {
        var activation = new TestActivationService();
        var release = new TestReleaseService();
        var sessionEventHub = new TestSessionEventHub();
        var service = new TestEventSinkProjectionLifecyclePort(
            () => enabled,
            activation,
            release,
            sessionEventHub);
        return new Fixture(service, activation, release, sessionEventHub);
    }

    private sealed record Fixture(
        TestEventSinkProjectionLifecyclePort Service,
        TestActivationService Activation,
        TestReleaseService Release,
        TestSessionEventHub SessionEventHub);
}

internal sealed class TestEventSinkProjectionLifecyclePort
    : EventSinkProjectionLifecyclePortBase<TestPortRuntimeLease, TestPortRuntimeLease, string>
{
    public TestEventSinkProjectionLifecyclePort(
        Func<bool> projectionEnabledAccessor,
        IProjectionScopeActivationService<TestPortRuntimeLease> activationService,
        IProjectionScopeReleaseService<TestPortRuntimeLease> releaseService,
        IProjectionSessionEventHub<string> sessionEventHub)
        : base(
            projectionEnabledAccessor,
            activationService,
            releaseService,
            sessionEventHub)
    {
    }

    public int ResolveRuntimeLeaseCalls { get; private set; }

    public Task<TestPortRuntimeLease?> EnsureProjectionPublicAsync(
        string rootActorId,
        string projectionKind,
        string sessionId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);

    public Task AttachSinkPublicAsync(
        TestPortRuntimeLease lease,
        IEventSink<string> sink,
        CancellationToken ct = default) =>
        AttachLiveSinkAsync(lease, sink, ct);

    public Task DetachSinkPublicAsync(
        TestPortRuntimeLease lease,
        IEventSink<string> sink,
        CancellationToken ct = default) =>
        DetachLiveSinkAsync(lease, sink, ct);

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

internal sealed class TestPortRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<string>,
      IProjectionPortSessionLease
{
    public TestPortRuntimeLease(string rootActorId, string sessionId)
        : base(rootActorId)
    {
        RootActorId = rootActorId;
        SessionId = sessionId;
    }

    public string RootActorId { get; }

    public string ScopeId => RootActorId;

    public string SessionId { get; }
}

internal sealed class TestActivationService : IProjectionScopeActivationService<TestPortRuntimeLease>
{
    public int Calls { get; private set; }

    public ProjectionScopeStartRequest? LastRequest { get; private set; }

    public TestPortRuntimeLease LeaseToReturn { get; } = new("lease-actor", "lease-session");

    public Task<TestPortRuntimeLease> EnsureAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(LeaseToReturn);
    }
}

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

    public string? LastScopeId { get; private set; }

    public string? LastSessionId { get; private set; }

    public Func<string, ValueTask>? PublishHandler { get; private set; }

    public TestSubscription? LastSubscription { get; private set; }

    public Task PublishAsync(string scopeId, string sessionId, string evt, CancellationToken ct = default)
    {
        _ = scopeId;
        _ = sessionId;
        _ = evt;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        string scopeId,
        string sessionId,
        Func<string, ValueTask> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubscribeCalls++;
        LastScopeId = scopeId;
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
