using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionRetryExhaustedFailureRepairServiceTests
{
    [Fact]
    public async Task RepairAsync_WhenManifestMatches_ShouldDispatchExactActorRequest()
    {
        var snapshot = BuildSnapshot();
        var replay = new RecordingReplayService();
        var service = new ProjectionRetryExhaustedFailureRepairService(
            new StubIntrospection(snapshot),
            replay);

        var result = await service.RepairAsync(ValidRequest());

        result.Status.Should().Be(
            ProjectionRetryExhaustedFailureRepairStatus.AcceptedForDispatch);
        replay.Requests.Should().ContainSingle();
        var dispatched = replay.Requests[0];
        dispatched.ScopeKey.Should().Be(new ProjectionRuntimeScopeKey(
            snapshot.RootActorId,
            snapshot.ProjectionKind,
            snapshot.Mode,
            snapshot.SessionId));
        dispatched.ExpectedScopeStateVersion.Should().Be(snapshot.StateVersion);
        dispatched.ExpectedUnresolvedFailureCount.Should().Be(snapshot.UnresolvedFailureCount);
        dispatched.ExpectedRetryExhaustedFailureCount.Should().Be(snapshot.RetryExhaustedFailureCount);
        dispatched.MaxItems.Should().Be(snapshot.RetryExhaustedFailureCount);
        dispatched.RequestId.Should().Be("operator-replay-alpha");
        dispatched.Reason.Should().Be("storage recovery completed");
        dispatched.RequestedBySubjectId.Should().Be("admin-alpha");
    }

    [Fact]
    public async Task RepairAsync_WhenRequestCountsAreInvalid_ShouldFailBeforeReadOrDispatch()
    {
        var introspection = new StubIntrospection(BuildSnapshot());
        var replay = new RecordingReplayService();
        var service = new ProjectionRetryExhaustedFailureRepairService(introspection, replay);
        var request = ValidRequest() with
        {
            ExpectedRetryExhaustedFailureCount = 20,
            ExpectedUnresolvedFailureCount = 19,
        };

        var result = await service.RepairAsync(request);

        result.Status.Should().Be(ProjectionRetryExhaustedFailureRepairStatus.InvalidRequest);
        introspection.Calls.Should().Be(0);
        replay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenSnapshotCoordinatesDoNotBuildRouteIdentity_ShouldFailClosed()
    {
        var snapshot = BuildSnapshot() with { RootActorId = "workflow-run-other" };
        var replay = new RecordingReplayService();
        var service = new ProjectionRetryExhaustedFailureRepairService(
            new StubIntrospection(snapshot),
            replay);

        var result = await service.RepairAsync(ValidRequest());

        result.Status.Should().Be(
            ProjectionRetryExhaustedFailureRepairStatus.ScopeIdentityMismatch);
        replay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenManifestChanged_ShouldFailBeforeDispatch()
    {
        var replay = new RecordingReplayService();
        var service = new ProjectionRetryExhaustedFailureRepairService(
            new StubIntrospection(BuildSnapshot()),
            replay);
        var request = ValidRequest() with { ExpectedScopeStateVersion = 8342 };

        var result = await service.RepairAsync(request);

        result.Status.Should().Be(ProjectionRetryExhaustedFailureRepairStatus.ManifestChanged);
        replay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenRecoveryIdentityIsUnavailable_ShouldReturnConflictStatus()
    {
        var replay = new RecordingReplayService { DispatchResult = false };
        var service = new ProjectionRetryExhaustedFailureRepairService(
            new StubIntrospection(BuildSnapshot()),
            replay);

        var result = await service.RepairAsync(ValidRequest());

        result.Status.Should().Be(
            ProjectionRetryExhaustedFailureRepairStatus.RecoveryIdentityUnavailable);
        replay.Requests.Should().ContainSingle();
    }

    private static ProjectionRetryExhaustedFailureRepairRequest ValidRequest() =>
        new(
            ScopeActorId,
            ExpectedScopeStateVersion: 8343,
            ExpectedUnresolvedFailureCount: 19,
            ExpectedRetryExhaustedFailureCount: 19,
            MaxItems: 19,
            RequestId: "operator-replay-alpha",
            Reason: "storage recovery completed",
            RequestedBySubjectId: "admin-alpha");

    private static ProjectionScopeIntrospectionSnapshot BuildSnapshot() =>
        new(
            ScopeActorId,
            "workflow-run-alpha",
            "workflow-execution-materialization",
            string.Empty,
            ProjectionRuntimeMode.DurableMaterialization,
            Active: true,
            ObservationAttached: true,
            Released: false,
            StateVersion: 8343,
            ReceivedEnvelopeTotal: 2618,
            AttemptedEnvelopeTotal: 2667,
            SuccessfulMaterializationTotal: 2340,
            FailedAttemptTotal: 619,
            RetryExhaustedTotal: 19,
            RetryExhaustedFailureCount: 19,
            UnresolvedFailureCount: 19,
            OldestUnresolvedFailureAt: DateTimeOffset.Parse("2026-08-28T07:03:55Z"),
            FailureDiagnosticDroppedTotal: 496,
            SourceVersions: [],
            UpdatedAt: DateTimeOffset.Parse("2026-08-28T08:59:41Z"));

    private const string ScopeActorId =
        "projection.durable.scope:workflow-execution-materialization:workflow-run-alpha";

    private sealed class StubIntrospection(ProjectionScopeIntrospectionSnapshot? snapshot)
        : IProjectionScopeIntrospectionQueryPort
    {
        public int Calls { get; private set; }

        public Task<ProjectionScopeIntrospectionSnapshot?> GetAsync(
            string scopeActorId,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<ProjectionObservedEnvelopeSnapshot>> ListRecentEnvelopesAsync(
            string scopeActorId,
            int take,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectionObservedEnvelopeSnapshot>>([]);
    }

    private sealed class RecordingReplayService : IProjectionFailureReplayService
    {
        public List<ProjectionRetryExhaustedFailuresRequest> Requests { get; } = [];
        public bool DispatchResult { get; init; } = true;

        public Task<bool> ReplayRetryExhaustedAsync(
            ProjectionRetryExhaustedFailuresRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(DispatchResult);
        }

        public Task<bool> ReplayAutomaticallyAsync(
            ProjectionRuntimeScopeKey scopeKey,
            long observedScopeStateVersion,
            int maxItems = 100,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
