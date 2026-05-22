using Aevatar.GAgents.StatusDashboard.Configuration;
using FluentAssertions;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class StatusDashboardManifestTests
{
    [Fact]
    public void FromOptions_AppliesDefaults_AndDropsInvalidEntries()
    {
        var options = new StatusDashboardOptions
        {
            DefaultIntervalSeconds = 45,
            DefaultTimeoutMs = 3_000,
            Targets =
            {
                new StatusProbeTargetConfig
                {
                    Slug = "self",
                    Name = "Self",
                    Category = "Self",
                    Probe = "http_status",
                    Parameters = { ["Url"] = "http://localhost/health" },
                },
                new StatusProbeTargetConfig { Slug = "", Probe = "http_status" }, // dropped: empty slug
                new StatusProbeTargetConfig { Slug = "no-kind" }, // dropped: empty probe
                new StatusProbeTargetConfig
                {
                    Slug = "fast",
                    Probe = "http_status",
                    IntervalSeconds = 10,
                    TimeoutMs = 500,
                },
                new StatusProbeTargetConfig { Slug = "self", Probe = "http_status" }, // dropped: duplicate
            },
        };

        var manifest = StatusDashboardManifest.FromOptions(options);

        manifest.Descriptors.Should().HaveCount(2);
        var self = manifest.Descriptors[0];
        self.Slug.Should().Be("self");
        self.DisplayName.Should().Be("Self");
        self.Category.Should().Be("self"); // lowercased
        self.IntervalSeconds.Should().Be(45);
        self.TimeoutMs.Should().Be(3_000);
        self.Parameters["Url"].Should().Be("http://localhost/health");

        manifest.Descriptors[1].IntervalSeconds.Should().Be(10);
        manifest.Descriptors[1].TimeoutMs.Should().Be(500);
    }
}
