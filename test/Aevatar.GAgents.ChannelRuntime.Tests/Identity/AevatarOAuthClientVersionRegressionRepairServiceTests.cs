using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.ProjectionRecovery;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class AevatarOAuthClientVersionRegressionRepairServiceTests
{
    [Fact]
    public async Task InspectAsync_WhenDocumentIsAheadOfCommittedSource_ShouldBeRepairable()
    {
        var store = new RecordingStore { Inspection = Inspection() };
        var service = new AevatarOAuthClientVersionRegressionRepairService(
            store,
            new RecordingRepublishPort());

        var result = await service.InspectAsync();

        result.Repairable.Should().BeTrue();
        result.Detail.Should().Contain("exceeds the authoritative source");
    }

    [Theory]
    [InlineData(0L, 3L, false)]
    [InlineData(2L, 2L, false)]
    [InlineData(3L, 2L, false)]
    [InlineData(2L, null, false)]
    public async Task InspectAsync_WhenStrictRegressionIsNotProven_ShouldNotBeRepairable(
        long sourceVersion,
        long? documentVersion,
        bool expectedRepairable)
    {
        var store = new RecordingStore
        {
            Inspection = Inspection() with
            {
                SourceStateVersion = sourceVersion,
                DocumentStateVersion = documentVersion,
            },
        };
        var service = new AevatarOAuthClientVersionRegressionRepairService(
            store,
            new RecordingRepublishPort());

        var result = await service.InspectAsync();

        result.Repairable.Should().Be(expectedRepairable);
    }

    [Fact]
    public async Task RepairAsync_WhenManifestMatches_ShouldDeleteAndDispatchNonCancelableRepublish()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new RecordingStore
        {
            Inspection = Inspection(),
            OnDelete = cancellation.Cancel,
        };
        var republish = new RecordingRepublishPort();
        var service = new AevatarOAuthClientVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request(), cancellation.Token);

        result.Status.Should().Be(AevatarOAuthClientVersionRegressionRepairStatus.Accepted);
        result.DeleteDisposition.Should().Be(AevatarOAuthClientReplicaDeleteDisposition.Deleted);
        store.DeleteRequests.Should().ContainSingle();
        republish.Requests.Should().ContainSingle().Which.Should().Be((2, "repair-1"));
        republish.CancellationTokens.Should().ContainSingle().Which.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task RepairAsync_WhenDocumentFingerprintChanged_ShouldNotDeleteOrDispatch()
    {
        var store = new RecordingStore
        {
            Inspection = Inspection() with { DocumentLastEventId = "event-other" },
        };
        var republish = new RecordingRepublishPort();
        var service = new AevatarOAuthClientVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(AevatarOAuthClientVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        republish.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenConditionalDeleteConflicts_ShouldNotDispatch()
    {
        var store = new RecordingStore
        {
            Inspection = Inspection(),
            DeleteDisposition = AevatarOAuthClientReplicaDeleteDisposition.RevisionConflict,
        };
        var republish = new RecordingRepublishPort();
        var service = new AevatarOAuthClientVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(AevatarOAuthClientVersionRegressionRepairStatus.Conflict);
        result.DeleteDisposition.Should().Be(AevatarOAuthClientReplicaDeleteDisposition.RevisionConflict);
        republish.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairAsync_WhenDocumentIsAlreadyAbsent_ShouldRejectWithoutDeleteOrRepublish()
    {
        var store = new RecordingStore
        {
            Inspection = Inspection() with
            {
                DocumentStateVersion = null,
                DocumentLastEventId = string.Empty,
                DocumentActorId = string.Empty,
            },
        };
        var republish = new RecordingRepublishPort();
        var service = new AevatarOAuthClientVersionRegressionRepairService(store, republish);

        var result = await service.RepairAsync(Request());

        result.Status.Should().Be(AevatarOAuthClientVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        republish.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("line one\u2028line two")]
    [InlineData("paragraph one\u2029paragraph two")]
    public async Task RepairAsync_WhenReasonContainsUnicodeLineSeparator_ShouldRejectBeforeStoreIo(
        string reason)
    {
        var store = new RecordingStore { Inspection = Inspection() };
        var republish = new RecordingRepublishPort();
        var service = new AevatarOAuthClientVersionRegressionRepairService(store, republish);

        var act = () => service.RepairAsync(Request() with { RepairReason = reason });

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*single-line*");
        store.DeleteRequests.Should().BeEmpty();
        republish.Requests.Should().BeEmpty();
    }

    private static AevatarOAuthClientVersionRegressionInspection Inspection() =>
        new(
            AevatarOAuthClientGAgent.WellKnownId,
            SourceStateVersion: 2,
            DocumentStateVersion: 3,
            "event-3",
            AevatarOAuthClientGAgent.WellKnownId,
            Repairable: false,
            Detail: string.Empty);

    private static AevatarOAuthClientVersionRegressionRepairRequest Request() =>
        new(
            AevatarOAuthClientGAgent.WellKnownId,
            ExpectedSourceStateVersion: 2,
            ExpectedDocumentStateVersion: 3,
            "event-3",
            "repair-1",
            "restore OAuth client projection from committed state",
            "admin-1");

    private sealed class RecordingStore : IAevatarOAuthClientVersionRegressionStorePort
    {
        public required AevatarOAuthClientVersionRegressionInspection Inspection { get; init; }

        public AevatarOAuthClientReplicaDeleteDisposition DeleteDisposition { get; init; } =
            AevatarOAuthClientReplicaDeleteDisposition.Deleted;

        public Action? OnDelete { get; init; }

        public List<AevatarOAuthClientVersionRegressionRepairRequest> DeleteRequests { get; } = [];

        public Task<AevatarOAuthClientVersionRegressionInspection> InspectAsync(
            CancellationToken ct = default) =>
            Task.FromResult(Inspection);

        public Task<AevatarOAuthClientReplicaDeleteDisposition> DeleteIfMatchesAsync(
            AevatarOAuthClientVersionRegressionRepairRequest request,
            CancellationToken ct = default)
        {
            DeleteRequests.Add(request);
            OnDelete?.Invoke();
            return Task.FromResult(DeleteDisposition);
        }
    }

    private sealed class RecordingRepublishPort : IAevatarOAuthClientProjectionRepublishPort
    {
        public List<(long ExpectedStateVersion, string RepairRequestId)> Requests { get; } = [];

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<AevatarOAuthClientProjectionRepublishReceipt> DispatchAsync(
            long expectedStateVersion,
            string repairRequestId,
            CancellationToken ct = default)
        {
            Requests.Add((expectedStateVersion, repairRequestId));
            CancellationTokens.Add(ct);
            return Task.FromResult(new AevatarOAuthClientProjectionRepublishReceipt(
                AevatarOAuthClientGAgent.WellKnownId,
                "command-1",
                "correlation-1"));
        }
    }
}
