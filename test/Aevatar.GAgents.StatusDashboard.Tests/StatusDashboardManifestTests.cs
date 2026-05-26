using Aevatar.GAgents.StatusDashboard.Configuration;
using FluentAssertions;

namespace Aevatar.GAgents.StatusDashboard.Tests;

public sealed class StatusDashboardManifestTests
{
    [Fact]
    public void FromOptions_UsesBuiltInTargets_WhenTargetsAreEmpty()
    {
        var manifest = StatusDashboardManifest.FromOptions(new StatusDashboardOptions
        {
            SelfBaseUrl = "http://127.0.0.1:9999/",
        });

        manifest.Descriptors.Should().NotBeEmpty();
        manifest.Descriptors
            .Single(d => d.Slug == "self-liveness")
            .Parameters["Url"]
            .Should()
            .Be("http://127.0.0.1:9999/health/live");
        manifest.Descriptors.Select(d => d.Slug).Should().Contain(new[]
        {
            "self-liveness",
            "self-readiness",
            "aevatar-core-loop-tools",
            "channel-bot-runtime",
            "nyxid-http-health",
            "nyxid-oidc-discovery",
        });
        var nyxIdHealth = manifest.Descriptors.Single(d => d.Slug == "nyxid-http-health");
        nyxIdHealth.DisplayName.Should().Be("NyxID HTTP health");
        nyxIdHealth.Category.Should().Be("upstream");
        nyxIdHealth.ProbeKind.Should().Be("http_status");
        nyxIdHealth.Parameters["Url"].Should().Be("${configuration:Aevatar:NyxId:Authority}/health");
        nyxIdHealth.Parameters["ExpectedStatuses"].Should().Be("200");
        manifest.Descriptors.Select(d => d.Slug).Should().NotContain(new[]
        {
            "chat-completion-api-singular-route",
            "responses-api-auth-gate",
            "messages-api-auth-gate",
            "chat-completions-api-auth-gate",
            "models-api-auth-gate",
            "voice-websocket-auth-gate",
            "channel-registration-api-auth-gate",
            "nyxid-llm-status",
            "nyxid-llm-gateway-auth-gate",
            "nyxid-channel-bots-auth-gate",
            "nyxid-channel-relay-reply-auth-gate",
        });
        manifest.Descriptors
            .Select(static d => d.Parameters.TryGetValue("ExpectedStatuses", out var value) ? value : string.Empty)
            .Should()
            .NotContain(static value => value.Split(',', StringSplitOptions.TrimEntries).Contains("401"));
        var coreLoop = manifest.Descriptors.Single(d => d.Slug == "aevatar-core-loop-tools");
        coreLoop.DisplayName.Should().Be("Aevatar Core Loop Tools");
        coreLoop.Category.Should().Be("feature");
        coreLoop.ProbeKind.Should().Be("aevatar_core_loop");
        coreLoop.Parameters["ToolSet"].Should().Be("workspace.default");
        coreLoop.Parameters["RequireWorkspaceSources"].Should().Be("true");
        manifest.Descriptors.Should().OnlyContain(d => d.IntervalSeconds == 60);
    }

    [Fact]
    public void FromOptions_CanDisableBuiltInTargets()
    {
        var manifest = StatusDashboardManifest.FromOptions(new StatusDashboardOptions
        {
            UseBuiltInTargets = false,
        });

        manifest.Descriptors.Should().BeEmpty();
    }

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
