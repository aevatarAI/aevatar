using Aevatar.GAgents.StatusDashboard.Executors;
using FluentAssertions;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class ReadmodelFreshnessProbeExecutorTests
{
    [Fact]
    public async Task ReturnsOk_WhenCountMeetsMinAndFresh()
    {
        var source = new FixedFreshnessSource("registrations", count: 5, ageSeconds: 10);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source });
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
        var source = new FixedFreshnessSource("registrations", count: 0);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source });
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
        var source = new FixedFreshnessSource("registrations", count: 3, ageSeconds: 600);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source });
        var descriptor = NewDescriptor(new()
        {
            ["Source"] = "registrations",
            ["StaleAfterSeconds"] = "60",
        });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Degraded);
        outcome.Detail.Should().StartWith("stale_");
    }

    [Fact]
    public async Task ReturnsDown_WhenUnknownSource()
    {
        var source = new FixedFreshnessSource("registrations", count: 5);
        var executor = new ReadmodelFreshnessProbeExecutor(new[] { source });
        var descriptor = NewDescriptor(new() { ["Source"] = "nothing-here" });

        var outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
        outcome.Status.Should().Be(HealthOutcomeStatus.Down);
        outcome.Detail.Should().Be("unknown_source");
    }

    [Fact]
    public async Task ReturnsDown_WhenSourceThrows()
    {
        var source = new ThrowingFreshnessSource("registrations");
        var executor = new ReadmodelFreshnessProbeExecutor(new IReadmodelFreshnessSource[] { source });
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

    private sealed class FixedFreshnessSource(string name, int count, int? ageSeconds = null)
        : IReadmodelFreshnessSource
    {
        public string Name => name;
        public Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct) =>
            Task.FromResult(new ReadmodelFreshnessSnapshot(
                count,
                ageSeconds.HasValue ? DateTimeOffset.UtcNow.AddSeconds(-ageSeconds.Value) : null));
    }

    private sealed class ThrowingFreshnessSource(string name) : IReadmodelFreshnessSource
    {
        public string Name => name;
        public Task<ReadmodelFreshnessSnapshot> GetFreshnessAsync(CancellationToken ct) =>
            Task.FromException<ReadmodelFreshnessSnapshot>(new InvalidOperationException("boom"));
    }
}
