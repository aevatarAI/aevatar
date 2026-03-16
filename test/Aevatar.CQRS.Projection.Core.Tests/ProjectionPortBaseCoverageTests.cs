using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class EventSinkProjectionLifecyclePortBaseTests
{
    [Fact]
    public void Constructor_ShouldThrow_WhenDependenciesAreNull()
    {
        var activation = new TestActivationService();
        var release = new TestReleaseService();
        var sinkManager = new TestSinkSubscriptionManager();
        var forwarder = new TestLiveSinkForwarder();

        Action noEnabledAccessor = () => _ = new TestEventSinkProjectionLifecyclePort(
            null!,
            activation,
            release,
            sinkManager,
            forwarder);
        Action noActivation = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            null!,
            release,
            sinkManager,
            forwarder);
        Action noRelease = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            activation,
            null!,
            sinkManager,
            forwarder);
        Action noSinkManager = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            activation,
            release,
            null!,
            forwarder);
        Action noForwarder = () => _ = new TestEventSinkProjectionLifecyclePort(
            () => true,
            activation,
            release,
            sinkManager,
            null!);

        noEnabledAccessor.Should().Throw<ArgumentNullException>().WithParameterName("projectionEnabledAccessor");
        noActivation.Should().Throw<ArgumentNullException>().WithParameterName("activationService");
        noRelease.Should().Throw<ArgumentNullException>().WithParameterName("releaseService");
        noSinkManager.Should().Throw<ArgumentNullException>().WithParameterName("sinkSubscriptionManager");
        noForwarder.Should().Throw<ArgumentNullException>().WithParameterName("liveSinkForwarder");
    }

    [Fact]
    public async Task EnsureProjectionAsync_ShouldReturnNull_WhenDisabledOrRootEntityIdInvalid()
    {
        var disabledFixture = CreateLifecycleFixture(enabled: false);
        var disabledResult = await disabledFixture.Service.EnsureProjectionPublicAsync(
            "actor-1",
            "projection",
            "cmd-1",
            CancellationToken.None);
        disabledResult.Should().BeNull();
        disabledFixture.Activation.Calls.Should().Be(0);

        var blankRootFixture = CreateLifecycleFixture(enabled: true);
        var blankRootResult = await blankRootFixture.Service.EnsureProjectionPublicAsync(
            "   ",
            "projection",
            "cmd-1",
            CancellationToken.None);
        blankRootResult.Should().BeNull();
        blankRootFixture.Activation.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EnsureProjectionAsync_ShouldCallActivation_WhenEnabled()
    {
        var fixture = CreateLifecycleFixture(enabled: true);

        var lease = await fixture.Service.EnsureProjectionPublicAsync(
            "actor-1",
            "projection-1",
            "cmd-1",
            CancellationToken.None);

        lease.Should().BeSameAs(fixture.Activation.LeaseToReturn);
        fixture.Activation.Calls.Should().Be(1);
        fixture.Activation.LastRootEntityId.Should().Be("actor-1");
        fixture.Activation.LastProjectionName.Should().Be("projection-1");
        fixture.Activation.LastCommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task AttachSinkAsync_ShouldValidateArgumentsAndCancellation()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestPortRuntimeLease("lease-1");
        var sink = new TestEventSink();

        Func<Task> noLease = () => fixture.Service.AttachSinkPublicAsync(null!, sink, CancellationToken.None);
        Func<Task> noSink = () => fixture.Service.AttachSinkPublicAsync(lease, null!, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> canceled = () => fixture.Service.AttachSinkPublicAsync(lease, sink, cts.Token);

        await noLease.Should().ThrowAsync<ArgumentNullException>().WithParameterName("lease");
        await noSink.Should().ThrowAsync<ArgumentNullException>().WithParameterName("sink");
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AttachSinkAsync_ShouldNoOp_WhenProjectionDisabled()
    {
        var fixture = CreateLifecycleFixture(enabled: false);
        var lease = new TestPortRuntimeLease("lease-1");
        var sink = new TestEventSink();

        await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);

        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(0);
        fixture.SinkManager.AttachCalls.Should().Be(0);
    }

    [Fact]
    public async Task AttachSinkAsync_ShouldAttachAndForwardLiveEvents_WhenEnabled()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestPortRuntimeLease("lease-1");
        var sink = new TestEventSink();

        await fixture.Service.AttachSinkPublicAsync(lease, sink, CancellationToken.None);
        await fixture.SinkManager.LastHandler!("evt-1");

        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        fixture.SinkManager.AttachCalls.Should().Be(1);
        fixture.SinkManager.LastLease.Should().BeSameAs(lease);
        fixture.SinkManager.LastSink.Should().BeSameAs(sink);
        fixture.Forwarder.ForwardCalls.Should().Be(1);
        fixture.Forwarder.LastLease.Should().BeSameAs(lease);
        fixture.Forwarder.LastSink.Should().BeSameAs(sink);
        fixture.Forwarder.LastEvent.Should().Be("evt-1");
        fixture.Forwarder.LastToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task DetachSinkAsync_ShouldValidateArgumentsAndCancellation()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestPortRuntimeLease("lease-1");
        var sink = new TestEventSink();

        Func<Task> noLease = () => fixture.Service.DetachSinkPublicAsync(null!, sink, CancellationToken.None);
        Func<Task> noSink = () => fixture.Service.DetachSinkPublicAsync(lease, null!, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> canceled = () => fixture.Service.DetachSinkPublicAsync(lease, sink, cts.Token);

        await noLease.Should().ThrowAsync<ArgumentNullException>().WithParameterName("lease");
        await noSink.Should().ThrowAsync<ArgumentNullException>().WithParameterName("sink");
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DetachSinkAsync_ShouldRespectProjectionEnabledGate()
    {
        var disabledFixture = CreateLifecycleFixture(enabled: false);
        var disabledLease = new TestPortRuntimeLease("lease-disabled");
        var disabledSink = new TestEventSink();
        await disabledFixture.Service.DetachSinkPublicAsync(disabledLease, disabledSink, CancellationToken.None);
        disabledFixture.SinkManager.DetachCalls.Should().Be(0);

        var enabledFixture = CreateLifecycleFixture(enabled: true);
        var enabledLease = new TestPortRuntimeLease("lease-enabled");
        var enabledSink = new TestEventSink();
        await enabledFixture.Service.DetachSinkPublicAsync(enabledLease, enabledSink, CancellationToken.None);
        enabledFixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        enabledFixture.SinkManager.DetachCalls.Should().Be(1);
        enabledFixture.SinkManager.LastLease.Should().BeSameAs(enabledLease);
        enabledFixture.SinkManager.LastSink.Should().BeSameAs(enabledSink);
    }

    [Fact]
    public async Task ReleaseProjectionAsync_ShouldValidateArgumentsAndCancellation()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestPortRuntimeLease("lease-1");

        Func<Task> noLease = () => fixture.Service.ReleaseProjectionPublicAsync(null!, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> canceled = () => fixture.Service.ReleaseProjectionPublicAsync(lease, cts.Token);

        await noLease.Should().ThrowAsync<ArgumentNullException>().WithParameterName("lease");
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ReleaseProjectionAsync_ShouldRespectProjectionEnabledGate()
    {
        var disabledFixture = CreateLifecycleFixture(enabled: false);
        var disabledLease = new TestPortRuntimeLease("lease-disabled");
        await disabledFixture.Service.ReleaseProjectionPublicAsync(disabledLease, CancellationToken.None);
        disabledFixture.Release.Calls.Should().Be(0);

        var enabledFixture = CreateLifecycleFixture(enabled: true);
        var enabledLease = new TestPortRuntimeLease("lease-enabled");
        await enabledFixture.Service.ReleaseProjectionPublicAsync(enabledLease, CancellationToken.None);
        enabledFixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        enabledFixture.Release.Calls.Should().Be(1);
        enabledFixture.Release.LastLease.Should().BeSameAs(enabledLease);
    }

    private static LifecycleFixture CreateLifecycleFixture(bool enabled)
    {
        var activation = new TestActivationService();
        var release = new TestReleaseService();
        var sinkManager = new TestSinkSubscriptionManager();
        var forwarder = new TestLiveSinkForwarder();
        var service = new TestEventSinkProjectionLifecyclePort(
            () => enabled,
            activation,
            release,
            sinkManager,
            forwarder);
        return new LifecycleFixture(service, activation, release, sinkManager, forwarder);
    }

    private sealed record LifecycleFixture(
        TestEventSinkProjectionLifecyclePort Service,
        TestActivationService Activation,
        TestReleaseService Release,
        TestSinkSubscriptionManager SinkManager,
        TestLiveSinkForwarder Forwarder);
}

internal sealed class TestEventSinkProjectionLifecyclePort
    : EventSinkProjectionLifecyclePortBase<TestLeaseContract, TestPortRuntimeLease, string>
{
    public TestEventSinkProjectionLifecyclePort(
        Func<bool> projectionEnabledAccessor,
        IProjectionSessionActivationService<TestPortRuntimeLease> activationService,
        IProjectionSessionReleaseService<TestPortRuntimeLease> releaseService,
        IEventSinkProjectionSubscriptionManager<TestPortRuntimeLease, string> sinkSubscriptionManager,
        IEventSinkProjectionLiveForwarder<TestPortRuntimeLease, string> liveSinkForwarder)
        : base(
            projectionEnabledAccessor,
            activationService,
            releaseService,
            sinkSubscriptionManager,
            liveSinkForwarder)
    {
    }

    public int ResolveRuntimeLeaseCalls { get; private set; }

    public Task<TestLeaseContract?> EnsureProjectionPublicAsync(
        string rootEntityId,
        string projectionName,
        string commandId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionSessionStartRequest
            {
                RootActorId = rootEntityId,
                ProjectionKind = projectionName,
                SessionId = commandId,
            },
            ct);

    public Task AttachSinkPublicAsync(
        TestLeaseContract lease,
        IEventSink<string> sink,
        CancellationToken ct = default) =>
        AttachLiveSinkAsync(lease, sink, ct);

    public Task DetachSinkPublicAsync(
        TestLeaseContract lease,
        IEventSink<string> sink,
        CancellationToken ct = default) =>
        DetachLiveSinkAsync(lease, sink, ct);

    public Task ReleaseProjectionPublicAsync(
        TestLeaseContract lease,
        CancellationToken ct = default) =>
        ReleaseActorProjectionAsync(lease, ct);

    protected override TestPortRuntimeLease ResolveRuntimeLease(TestLeaseContract lease)
    {
        ResolveRuntimeLeaseCalls++;
        return (TestPortRuntimeLease)lease;
    }
}

internal class TestLeaseContract
{
    protected TestLeaseContract(string leaseId)
    {
        LeaseId = leaseId;
    }

    public string LeaseId { get; }
}

internal sealed class TestPortRuntimeLease : TestLeaseContract, IProjectionRuntimeLease
{
    public TestPortRuntimeLease(string leaseId)
        : base(leaseId)
    {
    }

    public string RootEntityId => LeaseId;

    public int GetLiveSinkSubscriptionCount() => 0;
}

internal sealed class TestActivationService : IProjectionSessionActivationService<TestPortRuntimeLease>
{
    public int Calls { get; private set; }
    public string? LastRootEntityId { get; private set; }
    public string? LastProjectionName { get; private set; }
    public string? LastCommandId { get; private set; }
    public TestPortRuntimeLease LeaseToReturn { get; } = new("lease-activation");

    public Task<TestPortRuntimeLease> EnsureAsync(
        ProjectionSessionStartRequest request,
        CancellationToken ct = default)
    {
        Calls++;
        LastRootEntityId = request.RootActorId;
        LastProjectionName = request.ProjectionKind;
        LastCommandId = request.SessionId;
        return Task.FromResult(LeaseToReturn);
    }
}

internal sealed class TestReleaseService : IProjectionSessionReleaseService<TestPortRuntimeLease>
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

internal sealed class TestSinkSubscriptionManager
    : IEventSinkProjectionSubscriptionManager<TestPortRuntimeLease, string>
{
    public int AttachCalls { get; private set; }
    public int DetachCalls { get; private set; }
    public TestPortRuntimeLease? LastLease { get; private set; }
    public IEventSink<string>? LastSink { get; private set; }
    public Func<string, ValueTask>? LastHandler { get; private set; }

    public Task AttachOrReplaceAsync(
        TestPortRuntimeLease lease,
        IEventSink<string> sink,
        Func<string, ValueTask> handler,
        CancellationToken ct = default)
    {
        AttachCalls++;
        LastLease = lease;
        LastSink = sink;
        LastHandler = handler;
        return Task.CompletedTask;
    }

    public Task DetachAsync(
        TestPortRuntimeLease lease,
        IEventSink<string> sink,
        CancellationToken ct = default)
    {
        DetachCalls++;
        LastLease = lease;
        LastSink = sink;
        return Task.CompletedTask;
    }
}

internal sealed class TestLiveSinkForwarder : IEventSinkProjectionLiveForwarder<TestPortRuntimeLease, string>
{
    public int ForwardCalls { get; private set; }
    public TestPortRuntimeLease? LastLease { get; private set; }
    public IEventSink<string>? LastSink { get; private set; }
    public string? LastEvent { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public ValueTask ForwardAsync(
        TestPortRuntimeLease lease,
        IEventSink<string> sink,
        string evt,
        CancellationToken ct = default)
    {
        ForwardCalls++;
        LastLease = lease;
        LastSink = sink;
        LastEvent = evt;
        LastToken = ct;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestEventSink : IEventSink<string>
{
    public void Push(string evt)
    {
    }

    public ValueTask PushAsync(string evt, CancellationToken ct = default) => ValueTask.CompletedTask;

    public void Complete()
    {
    }

    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
