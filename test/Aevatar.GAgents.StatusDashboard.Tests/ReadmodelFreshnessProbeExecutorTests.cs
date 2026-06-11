using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class ReadmodelFreshnessProbeExecutorTests
{
    [Fact]
    public async Task ReturnsOk_WhenCountMeetsMinAndFresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        var source = new FixedFreshnessSource("registrations", count: 5, clock, ageSeconds: 10);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source }, clock);
        var descriptor = NewDescriptor(new()
        {
            ["Source"] = "registrations",
            ["MinCount"] = "1",
            ["StaleAfterSeconds"] = "60",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Ok);
        outcome.Detail.Should().Be("count_5");
    }

    [Fact]
    public async Task DegradesWhenCountBelowMin()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        var source = new FixedFreshnessSource("registrations", count: 0, clock);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source }, clock);
        var descriptor = NewDescriptor(new()
        {
            ["Source"] = "registrations",
            ["MinCount"] = "1",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Degraded);
        outcome.Detail.Should().Be("count_0");
    }

    [Fact]
    public async Task DegradesWhenStale()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        var source = new FixedFreshnessSource("registrations", count: 3, clock, ageSeconds: 600);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source }, clock);
        var descriptor = NewDescriptor(new()
        {
            ["Source"] = "registrations",
            ["StaleAfterSeconds"] = "60",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Degraded);
        outcome.Detail.Should().Be("stale_600s");
        outcome.ObservedAt.ToDateTimeOffset().Should().Be(clock.GetUtcNow());
    }

    [Fact]
    public async Task ReturnsDown_WhenUnknownSource()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z"));
        var source = new FixedFreshnessSource("registrations", count: 5, clock);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source }, clock);
        var descriptor = NewDescriptor(new() { ["Source"] = "nothing-here" });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("unknown_source");
    }

    [Fact]
    public async Task ReturnsDown_WhenSourceThrows()
    {
        var source = new ThrowingFreshnessSource("registrations");
        var executor = new ReadmodelFreshnessProbeExecutor(
            new IReadmodelFreshnessSource[] { source },
            new FakeTimeProvider(DateTimeOffset.Parse("2026-05-21T10:00:00Z")));
        var descriptor = NewDescriptor(new() { ["Source"] = "registrations" });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("freshness_source_threw");
    }

    private static HealthProbeTargetDescriptor NewDescriptor(Dictionary<string, string> parameters)
    {
        var d = new HealthProbeTargetDescriptor
        {
            Slug = "channel-bot-runtime",
            DisplayName = "Channel Bot Runtime",
            Category = "feature",
            ProbeKind = "readmodel_freshness",
            IntervalSeconds = 60,
            TimeoutMs = 5_000,
            Enabled = true,
        };
        foreach (var (k, v) in parameters) d.Parameters[k] = v;
        return d;
    }

    private sealed class FixedFreshnessSource(string name, int count, TimeProvider timeProvider, int? ageSeconds = null)
        : IReadmodelFreshnessSource
    {
        public string Name => name;
        public Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct) =>
            Task.FromResult(new ReadmodelFreshnessSnapshot(
                count,
                ageSeconds.HasValue ? timeProvider.GetUtcNow().AddSeconds(-ageSeconds.Value) : null));
    }

    private sealed class ThrowingFreshnessSource(string name) : IReadmodelFreshnessSource
    {
        public string Name => name;
        public Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct) =>
            Task.FromException<ReadmodelFreshnessSnapshot>(new InvalidOperationException("boom"));
    }
}
