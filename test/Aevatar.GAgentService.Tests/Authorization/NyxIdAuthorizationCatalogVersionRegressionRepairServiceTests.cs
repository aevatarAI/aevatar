using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogVersionRegressionRepairServiceTests
{
    private const string VerifiedOwnerSubject = "owner-alpha";
    private const string BearerToken = "bearer-secret";
    private static readonly string ActorId = NyxIdAuthorizationCatalogActorIds.Build(Owner());

    [Fact]
    public void RepairRequest_ToString_ShouldRedactBearerToken()
    {
        var text = Request().ToString();

        text.Should().NotContain(BearerToken);
        text.Should().Contain("[REDACTED]");
    }

    [Fact]
    public async Task InspectPersonalAsync_WhenDocumentVersionExceedsPositiveSourceVersion_ShouldBeRepairable()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var service = NewService(store);

        var result = await service.InspectPersonalAsync($" {VerifiedOwnerSubject} ");

        result.VerifiedOwnerSubject.Should().Be(VerifiedOwnerSubject);
        result.Repairable.Should().BeTrue();
        result.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 4)]
    [InlineData(4, 3)]
    public async Task InspectPersonalAsync_WhenVersionRelationshipIsNotRepairable_ShouldReject(
        long sourceVersion,
        long documentVersion)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion, documentVersion),
        };
        var service = NewService(store);

        var result = await service.InspectPersonalAsync(VerifiedOwnerSubject);

        result.Repairable.Should().BeFalse();
    }

    [Fact]
    public async Task InspectPersonalAsync_WhenDocumentActorDoesNotMatchSourceActor_ShouldReject()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(
                sourceVersion: 1,
                documentVersion: 4,
                documentActorId: "catalog-actor-other"),
        };
        var service = NewService(store);

        var result = await service.InspectPersonalAsync(VerifiedOwnerSubject);

        result.Repairable.Should().BeFalse();
    }

    [Theory]
    [InlineData(2, 4, "event-4")]
    [InlineData(1, 5, "event-4")]
    [InlineData(1, 4, "event-other")]
    public async Task RepairPersonalAsync_WhenManifestDoesNotMatchInspection_ShouldNotDeleteOrRefresh(
        long sourceVersion,
        long documentVersion,
        string documentLastEventId)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion, documentVersion, documentLastEventId),
        };
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().BeEmpty();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public async Task RepairPersonalAsync_WhenExpectedDocumentDoesNotExceedExpectedSource_ShouldRejectBeforeInspection(
        long documentVersion)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 4, documentVersion: null),
        };
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);
        var request = Request() with
        {
            ExpectedSourceStateVersion = 4,
            ExpectedDocumentStateVersion = documentVersion,
        };

        var act = () => service.RepairPersonalAsync(request);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        store.InspectedSubjects.Should().BeEmpty();
        store.DeleteRequests.Should().BeEmpty();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenExpectedActorDoesNotMatchPresentDocument_ShouldRejectBeforeInspection()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var act = () => service.RepairPersonalAsync(Request() with
        {
            ExpectedActorId = "catalog-actor-other",
        });

        await act.Should().ThrowAsync<ArgumentException>();
        store.InspectedSubjects.Should().BeEmpty();
        store.DeleteRequests.Should().BeEmpty();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenExpectedActorDoesNotMatchMissingDocument_ShouldRejectBeforeInspection()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: null),
        };
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var act = () => service.RepairPersonalAsync(Request() with
        {
            ExpectedActorId = "catalog-actor-other",
        });

        await act.Should().ThrowAsync<ArgumentException>();
        store.InspectedSubjects.Should().BeEmpty();
        store.DeleteRequests.Should().BeEmpty();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenRequesterDoesNotMatchVerifiedOwner_ShouldRejectBeforeInspection()
    {
        var store = new FakeStorePort();
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var act = () => service.RepairPersonalAsync(Request() with
        {
            RequestedBySubjectId = "operator-alpha",
        });

        await act.Should().ThrowAsync<ArgumentException>();
        store.InspectedSubjects.Should().BeEmpty();
        store.DeleteRequests.Should().BeEmpty();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenExpectedActorIsMissing_ShouldRejectBeforeInspection()
    {
        var store = new FakeStorePort();
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var act = () => service.RepairPersonalAsync(Request() with
        {
            ExpectedActorId = " ",
        });

        await act.Should().ThrowAsync<ArgumentException>();
        store.InspectedSubjects.Should().BeEmpty();
        store.DeleteRequests.Should().BeEmpty();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted)]
    [InlineData(NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent)]
    public async Task RepairPersonalAsync_WhenDeleteAllowsContinuation_ShouldRefreshExactlyOnce(
        NyxIdAuthorizationCatalogReplicaDeleteDisposition deleteDisposition)
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            DeleteDisposition = deleteDisposition,
        };
        var refresh = new FakeRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(7),
        };
        var visibility = new FakeVisibilityPort
        {
            Result = Visibility(NyxIdAuthorizationCatalogVisibilityStatus.Ready, 7, 7),
        };
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready);
        store.DeleteRequests.Should().ContainSingle();
        refresh.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenDocumentIsAlreadyMissing_ShouldContinueRefreshExactlyOnce()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: null),
            DeleteDisposition = NyxIdAuthorizationCatalogReplicaDeleteDisposition.AlreadyAbsent,
        };
        var refresh = new FakeRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(7),
        };
        var visibility = new FakeVisibilityPort
        {
            Result = Visibility(NyxIdAuthorizationCatalogVisibilityStatus.Ready, 7, 7),
        };
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready);
        store.DeleteRequests.Should().ContainSingle();
        refresh.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task RepairPersonalAsync_ShouldPassRepairIdentityAndMinimumToRepairRefresh()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var refresh = new FakeRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(7),
        };
        var visibility = new FakeVisibilityPort
        {
            Result = Visibility(NyxIdAuthorizationCatalogVisibilityStatus.Ready, 7, 7),
        };
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        await service.RepairPersonalAsync(Request());

        refresh.Calls.Should().ContainSingle().Which.Should().Be(
            (VerifiedOwnerSubject, BearerToken, 1, "repair-alpha", CancellationToken.None));
    }

    [Fact]
    public async Task RepairPersonalAsync_AfterDelete_ShouldUseNonRequestCancellationForRepairRefresh()
    {
        using var requestCancellation = new CancellationTokenSource();
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            OnDelete = requestCancellation.Cancel,
        };
        var refresh = new FakeRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(7),
        };
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            new FakeVisibilityPort());

        var act = () => service.RepairPersonalAsync(
            Request(),
            requestCancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        refresh.Calls.Should().ContainSingle().Which.Should().Be(
            (VerifiedOwnerSubject, BearerToken, 1, "repair-alpha", CancellationToken.None));
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenRefreshFails_ShouldReturnFailedWithoutVisibility()
    {
        var refreshFailure = new NyxIdAuthorizationCatalogRefreshResult(
            NyxIdAuthorizationCatalogRefreshStatus.Failed,
            "nyxid_catalog_refresh_failed");
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var refresh = new FakeRefreshPort
        {
            Result = refreshFailure,
        };
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Failed);
        result.Refresh.Should().BeSameAs(refreshFailure);
        result.Visibility.Should().BeNull();
        visibility.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenRefreshIsObserved_ShouldResolveVisibilityAtCommittedVersion()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var refresh = new FakeRefreshPort
        {
            Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
        };
        var visibility = new FakeVisibilityPort
        {
            Result = Visibility(NyxIdAuthorizationCatalogVisibilityStatus.Ready, 23, 23),
        };
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        await service.RepairPersonalAsync(Request());

        var call = visibility.Calls.Should().ContainSingle().Subject;
        call.Owner.Authority.Should().Be(NyxIdAuthorizationAuthorities.NyxId);
        call.Owner.OwnerKind.Should().Be(AuthorizationOwnerKind.Personal);
        call.Owner.OwnerSubject.Should().Be(VerifiedOwnerSubject);
        call.RequiredStateVersion.Should().Be(23);
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenRebuiltReplicaIsReady_ShouldReturnReady()
    {
        var ready = Visibility(NyxIdAuthorizationCatalogVisibilityStatus.Ready, 23, 23);
        var service = NewService(
            new FakeStorePort
            {
                Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            },
            new FakeRefreshPort
            {
                Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
            },
            new FakeVisibilityPort
            {
                Result = ready,
            });

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Ready);
        result.Visibility.Should().BeSameAs(ready);
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenRebuiltReplicaIsPending_ShouldReturnProjectionPending()
    {
        var pending = Visibility(
            NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending,
            requiredStateVersion: 23,
            visibleStateVersion: 0);
        var service = NewService(
            new FakeStorePort
            {
                Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            },
            new FakeRefreshPort
            {
                Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(23),
            },
            new FakeVisibilityPort
            {
                Result = pending,
            });

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(
            NyxIdAuthorizationCatalogVersionRegressionRepairStatus.ProjectionPending);
        result.Visibility.Should().BeSameAs(pending);
    }

    [Fact]
    public async Task RepairPersonalAsync_WhenDeleteConflicts_ShouldNotRefresh()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
            DeleteDisposition =
                NyxIdAuthorizationCatalogReplicaDeleteDisposition.RevisionConflict,
        };
        var refresh = new FakeRefreshPort();
        var visibility = new FakeVisibilityPort();
        var service = new NyxIdAuthorizationCatalogVersionRegressionRepairService(
            store,
            refresh,
            visibility);

        var result = await service.RepairPersonalAsync(Request());

        result.Status.Should().Be(NyxIdAuthorizationCatalogVersionRegressionRepairStatus.Conflict);
        store.DeleteRequests.Should().ContainSingle();
        refresh.Calls.Should().BeEmpty();
        visibility.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RepairPersonalAsync_ShouldNormalizeManifestBeforeDelete()
    {
        var store = new FakeStorePort
        {
            Inspection = Inspection(sourceVersion: 1, documentVersion: 4),
        };
        var service = NewService(store);
        var request = Request() with
        {
            VerifiedOwnerSubject = $" {VerifiedOwnerSubject} ",
            ExpectedActorId = $" {ActorId} ",
            ExpectedDocumentLastEventId = " event-4 ",
            RepairRequestId = " repair-alpha ",
            RepairReason = " rebuild from NyxID ",
            RequestedBySubjectId = $" {VerifiedOwnerSubject} ",
        };

        await service.RepairPersonalAsync(request);

        var normalized = store.DeleteRequests.Should().ContainSingle().Subject;
        normalized.VerifiedOwnerSubject.Should().Be(VerifiedOwnerSubject);
        normalized.ExpectedActorId.Should().Be(ActorId);
        normalized.ExpectedDocumentLastEventId.Should().Be("event-4");
        normalized.RepairRequestId.Should().Be("repair-alpha");
        normalized.RepairReason.Should().Be("rebuild from NyxID");
        normalized.RequestedBySubjectId.Should().Be(VerifiedOwnerSubject);
        normalized.BearerToken.Should().Be(BearerToken);
    }

    private static NyxIdAuthorizationCatalogVersionRegressionRepairService NewService(
        FakeStorePort store,
        FakeRefreshPort? refresh = null,
        FakeVisibilityPort? visibility = null) =>
        new(
            store,
            refresh ?? new FakeRefreshPort
            {
                Result = NyxIdAuthorizationCatalogRefreshResult.ObservedAt(7),
            },
            visibility ?? new FakeVisibilityPort
            {
                Result = Visibility(NyxIdAuthorizationCatalogVisibilityStatus.Ready, 7, 7),
            });

    private static NyxIdAuthorizationCatalogVersionRegressionInspection Inspection(
        long sourceVersion,
        long? documentVersion,
        string documentLastEventId = "event-4",
        string documentActorId = "") =>
        new(
            VerifiedOwnerSubject,
            ActorId,
            sourceVersion,
            documentVersion,
            documentLastEventId,
            documentVersion.HasValue
                ? documentActorId.Length == 0 ? ActorId : documentActorId
                : string.Empty,
            Repairable: false,
            Detail: string.Empty);

    private static NyxIdAuthorizationCatalogVersionRegressionRepairRequest Request() =>
        new(
            VerifiedOwnerSubject,
            ExpectedActorId: ActorId,
            BearerToken,
            ExpectedSourceStateVersion: 1,
            ExpectedDocumentStateVersion: 4,
            ExpectedDocumentLastEventId: "event-4",
            RepairRequestId: "repair-alpha",
            RepairReason: "rebuild from NyxID",
            RequestedBySubjectId: VerifiedOwnerSubject);

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = VerifiedOwnerSubject,
    };

    private static NyxIdAuthorizationCatalogVisibilityResult Visibility(
        NyxIdAuthorizationCatalogVisibilityStatus status,
        long requiredStateVersion,
        long visibleStateVersion) =>
        new(
            status,
            requiredStateVersion,
            visibleStateVersion,
            status == NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending
                ? "nyxid_catalog_projection_pending"
                : string.Empty);

    private sealed class FakeStorePort : INyxIdAuthorizationCatalogVersionRegressionStorePort
    {
        public NyxIdAuthorizationCatalogVersionRegressionInspection Inspection { get; set; } =
            NyxIdAuthorizationCatalogVersionRegressionRepairServiceTests.Inspection(1, 4);

        public NyxIdAuthorizationCatalogReplicaDeleteDisposition DeleteDisposition { get; set; } =
            NyxIdAuthorizationCatalogReplicaDeleteDisposition.Deleted;

        public List<string> InspectedSubjects { get; } = [];

        public List<NyxIdAuthorizationCatalogVersionRegressionRepairRequest> DeleteRequests { get; } = [];

        public Action? OnDelete { get; init; }

        public Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
            string verifiedOwnerSubject,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            InspectedSubjects.Add(verifiedOwnerSubject);
            return Task.FromResult(Inspection);
        }

        public Task<NyxIdAuthorizationCatalogReplicaDeleteDisposition> DeleteIfMatchesAsync(
            NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DeleteRequests.Add(request);
            OnDelete?.Invoke();
            return Task.FromResult(DeleteDisposition);
        }
    }

    private sealed class FakeRefreshPort : INyxIdAuthorizationCatalogRepairRefreshPort
    {
        public NyxIdAuthorizationCatalogRefreshResult Result { get; set; } =
            new(NyxIdAuthorizationCatalogRefreshStatus.Failed, "not-configured");

        public List<(
            string VerifiedOwnerSubject,
            string BearerToken,
            long MinimumSourceStateVersion,
            string RepairRequestId,
            CancellationToken CancellationToken)> Calls { get; } = [];

        public Task<NyxIdAuthorizationCatalogRefreshResult> RefreshPersonalAsync(
            string verifiedOwnerSubject,
            string bearerToken,
            long minimumSourceStateVersion,
            string repairRequestId,
            CancellationToken ct = default)
        {
            Calls.Add((
                verifiedOwnerSubject,
                bearerToken,
                minimumSourceStateVersion,
                repairRequestId,
                ct));
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeVisibilityPort : INyxIdAuthorizationCatalogVisibilityPort
    {
        public NyxIdAuthorizationCatalogVisibilityResult Result { get; set; } =
            NyxIdAuthorizationCatalogVisibilityResult.Unavailable(1);

        public List<(AuthorizationOwnerIdentity Owner, long RequiredStateVersion)> Calls { get; } = [];

        public Task<NyxIdAuthorizationCatalogVisibilityResult> ResolveAsync(
            AuthorizationOwnerIdentity owner,
            long requiredStateVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((owner.Clone(), requiredStateVersion));
            return Task.FromResult(Result);
        }
    }
}
