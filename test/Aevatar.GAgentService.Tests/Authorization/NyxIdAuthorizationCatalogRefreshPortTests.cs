using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPortTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public async Task RefreshPersonalAsync_WhenExactTopologyContractIsNotPublished_ShouldInvalidateStableBlocker()
    {
        var commands = new RecordingCommandPort();
        var port = Create(commands);

        var result = await port.RefreshPersonalAsync(" owner-alpha ", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.PublishedContractMissing);
        result.FailureCode.Should().Be(
            NyxIdAuthorizationCatalogRefreshPort.PublishedContractMissingFailureCode);
        commands.Activations.Should().ContainSingle();
        commands.Activations[0].Owner.Should().BeEquivalentTo(Owner());
        commands.Activations[0].At.Should().Be(Now);
        commands.Invalidations.Should().ContainSingle();
        commands.Invalidations[0].Owner.Should().BeEquivalentTo(Owner());
        commands.Invalidations[0].At.Should().Be(Now);
        commands.Invalidations[0].Reason.Should().Be(
            NyxIdAuthorizationCatalogRefreshPort.PublishedContractMissingFailureCode);
        commands.Observations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenOrganizationOwnerContractIsNotPublished_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.OwnerKind = AuthorizationOwnerKind.Organization;
        owner.OwnerSubject = "org-alpha";

        var result = await Create(commands).RefreshAsync(owner, "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported);
        result.FailureCode.Should().Be("nyxid_catalog_organization_owner_not_supported");
        commands.AllCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenAuthorityIsNotNyxId_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.Authority = "other-authority";

        var act = () => Create(commands).RefreshAsync(owner, "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner authority is not supported*");
        commands.AllCalls.Should().Be(0);
    }

    private static NyxIdAuthorizationCatalogRefreshPort Create(RecordingCommandPort commands) => new(
        commands,
        new FakeTimeProvider(Now),
        NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance);

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private sealed class RecordingCommandPort : INyxIdAuthorizationCatalogCommandPort
    {
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At)> Activations { get; } = [];
        public List<NyxIdAuthorizationCatalogObservation> Observations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Code)> Failures { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)> Invalidations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)> Cleanups { get; } = [];

        public int AllCalls => Activations.Count + Observations.Count + Failures.Count +
                               Invalidations.Count + Cleanups.Count;

        public Task ActivateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset activatedAtUtc,
            CancellationToken ct = default)
        {
            Activations.Add((owner.Clone(), activatedAtUtc));
            return Task.CompletedTask;
        }

        public Task ObserveAsync(
            NyxIdAuthorizationCatalogObservation observation,
            CancellationToken ct = default)
        {
            Observations.Add(observation);
            return Task.CompletedTask;
        }

        public Task RecordRefreshFailureAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset failedAtUtc,
            string failureCode,
            CancellationToken ct = default)
        {
            Failures.Add((owner.Clone(), failedAtUtc, failureCode));
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Invalidations.Add((owner.Clone(), invalidatedAtUtc, reason));
            return Task.CompletedTask;
        }

        public Task CleanupAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset cleanedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Cleanups.Add((owner.Clone(), cleanedAtUtc, reason));
            return Task.CompletedTask;
        }
    }
}
