using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Application.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogVisibilityServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-21T00:05:00Z");

    [Fact]
    public async Task ResolveAsync_WhenCommittedVersionIsVisibleAndUsable_ShouldReturnReady()
    {
        var query = new StubCatalogQueryPort(Snapshot(23));
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Ready);
        result.Ready.Should().BeTrue();
        result.RequiredStateVersion.Should().Be(23);
        result.VisibleStateVersion.Should().Be(23);
        result.FailureCode.Should().BeEmpty();
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenCommittedVersionIsNotVisible_ShouldReturnProjectionPending()
    {
        var query = new StubCatalogQueryPort(Snapshot(22));
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.ProjectionPending);
        result.ProjectionPending.Should().BeTrue();
        result.RequiredStateVersion.Should().Be(23);
        result.VisibleStateVersion.Should().Be(22);
        result.FailureCode.Should().Be("nyxid_catalog_projection_pending");
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenNewerInvalidationIsVisible_ShouldNotReturnProjectionPending()
    {
        var query = new StubCatalogQueryPort(Snapshot(24) with { Invalidated = true });
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Invalidated);
        result.ProjectionPending.Should().BeFalse();
        result.VisibleStateVersion.Should().Be(24);
        result.FailureCode.Should().Be("nyxid_catalog_snapshot_invalidated");
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenVisibleSnapshotIsStale_ShouldReturnStale()
    {
        var query = new StubCatalogQueryPort(Snapshot(23) with { FreshUntilUtc = Now });
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Stale);
        result.ProjectionPending.Should().BeFalse();
        result.FailureCode.Should().Be("nyxid_catalog_snapshot_stale");
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveRequiredServicesAsync_WhenProviderClockIsWithinAllowedSkew_ShouldReturnReady()
    {
        var serviceEvidence = ServiceEvidence(
            observedAt: Now.AddSeconds(-10),
            evaluatedAt: Now.AddSeconds(10));
        var query = new StubCatalogQueryPort(Snapshot(23) with
        {
            Services = [serviceEvidence],
        });
        var service = NewService(query);

        var result = await service.ResolveRequiredServicesAsync(
            Owner(),
            23,
            [new NyxIdUserServiceCapabilityRef { UserServiceId = serviceEvidence.UserServiceId }]);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Ready);
        result.Ready.Should().BeTrue();
        result.FailureCode.Should().BeEmpty();
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public void EvaluateServiceAuthorityWindow_WhenProviderClockSkewEqualsLimit_ShouldReturnReady()
    {
        var serviceEvidence = ServiceEvidence(
            observedAt: Now.AddSeconds(-30),
            evaluatedAt: Now);

        var result = NyxIdAuthorizationCatalogIntegrity.EvaluateServiceAuthorityWindow(
            Snapshot(23) with { Services = [serviceEvidence] },
            serviceEvidence,
            Now);

        result.Status.Should().Be(NyxIdAuthorizationServiceAuthorityWindowStatus.Ready);
        result.Ready.Should().BeTrue();
    }

    [Fact]
    public async Task ResolveRequiredServicesAsync_WhenOwnerStampIsStaleButRequiredServiceStampIsFresh_ShouldReturnReady()
    {
        var serviceEvidence = ServiceEvidence(
            observedAt: Now.AddSeconds(-10),
            evaluatedAt: Now);
        var query = new StubCatalogQueryPort(Snapshot(23) with
        {
            FreshUntilUtc = Now,
            Services = [serviceEvidence],
        });
        var service = NewService(query);

        var result = await service.ResolveRequiredServicesAsync(
            Owner(),
            23,
            [new NyxIdUserServiceCapabilityRef { UserServiceId = serviceEvidence.UserServiceId }]);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Ready);
        result.Ready.Should().BeTrue();
        result.FailureCode.Should().BeEmpty();
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public void EvaluateServiceAuthorityWindow_WhenProviderClockSkewExceedsLimit_ShouldReturnTypedStatus()
    {
        var serviceEvidence = ServiceEvidence(
            observedAt: Now.AddSeconds(-31),
            evaluatedAt: Now);

        var result = NyxIdAuthorizationCatalogIntegrity.EvaluateServiceAuthorityWindow(
            Snapshot(23) with { Services = [serviceEvidence] },
            serviceEvidence,
            Now);

        result.Status.Should().Be(
            NyxIdAuthorizationServiceAuthorityWindowStatus.ProviderClockSkewExceeded);
        result.ObservedAtUtc.Should().Be(Now.AddSeconds(-31));
        result.FreshUntilUtc.Should().Be(Now.AddMinutes(14).AddSeconds(29));
        result.ProviderEvaluatedAtUtc.Should().Be(Now);
    }

    [Fact]
    public async Task ResolveAsync_WhenActivatedSnapshotWasNeverObserved_ShouldReturnInvalid()
    {
        var query = new StubCatalogQueryPort(Snapshot(23) with
        {
            ObservedAtUtc = default,
            FreshUntilUtc = default,
            ContractVersion = string.Empty,
            PolicyVersion = string.Empty,
            EvaluatedAtUtc = default,
            ContentDigest = string.Empty,
        });
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Invalid);
        result.FailureCode.Should().Be("nyxid_catalog_snapshot_invalid");
        result.RequiredStateVersion.Should().Be(23);
        result.VisibleStateVersion.Should().Be(23);
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenVisibleOwnerDoesNotMatch_ShouldReturnOwnerMismatch()
    {
        var otherOwner = Owner();
        otherOwner.OwnerSubject = "nyx-owner-other";
        var query = new StubCatalogQueryPort(Snapshot(23) with { Owner = otherOwner });
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.OwnerMismatch);
        result.FailureCode.Should().Be("nyxid_catalog_owner_mismatch");
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenReadModelQueryFails_ShouldReturnUnavailable()
    {
        var query = new StubCatalogQueryPort(null)
        {
            Exception = new InvalidOperationException("private-store-detail"),
        };
        var service = NewService(query);

        var result = await service.ResolveAsync(Owner(), 23);

        result.Status.Should().Be(NyxIdAuthorizationCatalogVisibilityStatus.Unavailable);
        result.RequiredStateVersion.Should().Be(23);
        result.VisibleStateVersion.Should().Be(0);
        result.FailureCode.Should().Be("nyxid_catalog_visibility_unavailable");
        query.QueryCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_WhenCallerCancels_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var query = new StubCatalogQueryPort(null)
        {
            Exception = new OperationCanceledException(cts.Token),
        };
        var service = NewService(query);

        var act = () => service.ResolveAsync(Owner(), 23, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        query.QueryCount.Should().Be(1);
    }

    private static NyxIdAuthorizationCatalogVisibilityService NewService(
        INyxIdAuthorizationCatalogQueryPort queryPort) => new(
        queryPort,
        new FakeTimeProvider(Now),
        NullLogger<NyxIdAuthorizationCatalogVisibilityService>.Instance);

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "nyx-owner-alpha",
    };

    private static NyxIdAuthorizationCatalogSnapshot Snapshot(long stateVersion) => new(
        Owner(),
        stateVersion,
        Now.AddMinutes(-1),
        Now.AddMinutes(10),
        "scope-plan-contract/v1",
        "scope-plan-policy/v1",
        Now.AddMinutes(-1),
        "catalog-digest-alpha",
        [],
        Activated: true);

    private static NyxIdAuthorizationServiceEvidence ServiceEvidence(
        DateTimeOffset observedAt,
        DateTimeOffset evaluatedAt) => new()
    {
        UserServiceId = "service-a",
        ServiceSlug = "api-alpha",
        DisplayName = "Alpha",
        Access = NyxIdAuthorizationAccess.Permitted,
        NodeGrantRequirement = AuthorizationGrantRequirement.NotRequired,
        ResourceOwner = Owner(),
        ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        FreshUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(15)),
        EvaluatedAt = Timestamp.FromDateTimeOffset(evaluatedAt),
        AuthorityContractVersion = "1",
        AuthorityPolicyVersion = "api-key-scope-v1",
    };

    private sealed class StubCatalogQueryPort(NyxIdAuthorizationCatalogSnapshot? snapshot)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public Exception? Exception { get; init; }
        public int QueryCount { get; private set; }

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            QueryCount++;
            return Exception == null
                ? Task.FromResult(snapshot)
                : Task.FromException<NyxIdAuthorizationCatalogSnapshot?>(Exception);
        }
    }
}
