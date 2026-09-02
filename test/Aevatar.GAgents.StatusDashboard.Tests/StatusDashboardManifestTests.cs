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
            "studio-health",
            "app-context",
            "aevatar-core-loop-tools",
            "audit-query-index",
            "channel-bot-runtime",
            "nyxid-http-health",
            "nyxid-oidc-discovery",
        });
        var nyxIdHealth = manifest.Descriptors.Single(d => d.Slug == "nyxid-http-health");
        nyxIdHealth.DisplayName.Should().Be("NyxID · health");
        nyxIdHealth.Category.Should().Be("upstream");
        nyxIdHealth.Severity.Should().Be("standard");
        nyxIdHealth.ProbeKind.Should().Be("http_status");
        nyxIdHealth.Parameters["Url"].Should().Be("${configuration:Aevatar:NyxId:Authority}/health");
        nyxIdHealth.Parameters["ExpectedStatuses"].Should().Be("200");
        // Canon §9/§9.1: critical surfaces carry the "critical" weight; default set has no
        // credentialed canary targets until a canary bearer is configured.
        manifest.Descriptors.Single(d => d.Slug == "self-readiness").Severity.Should().Be("critical");
        manifest.Descriptors.Single(d => d.Slug == "aevatar-core-loop-tools").Severity.Should().Be("critical");
        manifest.Descriptors.Select(d => d.Slug).Should().NotContain("llm-completion-canary");
        manifest.Descriptors.Select(d => d.Slug).Should().NotContain("llm-catalog");
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
        var audit = manifest.Descriptors.Single(d => d.Slug == "audit-query-index");
        audit.DisplayName.Should().Be("Audit Trail Query / Index");
        audit.Category.Should().Be("feature");
        audit.Severity.Should().Be("standard");
        audit.ProbeKind.Should().Be("audit_query_index");
        manifest.Descriptors.Should().OnlyContain(d => d.IntervalSeconds == 60);
    }

    [Fact]
    public void FromOptions_EmitsLlmCanary_OnlyWhenCanaryBearerConfigured()
    {
        var manifest = StatusDashboardManifest.FromOptions(new StatusDashboardOptions
        {
            SelfBaseUrl = "http://127.0.0.1:9999/",
            Probe = new StatusProbeOptions
            {
                CanaryBearer = "nyxid-canary-key",
                CanaryModel = "deepseek/deepseek-v4-flash",
                CanaryMaxTokens = 8,
                CanaryIntervalSeconds = 900,
            },
        });

        var completion = manifest.Descriptors.Single(d => d.Slug == "llm-completion-canary");
        completion.Category.Should().Be("llm");
        completion.Severity.Should().Be("canary");
        completion.IntervalSeconds.Should().Be(900);
        completion.Parameters["Method"].Should().Be("POST");
        completion.Parameters["ExpectedStatuses"].Should().Be("200");
        completion.Parameters["ExpectedBodyContains"].Should().Be("choices");
        completion.Parameters["Auth.Mode"].Should().Be("static_bearer");
        completion.Parameters["Auth.StaticBearerConfigurationKey"]
            .Should().Be(StatusProbeOptions.CanaryBearerConfigurationKey);
        completion.Parameters["Body"].Should().Contain("deepseek/deepseek-v4-flash").And.Contain("\"max_tokens\":8");

        var catalog = manifest.Descriptors.Single(d => d.Slug == "llm-catalog");
        catalog.Parameters["Auth.Mode"].Should().Be("static_bearer");
        catalog.Parameters["ExpectedStatuses"].Should().Be("200");

        // Canon §9.1: never an expect-401 auth gate, even for credentialed targets.
        manifest.Descriptors
            .Select(static d => d.Parameters.TryGetValue("ExpectedStatuses", out var v) ? v : string.Empty)
            .Should()
            .NotContain(static v => v.Split(',', StringSplitOptions.TrimEntries).Contains("401"));
    }

    [Fact]
    public void FromOptions_EmitsOrchestrationProbes_OnlyWhenScopeConfigured()
    {
        var without = StatusDashboardManifest.FromOptions(new StatusDashboardOptions
        {
            SelfBaseUrl = "http://127.0.0.1:9999/",
        });
        without.Descriptors.Select(d => d.Slug)
            .Should().NotContain(new[] { "orchestration-scope-read", "observatory-read" });

        var withScope = StatusDashboardManifest.FromOptions(new StatusDashboardOptions
        {
            SelfBaseUrl = "http://127.0.0.1:9999/",
            Probe = new StatusProbeOptions { ScopeId = "scope-123" },
        });
        var orchestration = withScope.Descriptors.Single(d => d.Slug == "orchestration-scope-read");
        orchestration.Category.Should().Be("orchestration");
        orchestration.Parameters["Url"].Should().Be("http://127.0.0.1:9999/api/scopes/scope-123/services");
        orchestration.Parameters["ExpectedStatuses"].Should().Be("200");
        orchestration.Parameters["Auth.Mode"].Should().Be("scope_service_token");
        orchestration.Parameters["Auth.ScopeId"].Should().Be("scope-123");

        var observatory = withScope.Descriptors.Single(d => d.Slug == "observatory-read");
        observatory.Parameters["Url"].Should().Be("http://127.0.0.1:9999/api/workflow/observatory/me");
        observatory.Parameters["Auth.Mode"].Should().Be("scope_service_token");
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
