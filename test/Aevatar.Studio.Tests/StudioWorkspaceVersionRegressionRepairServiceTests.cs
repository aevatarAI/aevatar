using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkspaceVersionRegressionRepairServiceTests
{
    private const string ScopeId = "scope-alpha";
    private const string ActorId = "studio-workspace:scope-alpha";

    [Fact]
    public async Task InspectAsync_WhenDocumentVersionExceedsPositiveSourceVersion_ShouldBeRepairable()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var service = new StudioWorkspaceVersionRegressionRepairService(
            store,
            new FakeRepublishPort());

        var result = await service.InspectAsync($" {ScopeId} ");

        result.ScopeId.Should().Be(ScopeId);
        result.Repairable.Should().BeTrue();
        result.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InspectAsync_WhenSourceVersionIsZero_ShouldNotBeRepairable()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 0, documentVersion: 4),
        };
        var service = new StudioWorkspaceVersionRegressionRepairService(
            store,
            new FakeRepublishPort());

        var result = await service.InspectAsync(ScopeId);

        result.Repairable.Should().BeFalse();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public async Task InspectAsync_WhenDocumentVersionDoesNotExceedSourceVersion_ShouldNotBeRepairable(
        long documentVersion)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 4, documentVersion: documentVersion),
        };
        var service = new StudioWorkspaceVersionRegressionRepairService(
            store,
            new FakeRepublishPort());

        var result = await service.InspectAsync(ScopeId);

        result.Repairable.Should().BeFalse();
    }

    [Fact]
    public async Task InspectAsync_WhenDocumentActorDoesNotMatchSourceActor_ShouldNotBeRepairable()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(
                sourceVersion: 1,
                documentVersion: 4,
                documentActorId: "studio-workspace:scope-other"),
        };
        var service = new StudioWorkspaceVersionRegressionRepairService(
            store,
            new FakeRepublishPort());

        var result = await service.InspectAsync(ScopeId);

        result.Repairable.Should().BeFalse();
    }

    [Theory]
    [InlineData(2, 4, "event-4")]
    [InlineData(1, 5, "event-4")]
    [InlineData(1, 4, "event-other")]
    public async Task RepairAsync_WhenApplyManifestDoesNotMatchInspection_ShouldNotDeleteOrDispatch(
        long sourceVersion,
        long documentVersion,
        string documentLastEventId)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(
                sourceVersion,
                documentVersion,
                documentLastEventId: documentLastEventId),
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        republish.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenExpectedActorDoesNotMatchPresentDocument_ShouldNotDeleteOrDispatch()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request() with
        {
            ExpectedActorId = "studio-workspace:scope-other",
        });

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        republish.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenExpectedActorDoesNotMatchMissingDocument_ShouldNotDeleteOrDispatch()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: null),
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request() with
        {
            ExpectedActorId = "studio-workspace:scope-other",
        });

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        republish.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenExpectedActorIsMissing_ShouldNotDeleteOrDispatch()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request() with
        {
            ExpectedActorId = " ",
        });

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        republish.Dispatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StudioWorkspaceReplicaDeleteDisposition.Deleted)]
    [InlineData(StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent)]
    public async Task RepairAsync_WhenDeleteAllowsContinuation_ShouldDispatchTypedRepublish(
        StudioWorkspaceReplicaDeleteDisposition deleteDisposition)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            DeleteDisposition = deleteDisposition,
        };
        var republish = new FakeRepublishPort
        {
            Receipt = new StudioWorkspaceProjectionRepublishReceipt(
                ActorId,
                "command-alpha",
                "correlation-alpha"),
        };
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Accepted);
        result.CommandId.Should().Be("command-alpha");
        store.DeleteRequests.Should().ContainSingle();
        republish.Dispatches.Should().ContainSingle().Which.Should().Be(
            (ScopeId, 1, "repair-alpha"));
    }

    [Fact]
    public async Task RepairAsync_WhenReplicaIsAlreadyMissingAndSourceIsUnchanged_ShouldContinueRepublish()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: null),
            DeleteDisposition = StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent,
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Accepted);
        store.DeleteRequests.Should().ContainSingle();
        republish.Dispatches.Should().ContainSingle();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public async Task RepairAsync_WhenMissingDocumentContinuationIsNotARegression_ShouldRejectBeforeDeleteOrDispatch(
        long expectedDocumentStateVersion)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 4, documentVersion: null),
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);
        var request = Request() with
        {
            ExpectedSourceStateVersion = 4,
            ExpectedDocumentStateVersion = expectedDocumentStateVersion,
        };

        var act = () => service.RepairAsync(request);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.DeleteRequests.Should().BeEmpty();
        republish.Dispatches.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StudioWorkspaceReplicaDeleteDisposition.Deleted)]
    [InlineData(StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent)]
    public async Task RepairAsync_AfterDeleteAllowsContinuation_ShouldDispatchWithNonRequestCancellation(
        StudioWorkspaceReplicaDeleteDisposition deleteDisposition)
    {
        using var requestCancellation = new CancellationTokenSource();
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            DeleteDisposition = deleteDisposition,
            OnDelete = requestCancellation.Cancel,
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request(), requestCancellation.Token);

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Accepted);
        republish.DispatchCancellationTokens.Should().ContainSingle()
            .Which.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task RepairAsync_WhenDeleteRevisionConflicts_ShouldNotDispatch()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            DeleteDisposition = StudioWorkspaceReplicaDeleteDisposition.RevisionConflict,
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(StudioWorkspaceVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().ContainSingle();
        republish.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_ShouldNormalizeManifestBeforeDeletingAndDispatching()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            DeleteDisposition = StudioWorkspaceReplicaDeleteDisposition.Deleted,
        };
        var republish = new FakeRepublishPort();
        var service = new StudioWorkspaceVersionRegressionRepairService(store, republish);
        var request = Request() with
        {
            ScopeId = $" {ScopeId} ",
            ExpectedActorId = $" {ActorId} ",
            ExpectedDocumentLastEventId = " event-4 ",
            RepairRequestId = " repair-alpha ",
            RepairReason = " restore authoritative workspace ",
            RequestedBySubjectId = " operator-alpha ",
        };

        await service.RepairAsync(request);

        var normalized = store.DeleteRequests.Should().ContainSingle().Subject;
        normalized.ScopeId.Should().Be(ScopeId);
        normalized.ExpectedActorId.Should().Be(ActorId);
        normalized.ExpectedDocumentLastEventId.Should().Be("event-4");
        normalized.RepairRequestId.Should().Be("repair-alpha");
        normalized.RepairReason.Should().Be("restore authoritative workspace");
        normalized.RequestedBySubjectId.Should().Be("operator-alpha");
        republish.Dispatches.Should().ContainSingle().Which.Should().Be(
            (ScopeId, 1, "repair-alpha"));
    }

    private static StudioWorkspaceVersionRegressionInspection Inspection(
        long sourceVersion,
        long? documentVersion,
        string documentLastEventId = "event-4",
        string documentActorId = ActorId) =>
        new(
            ScopeId,
            ActorId,
            sourceVersion,
            documentVersion,
            documentLastEventId,
            documentVersion.HasValue ? documentActorId : string.Empty,
            Repairable: false,
            Detail: string.Empty);

    private static StudioWorkspaceVersionRegressionRepairRequest Request() =>
        new(
            ScopeId,
            ExpectedActorId: ActorId,
            ExpectedSourceStateVersion: 1,
            ExpectedDocumentStateVersion: 4,
            ExpectedDocumentLastEventId: "event-4",
            RepairRequestId: "repair-alpha",
            RepairReason: "restore authoritative workspace",
            RequestedBySubjectId: "operator-alpha");

    private sealed class FakeStorePort : IStudioWorkspaceVersionRegressionStorePort
    {
        public StudioWorkspaceVersionRegressionInspection Inspection { get; set; } =
            StudioWorkspaceVersionRegressionRepairServiceTests.Inspection(1, 4);

        public StudioWorkspaceReplicaDeleteDisposition DeleteDisposition { get; set; } =
            StudioWorkspaceReplicaDeleteDisposition.Deleted;

        public List<string> InspectedScopes { get; } = [];

        public List<StudioWorkspaceVersionRegressionRepairRequest> DeleteRequests { get; } = [];

        public Action? OnDelete { get; init; }

        public Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            InspectedScopes.Add(scopeId);
            return Task.FromResult(Inspection);
        }

        public Task<StudioWorkspaceReplicaDeleteDisposition> DeleteIfMatchesAsync(
            StudioWorkspaceVersionRegressionRepairRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteRequests.Add(request);
            OnDelete?.Invoke();
            return Task.FromResult(DeleteDisposition);
        }
    }

    private sealed class FakeRepublishPort : IStudioWorkspaceProjectionRepublishPort
    {
        public StudioWorkspaceProjectionRepublishReceipt Receipt { get; set; } =
            new(ActorId, "command-alpha", "correlation-alpha");

        public List<(string ScopeId, long MinimumVersion, string RepairRequestId)> Dispatches { get; } = [];

        public List<CancellationToken> DispatchCancellationTokens { get; } = [];

        public Task<StudioWorkspaceProjectionRepublishReceipt> DispatchAsync(
            string scopeId,
            long minimumStateVersion,
            string repairRequestId,
            CancellationToken ct = default)
        {
            DispatchCancellationTokens.Add(ct);
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((scopeId, minimumStateVersion, repairRequestId));
            return Task.FromResult(Receipt);
        }
    }
}
