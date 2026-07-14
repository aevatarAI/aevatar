using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdCatalogAccessLifecyclePortTests
{
    [Fact]
    public async Task InvalidateAsync_WhenAuthorityIsMissing_ShouldNotForward()
    {
        var commands = new RecordingCommandPort();
        var port = Create(commands, new ConfigurationBuilder().Build());

        await port.InvalidateAsync(Subject(OwnerScope.NyxIdPlatform, "nyx-owner-alpha"), "binding_revoked");

        commands.Invalidations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("lark", "nyx-owner-alpha")]
    [InlineData("nyxid", " ")]
    public async Task InvalidateAsync_WhenSubjectIsNotVerifiedNyxIdOwner_ShouldNotForward(
        string platform,
        string ownerSubject)
    {
        var commands = new RecordingCommandPort();
        var port = Create(commands, Configuration());

        await port.InvalidateAsync(Subject(platform, ownerSubject), "binding_revoked");

        commands.Invalidations.Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidateAsync_ShouldNormalizeAndForwardConfiguredAuthorityClockAndReason()
    {
        var commands = new RecordingCommandPort();
        var port = Create(commands, Configuration());

        await port.InvalidateAsync(Subject(OwnerScope.NyxIdPlatform, " nyx-owner-alpha "), "binding_revoked");

        var invalidation = commands.Invalidations.Should().ContainSingle().Subject;
        invalidation.Owner.Authority.Should().Be("https://nyx.example");
        invalidation.Owner.OwnerKind.Should().Be(NyxIdCatalogOwnerKind.Personal);
        invalidation.Owner.OwnerSubject.Should().Be("nyx-owner-alpha");
        invalidation.InvalidatedAt.Should().Be(DateTimeOffset.Parse("2026-07-15T01:02:03Z"));
        invalidation.Reason.Should().Be("binding_revoked");
    }

    private static NyxIdCatalogAccessLifecyclePort Create(
        RecordingCommandPort commands,
        IConfiguration configuration) =>
        new(commands, configuration, new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T01:02:03Z")));

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aevatar:NyxId:Authority"] = "https://nyx.example/",
        }).Build();

    private static ExternalSubjectRef Subject(string platform, string ownerSubject) => new()
    {
        Platform = platform,
        Tenant = "tenant-alpha",
        ExternalUserId = ownerSubject,
    };

    private sealed class RecordingCommandPort : INyxIdCatalogSnapshotCommandPort
    {
        public List<(NyxIdCatalogOwnerIdentity Owner, DateTimeOffset InvalidatedAt, string Reason)> Invalidations { get; } = [];

        public Task ObserveAsync(NyxIdCatalogObservation observation, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RecordRefreshFailureAsync(
            NyxIdCatalogOwnerIdentity owner,
            DateTimeOffset failedAtUtc,
            string failureCode,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task InvalidateAsync(
            NyxIdCatalogOwnerIdentity owner,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Invalidations.Add((owner, invalidatedAtUtc, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
