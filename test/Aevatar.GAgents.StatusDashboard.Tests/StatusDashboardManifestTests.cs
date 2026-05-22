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
            "responses-forward-team-00-nyxid-identity",
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
        stages.Should().OnlyContain(d => d.IntervalSeconds == 300);
        stages.Should().OnlyContain(d => d.TimeoutMs == 45_000);
        stages.Where(d => d.ProbeKind == "http_status").Should().OnlyContain(d =>
            d.Parameters["Header.Authorization"] ==
            "Bearer ${configuration:Aevatar:Status:ResponsesForwardToTeam:BearerToken}");
        stages.Where(d => d.ProbeKind == "http_status").Should().OnlyContain(d =>
            d.Parameters["Auth.Mode"] == "static_bearer" &&
            d.Parameters["Auth.TokenEndpoint"] == "https://nyx.example/oauth/token" &&
            d.Parameters["Auth.ClientIdConfigurationKey"] == "Aevatar:Status:ResponsesForwardToTeam:ClientId" &&
            d.Parameters["Auth.ClientSecretConfigurationKey"] == "Aevatar:Status:ResponsesForwardToTeam:ClientSecret" &&
            d.Parameters["Auth.ClientCredentialsScope"] == "proxy:* llm:proxy");

        stages.Where(d => d.Slug is
                "responses-forward-team-00-nyxid-identity" or
                "responses-forward-team-01-nyxid-service" or
                "responses-forward-team-02-nyxid-proxy-models" or
                "responses-forward-team-03-direct-responses" or
                "responses-forward-team-08-nyxid-proxy-e2e")
            .Should()
            .OnlyContain(d => d.ProbeKind == "http_status");
        stages.Where(d => d.Slug is
                "responses-forward-team-04-route-policy" or
                "responses-forward-team-05-team-entry-member" or
                "responses-forward-team-06-member-binding" or
                "responses-forward-team-07-direct-team-invoke")
            .Should()
            .OnlyContain(d => d.ProbeKind == "responses_forward_team_internal");

        var identity = stages.Single(d => d.Slug == "responses-forward-team-00-nyxid-identity");
        identity.Parameters["Url"].Should().Be("https://nyx.example/api/v1/users/me");
        identity.Parameters["ExpectedBodyRegex"].Should().Contain("\"(id|user_id|sub)\"");

        var nyxService = stages.Single(d => d.Slug == "responses-forward-team-01-nyxid-service");
        nyxService.Parameters["ExpectedBodyRegex"].Should().Contain("\"proxy_url_slug\"");
        nyxService.Parameters["ExpectedBodyRegex"].Should().Contain("/s/aevatar/");

        var nyxProxy = stages.Single(d => d.Slug == "responses-forward-team-02-nyxid-proxy-models");
        nyxProxy.Parameters["Url"].Should().Be("https://nyx.example/api/v1/proxy/s/aevatar/v1/models");
        nyxProxy.Parameters["ExpectedBodyRegex"].Should().Contain("\"data\"");

        var routePolicy = stages.Single(d => d.Slug == "responses-forward-team-04-route-policy");
        routePolicy.Parameters["Stage"].Should().Be("route-policy");
        routePolicy.Parameters["ScopeId"].Should().Be("scope-1");
        routePolicy.Parameters["TeamId"].Should().Be("team-1");
        routePolicy.Parameters["EndpointId"].Should().Be("chat");
        routePolicy.Parameters.Should().NotContainKey("Auth.TokenEndpoint");

        var teamEntryMember = stages.Single(d => d.Slug == "responses-forward-team-05-team-entry-member");
        teamEntryMember.Parameters["Stage"].Should().Be("team-entry-member");
        teamEntryMember.Parameters["MemberId"].Should().Be("member-1");
        teamEntryMember.Parameters["PublishedServiceId"].Should().Be("member-member-1");
        teamEntryMember.Parameters.Should().NotContainKey("Auth.TokenEndpoint");

        var memberBinding = stages.Single(d => d.Slug == "responses-forward-team-06-member-binding");
        memberBinding.DisplayName.Should().Be("Responses -> Studio Team 06 member binding");
        memberBinding.Parameters["Stage"].Should().Be("member-binding");
        memberBinding.Parameters.Should().NotContainKey("BearerTokenConfigurationKey");
        memberBinding.Parameters.Should().NotContainKey("Auth.TokenEndpoint");
        memberBinding.Parameters.Should().NotContainKey("Auth.Mode");

        var directInvoke = stages.Single(d => d.Slug == "responses-forward-team-07-direct-team-invoke");
        directInvoke.Parameters["Stage"].Should().Be("direct-team-invoke");
        directInvoke.Parameters["Prompt"].Should().Be("status probe");
        directInvoke.Parameters["Auth.TokenEndpoint"].Should().Be("https://nyx.example/oauth/token");

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
