using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public class ProjectionQueryPortServiceBaseTests
{
    [Fact]
    public void Constructor_ShouldThrow_WhenQueryEnabledAccessorIsNull()
    {
        Action act = () => _ = new TestProjectionQueryPortService(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("queryEnabledAccessor");
    }

    [Fact]
    public async Task QueryMethods_ShouldShortCircuit_WhenQueryIsDisabled()
    {
        var service = new TestProjectionQueryPortService(() => false);

        var snapshot = await service.GetSnapshotPublicAsync("actor-1", CancellationToken.None);
        var snapshots = await service.ListSnapshotsPublicAsync(50, CancellationToken.None);
        var timeline = await service.ListTimelinePublicAsync("actor-1", 50, CancellationToken.None);
        var edges = await service.GetGraphEdgesPublicAsync("actor-1", 50, CancellationToken.None);
        var subgraph = await service.GetGraphSubgraphPublicAsync("actor-1", 2, 50, CancellationToken.None);

        snapshot.Should().BeNull();
        snapshots.Should().BeEmpty();
        timeline.Should().BeEmpty();
        edges.Should().BeEmpty();
        subgraph.Should().BeEquivalentTo(new TestGraphSubgraph("actor-1", true));
        service.ReadSnapshotCalls.Should().Be(0);
        service.ReadSnapshotsCalls.Should().Be(0);
        service.ReadTimelineCalls.Should().Be(0);
        service.ReadGraphEdgesCalls.Should().Be(0);
        service.ReadGraphSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task EntityScopedMethods_ShouldShortCircuit_WhenEntityIdIsBlank()
    {
        var service = new TestProjectionQueryPortService(() => true);

        var snapshot = await service.GetSnapshotPublicAsync("   ", CancellationToken.None);
        var timeline = await service.ListTimelinePublicAsync(" ", 20, CancellationToken.None);
        var edges = await service.GetGraphEdgesPublicAsync("\t", 20, CancellationToken.None);
        var subgraph = await service.GetGraphSubgraphPublicAsync(" ", 3, 20, CancellationToken.None);

        snapshot.Should().BeNull();
        timeline.Should().BeEmpty();
        edges.Should().BeEmpty();
        subgraph.Should().BeEquivalentTo(new TestGraphSubgraph(" ", true));
        service.ReadSnapshotCalls.Should().Be(0);
        service.ReadTimelineCalls.Should().Be(0);
        service.ReadGraphEdgesCalls.Should().Be(0);
        service.ReadGraphSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryMethods_ShouldDispatchToCore_WhenEnabledAndInputsAreValid()
    {
        var service = new TestProjectionQueryPortService(() => true);

        var snapshot = await service.GetSnapshotPublicAsync("actor-1", CancellationToken.None);
        var snapshots = await service.ListSnapshotsPublicAsync(10, CancellationToken.None);
        var timeline = await service.ListTimelinePublicAsync("actor-1", 10, CancellationToken.None);
        var edges = await service.GetGraphEdgesPublicAsync("actor-1", 10, CancellationToken.None);
        var subgraph = await service.GetGraphSubgraphPublicAsync("actor-1", 2, 10, CancellationToken.None);

        snapshot.Should().Be("snapshot:actor-1");
        snapshots.Should().Equal("snapshot-a", "snapshot-b");
        timeline.Should().Equal("timeline-a");
        edges.Should().Equal("edge-a");
        subgraph.Should().BeEquivalentTo(new TestGraphSubgraph("actor-1", false));
        service.ReadSnapshotCalls.Should().Be(1);
        service.ReadSnapshotsCalls.Should().Be(1);
        service.ReadTimelineCalls.Should().Be(1);
        service.ReadGraphEdgesCalls.Should().Be(1);
        service.ReadGraphSubgraphCalls.Should().Be(1);
    }
}

public class ProjectionLifecyclePortServiceBaseTests
{
    [Fact]
    public void Constructor_ShouldThrow_WhenDependenciesAreNull()
    {
        var activation = new TestActivationService();
        var release = new TestReleaseService();
        var sinkManager = new TestSinkSubscriptionManager();
        var forwarder = new TestLiveSinkForwarder();

        Action noEnabledAccessor = () => _ = new TestProjectionLifecyclePortService(
            null!,
            activation,
            release,
            sinkManager,
            forwarder);
        Action noActivation = () => _ = new TestProjectionLifecyclePortService(
            () => true,
            null!,
            release,
            sinkManager,
            forwarder);
        Action noRelease = () => _ = new TestProjectionLifecyclePortService(
            () => true,
            activation,
            null!,
            sinkManager,
            forwarder);
        Action noSinkManager = () => _ = new TestProjectionLifecyclePortService(
            () => true,
            activation,
            release,
            null!,
            forwarder);
        Action noForwarder = () => _ = new TestProjectionLifecyclePortService(
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
            "{}",
            "cmd-1",
            CancellationToken.None);
        disabledResult.Should().BeNull();
        disabledFixture.Activation.Calls.Should().Be(0);

        var blankRootFixture = CreateLifecycleFixture(enabled: true);
        var blankRootResult = await blankRootFixture.Service.EnsureProjectionPublicAsync(
            "   ",
            "projection",
            "{}",
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
            "{\"k\":\"v\"}",
            "cmd-1",
            CancellationToken.None);

        lease.Should().BeSameAs(fixture.Activation.LeaseToReturn);
        fixture.Activation.Calls.Should().Be(1);
        fixture.Activation.LastRootEntityId.Should().Be("actor-1");
        fixture.Activation.LastProjectionName.Should().Be("projection-1");
        fixture.Activation.LastInput.Should().Be("{\"k\":\"v\"}");
        fixture.Activation.LastCommandId.Should().Be("cmd-1");
    }

    [Fact]
    public async Task AttachSinkAsync_ShouldValidateArgumentsAndCancellation()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestRuntimeLease("lease-1");

        Func<Task> noLease = () => fixture.Service.AttachSinkPublicAsync(null!, "sink-1", CancellationToken.None);
        Func<Task> noSink = () => fixture.Service.AttachSinkPublicAsync(lease, null!, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> canceled = () => fixture.Service.AttachSinkPublicAsync(lease, "sink-1", cts.Token);

        await noLease.Should().ThrowAsync<ArgumentNullException>().WithParameterName("lease");
        await noSink.Should().ThrowAsync<ArgumentNullException>().WithParameterName("sink");
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AttachSinkAsync_ShouldNoOp_WhenProjectionDisabled()
    {
        var fixture = CreateLifecycleFixture(enabled: false);
        var lease = new TestRuntimeLease("lease-1");

        await fixture.Service.AttachSinkPublicAsync(lease, "sink-1", CancellationToken.None);

        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(0);
        fixture.SinkManager.AttachCalls.Should().Be(0);
    }

    [Fact]
    public async Task AttachSinkAsync_ShouldAttachAndForwardLiveEvents_WhenEnabled()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestRuntimeLease("lease-1");

        await fixture.Service.AttachSinkPublicAsync(lease, "sink-1", CancellationToken.None);
        await fixture.SinkManager.LastHandler!("evt-1");

        fixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        fixture.SinkManager.AttachCalls.Should().Be(1);
        fixture.SinkManager.LastLease.Should().BeSameAs(lease);
        fixture.SinkManager.LastSink.Should().Be("sink-1");
        fixture.Forwarder.ForwardCalls.Should().Be(1);
        fixture.Forwarder.LastLease.Should().BeSameAs(lease);
        fixture.Forwarder.LastSink.Should().Be("sink-1");
        fixture.Forwarder.LastEvent.Should().Be("evt-1");
        fixture.Forwarder.LastToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task DetachSinkAsync_ShouldValidateArgumentsAndCancellation()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestRuntimeLease("lease-1");

        Func<Task> noLease = () => fixture.Service.DetachSinkPublicAsync(null!, "sink-1", CancellationToken.None);
        Func<Task> noSink = () => fixture.Service.DetachSinkPublicAsync(lease, null!, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> canceled = () => fixture.Service.DetachSinkPublicAsync(lease, "sink-1", cts.Token);

        await noLease.Should().ThrowAsync<ArgumentNullException>().WithParameterName("lease");
        await noSink.Should().ThrowAsync<ArgumentNullException>().WithParameterName("sink");
        await canceled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DetachSinkAsync_ShouldRespectProjectionEnabledGate()
    {
        var disabledFixture = CreateLifecycleFixture(enabled: false);
        var disabledLease = new TestRuntimeLease("lease-disabled");
        await disabledFixture.Service.DetachSinkPublicAsync(disabledLease, "sink-1", CancellationToken.None);
        disabledFixture.SinkManager.DetachCalls.Should().Be(0);

        var enabledFixture = CreateLifecycleFixture(enabled: true);
        var enabledLease = new TestRuntimeLease("lease-enabled");
        await enabledFixture.Service.DetachSinkPublicAsync(enabledLease, "sink-1", CancellationToken.None);
        enabledFixture.Service.ResolveRuntimeLeaseCalls.Should().Be(1);
        enabledFixture.SinkManager.DetachCalls.Should().Be(1);
        enabledFixture.SinkManager.LastLease.Should().BeSameAs(enabledLease);
        enabledFixture.SinkManager.LastSink.Should().Be("sink-1");
    }

    [Fact]
    public async Task ReleaseProjectionAsync_ShouldValidateArgumentsAndCancellation()
    {
        var fixture = CreateLifecycleFixture(enabled: true);
        var lease = new TestRuntimeLease("lease-1");

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
        var disabledLease = new TestRuntimeLease("lease-disabled");
        await disabledFixture.Service.ReleaseProjectionPublicAsync(disabledLease, CancellationToken.None);
        disabledFixture.Release.Calls.Should().Be(0);

        var enabledFixture = CreateLifecycleFixture(enabled: true);
        var enabledLease = new TestRuntimeLease("lease-enabled");
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
        var service = new TestProjectionLifecyclePortService(
            () => enabled,
            activation,
            release,
            sinkManager,
            forwarder);
        return new LifecycleFixture(service, activation, release, sinkManager, forwarder);
    }

    private sealed record LifecycleFixture(
        TestProjectionLifecyclePortService Service,
        TestActivationService Activation,
        TestReleaseService Release,
        TestSinkSubscriptionManager SinkManager,
        TestLiveSinkForwarder Forwarder);
}

internal sealed class TestProjectionQueryPortService
    : ProjectionQueryPortServiceBase<string, string, string, TestGraphSubgraph>
{
    public TestProjectionQueryPortService(Func<bool> queryEnabledAccessor)
        : base(queryEnabledAccessor)
    {
    }

    public int ReadSnapshotCalls { get; private set; }
    public int ReadSnapshotsCalls { get; private set; }
    public int ReadTimelineCalls { get; private set; }
    public int ReadGraphEdgesCalls { get; private set; }
    public int ReadGraphSubgraphCalls { get; private set; }

    public Task<string?> GetSnapshotPublicAsync(string entityId, CancellationToken ct = default) =>
        GetSnapshotAsync(entityId, ct);

    public Task<IReadOnlyList<string>> ListSnapshotsPublicAsync(int take = 200, CancellationToken ct = default) =>
        ListSnapshotsAsync(take, ct);

    public Task<IReadOnlyList<string>> ListTimelinePublicAsync(
        string entityId,
        int take = 200,
        CancellationToken ct = default) => ListTimelineAsync(entityId, take, ct);

    public Task<IReadOnlyList<string>> GetGraphEdgesPublicAsync(
        string entityId,
        int take = 200,
        CancellationToken ct = default) => GetGraphEdgesAsync(entityId, take, ct);

    public Task<TestGraphSubgraph> GetGraphSubgraphPublicAsync(
        string entityId,
        int depth = 2,
        int take = 200,
        CancellationToken ct = default) => GetGraphSubgraphAsync(entityId, depth, take, ct);

    protected override Task<string?> ReadSnapshotCoreAsync(string entityId, CancellationToken ct)
    {
        ReadSnapshotCalls++;
        return Task.FromResult<string?>($"snapshot:{entityId}");
    }

    protected override Task<IReadOnlyList<string>> ReadSnapshotsCoreAsync(int take, CancellationToken ct)
    {
        ReadSnapshotsCalls++;
        return Task.FromResult<IReadOnlyList<string>>(["snapshot-a", "snapshot-b"]);
    }

    protected override Task<IReadOnlyList<string>> ReadTimelineCoreAsync(
        string entityId,
        int take,
        CancellationToken ct)
    {
        ReadTimelineCalls++;
        return Task.FromResult<IReadOnlyList<string>>(["timeline-a"]);
    }

    protected override Task<IReadOnlyList<string>> ReadGraphEdgesCoreAsync(
        string entityId,
        int take,
        CancellationToken ct)
    {
        ReadGraphEdgesCalls++;
        return Task.FromResult<IReadOnlyList<string>>(["edge-a"]);
    }

    protected override Task<TestGraphSubgraph> ReadGraphSubgraphCoreAsync(
        string entityId,
        int depth,
        int take,
        CancellationToken ct)
    {
        ReadGraphSubgraphCalls++;
        return Task.FromResult(new TestGraphSubgraph(entityId, false));
    }

    protected override TestGraphSubgraph CreateEmptyGraphSubgraph(string entityId) =>
        new(entityId, true);
}

internal sealed record TestGraphSubgraph(string EntityId, bool IsEmpty);

internal sealed class TestProjectionLifecyclePortService
    : ProjectionLifecyclePortServiceBase<TestLeaseContract, TestRuntimeLease, string, string>
{
    public TestProjectionLifecyclePortService(
        Func<bool> projectionEnabledAccessor,
        IProjectionPortActivationService<TestRuntimeLease> activationService,
        IProjectionPortReleaseService<TestRuntimeLease> releaseService,
        IProjectionPortSinkSubscriptionManager<TestRuntimeLease, string, string> sinkSubscriptionManager,
        IProjectionPortLiveSinkForwarder<TestRuntimeLease, string, string> liveSinkForwarder)
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
        string input,
        string commandId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(rootEntityId, projectionName, input, commandId, ct);

    public Task AttachSinkPublicAsync(
        TestLeaseContract lease,
        string sink,
        CancellationToken ct = default) =>
        AttachSinkAsync(lease, sink, ct);

    public Task DetachSinkPublicAsync(
        TestLeaseContract lease,
        string sink,
        CancellationToken ct = default) =>
        DetachSinkAsync(lease, sink, ct);

    public Task ReleaseProjectionPublicAsync(
        TestLeaseContract lease,
        CancellationToken ct = default) =>
        ReleaseProjectionAsync(lease, ct);

    protected override TestRuntimeLease ResolveRuntimeLease(TestLeaseContract lease)
    {
        ResolveRuntimeLeaseCalls++;
        return (TestRuntimeLease)lease;
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

internal sealed class TestRuntimeLease : TestLeaseContract
{
    public TestRuntimeLease(string leaseId)
        : base(leaseId)
    {
    }
}

internal sealed class TestActivationService : IProjectionPortActivationService<TestRuntimeLease>
{
    public int Calls { get; private set; }
    public string? LastRootEntityId { get; private set; }
    public string? LastProjectionName { get; private set; }
    public string? LastInput { get; private set; }
    public string? LastCommandId { get; private set; }
    public TestRuntimeLease LeaseToReturn { get; } = new("lease-activation");

    public Task<TestRuntimeLease> EnsureAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct = default)
    {
        Calls++;
        LastRootEntityId = rootEntityId;
        LastProjectionName = projectionName;
        LastInput = input;
        LastCommandId = commandId;
        return Task.FromResult(LeaseToReturn);
    }
}

internal sealed class TestReleaseService : IProjectionPortReleaseService<TestRuntimeLease>
{
    public int Calls { get; private set; }
    public TestRuntimeLease? LastLease { get; private set; }

    public Task ReleaseIfIdleAsync(TestRuntimeLease lease, CancellationToken ct = default)
    {
        Calls++;
        LastLease = lease;
        return Task.CompletedTask;
    }
}

internal sealed class TestSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<TestRuntimeLease, string, string>
{
    public int AttachCalls { get; private set; }
    public int DetachCalls { get; private set; }
    public TestRuntimeLease? LastLease { get; private set; }
    public string? LastSink { get; private set; }
    public Func<string, ValueTask>? LastHandler { get; private set; }

    public Task AttachOrReplaceAsync(
        TestRuntimeLease lease,
        string sink,
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
        TestRuntimeLease lease,
        string sink,
        CancellationToken ct = default)
    {
        DetachCalls++;
        LastLease = lease;
        LastSink = sink;
        return Task.CompletedTask;
    }
}

internal sealed class TestLiveSinkForwarder : IProjectionPortLiveSinkForwarder<TestRuntimeLease, string, string>
{
    public int ForwardCalls { get; private set; }
    public TestRuntimeLease? LastLease { get; private set; }
    public string? LastSink { get; private set; }
    public string? LastEvent { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public ValueTask ForwardAsync(
        TestRuntimeLease lease,
        string sink,
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
