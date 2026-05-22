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
            "responses-api-auth-gate",
            "messages-api-auth-gate",
            "models-api-auth-gate",
            "voice-websocket-auth-gate",
            "channel-bot-runtime",
            "nyxid-llm-status",
            "nyxid-llm-gateway-auth-gate",
            "nyxid-channel-bots-auth-gate",
            "nyxid-channel-relay-reply-auth-gate",
        });
        manifest.Descriptors.Should().OnlyContain(d => d.IntervalSeconds == 60);
    }

    [Fact]
    public void FromOptions_AddsResponsesForwardToTeamStages_WhenEnabled()
    {
        var manifest = StatusDashboardManifest.FromOptions(new StatusDashboardOptions
        {
            SelfBaseUrl = "http://127.0.0.1:9999/",
            ResponsesForwardToTeam = new ResponsesForwardToTeamStatusProbeOptions
            {
                Enabled = true,
                DirectBaseUrl = "https://aevatar.example/",
                NyxIdBaseUrl = "https://nyx.example/",
                NyxIdServiceSlug = "aevatar",
                AccessTokenConfigurationKey = "Aevatar:Status:ResponsesForwardToTeam:BearerToken",
                ScopeId = "scope-1",
                TeamId = "team-1",
                MemberId = "member-1",
                PublishedServiceId = "member-member-1",
                EndpointId = "chat",
                Model = "deepseek/deepseek-chat",
                Prompt = "status probe",
            },
        });

        var stageSlugs = new[]
        {
            "responses-forward-team-01-nyxid-service",
            "responses-forward-team-02-nyxid-proxy-models",
            "responses-forward-team-03-direct-responses",
            "responses-forward-team-04-route-policy",
            "responses-forward-team-05-team-entry-member",
            "responses-forward-team-06-member-binding",
            "responses-forward-team-07-direct-team-invoke",
            "responses-forward-team-08-nyxid-proxy-e2e",
        };

        var stages = manifest.Descriptors
            .Where(d => stageSlugs.Contains(d.Slug, StringComparer.Ordinal))
            .ToArray();

        stages.Select(d => d.Slug).Should().Equal(stageSlugs);
        stages.Should().OnlyContain(d => d.Category == "feature");
        stages.Should().OnlyContain(d => d.ProbeKind == "http_status");
        stages.Should().OnlyContain(d => d.IntervalSeconds == 300);
        stages.Should().OnlyContain(d => d.TimeoutMs == 45_000);
        stages.Should().OnlyContain(d =>
            d.Parameters["Header.Authorization"] ==
            "Bearer ${configuration:Aevatar:Status:ResponsesForwardToTeam:BearerToken}");

        var nyxProxy = stages.Single(d => d.Slug == "responses-forward-team-02-nyxid-proxy-models");
        nyxProxy.Parameters["Url"].Should().Be("https://nyx.example/api/v1/proxy/s/aevatar/v1/models");
        nyxProxy.Parameters["ExpectedBodyRegex"].Should().Contain("\"data\"");

        var e2e = stages.Single(d => d.Slug == "responses-forward-team-08-nyxid-proxy-e2e");
        e2e.Parameters["Url"].Should().Be("https://nyx.example/api/v1/proxy/s/aevatar/v1/responses");
        e2e.Parameters["Method"].Should().Be("POST");
        e2e.Parameters["Body"].Should().Contain("\"stream\":true");
        e2e.Parameters["Body"].Should().Contain("\"input\":\"status probe\"");
        e2e.Parameters["ExpectedBodyContains"].Should().Be("event: response.completed");
        e2e.Parameters["ForbiddenBodyContains"].Should().Be("event: response.failed");
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
