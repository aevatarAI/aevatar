using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;
using Xunit;

namespace Aevatar.AI.Tests;

/// <summary>
/// Issue #2580 Item 2: NyxID platform tools whose broker endpoints only accept a human-session
/// credential self-declare <c>AgentToolCapabilities.RequiresHumanSession</c> so the channel reply
/// path can filter them out of bot-class relay turns (where the broker would reject them). Tools that
/// target the delegated proxy / LLM / discovery surfaces must NOT declare it — they stay available in
/// relay turns. This pins the classification and fails loudly if a new tool is left unclassified.
/// </summary>
public class NyxIdAgentToolSourceHumanSessionGatingTests
{
    private static readonly HashSet<string> HumanSessionOnlyTools = new(StringComparer.Ordinal)
    {
        "nyxid_account", "nyxid_profile", "nyxid_mfa", "nyxid_sessions", "nyxid_api_keys",
        "nyxid_external_keys", "nyxid_nodes", "nyxid_endpoints", "nyxid_notifications",
        "nyxid_providers", "nyxid_orgs", "nyxid_admin", "nyxid_channel_bots", "nyxid_status",
        "nyxid_services", "nyxid_approvals",
    };

    private static readonly HashSet<string> RelaySafeTools = new(StringComparer.Ordinal)
    {
        "nyxid_proxy", "nyxid_require_service", "code_execute", "nyxid_llm_status", "nyxid_catalog",
        "nyxid_channel_events",
    };

    [Fact]
    public async Task DiscoverToolsAsync_ClassifiesEveryToolByHumanSessionRequirement()
    {
        var source = new NyxIdAgentToolSource(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example" }, new HttpClient()));

        var tools = await source.DiscoverToolsAsync();

        tools.Should().NotBeEmpty();
        foreach (var tool in tools)
        {
            var isClassified = HumanSessionOnlyTools.Contains(tool.Name) || RelaySafeTools.Contains(tool.Name);
            isClassified.Should().BeTrue(
                $"{tool.Name} is unclassified — add it to the human-only or relay-safe set for the relay-turn gate");
            DeclaresHumanSession(tool).Should().Be(
                HumanSessionOnlyTools.Contains(tool.Name),
                $"{tool.Name}'s RequiresHumanSession capability must match its broker-surface classification");
        }

        tools.Select(t => t.Name).Should().Contain(HumanSessionOnlyTools);
        tools.Select(t => t.Name).Should().Contain(RelaySafeTools);
    }

    private static bool DeclaresHumanSession(IAgentTool tool) =>
        tool is IAgentToolCapabilityDescriptor descriptor &&
        descriptor.Capabilities.Contains(AgentToolCapabilities.RequiresHumanSession);
}
