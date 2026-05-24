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
            "chat-completions-api-auth-gate",
            "chat-completion-api-singular-route",
            "models-api-auth-gate",
            "voice-websocket-auth-gate",
            "channel-bot-runtime",
            "nyxid-llm-status",
            "nyxid-llm-gateway-auth-gate",
            "nyxid-channel-bots-auth-gate",
            "nyxid-channel-relay-reply-auth-gate",
        });
        var chatCompletions = manifest.Descriptors.Single(d => d.Slug == "chat-completions-api-auth-gate");
        chatCompletions.DisplayName.Should().Be("OpenAI Chat Completions API auth gate");
        chatCompletions.Category.Should().Be("feature");
        chatCompletions.ProbeKind.Should().Be("http_status");
        chatCompletions.Parameters["Url"].Should().Be("http://127.0.0.1:9999/v1/chat/completions");
        chatCompletions.Parameters["Method"].Should().Be("POST");
        chatCompletions.Parameters["ExpectedStatuses"].Should().Be("401");
        chatCompletions.Parameters["Body"].Should().Be("{}");
        var singularChatCompletion = manifest.Descriptors.Single(d => d.Slug == "chat-completion-api-singular-route");
        singularChatCompletion.DisplayName.Should().Be("OpenAI Chat Completion singular route");
        singularChatCompletion.Category.Should().Be("feature");
        singularChatCompletion.ProbeKind.Should().Be("http_status");
        singularChatCompletion.Parameters["Url"].Should().Be("http://127.0.0.1:9999/v1/chat/completion");
        singularChatCompletion.Parameters["Method"].Should().Be("POST");
        singularChatCompletion.Parameters["ExpectedStatuses"].Should().Be("404");
        singularChatCompletion.Parameters["Body"].Should().Be("{}");
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
