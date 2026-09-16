using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Abstractions.CodeExecution;
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
        "nyxid_services", "nyxid_approvals", "nyxid_request_key_create",
        "nyxid_request_key_rotate",
    };

    private static readonly HashSet<string> RelaySafeTools = new(StringComparer.Ordinal)
    {
        "nyxid_proxy", "nyxid_require_service", "code_execute", "nyxid_llm_status", "nyxid_catalog",
        "nyxid_channel_events",
    };

    [Fact]
    public async Task DiscoverToolsAsync_ClassifiesEveryToolByHumanSessionRequirement()
    {
        var options = new NyxIdToolOptions { BaseUrl = "https://nyx.example" };
        var client = new NyxIdApiClient(options, new HttpClient());
        var privilegedSource = new NyxIdAgentToolSource(options, client);
        var executionSource = new NyxIdExecutionAgentToolSource(
            options,
            client,
            codeExecutionPorts: [new StubCodeExecutionPort()]);

        var tools = (await privilegedSource.DiscoverToolsAsync())
            .Concat(await executionSource.DiscoverToolsAsync())
            .ToArray();

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

        var channelEvents = tools.Single(tool => tool.Name == "nyxid_channel_events");
        channelEvents.Should().BeAssignableTo<IAgentToolCapabilityDescriptor>()
            .Which.Capabilities.Should().Contain(AgentToolCapabilities.ExcludeFromNyxIdChat,
                "channel-event mutation is Class X on the Assistant surface even though it remains relay-safe");
    }

    private static bool DeclaresHumanSession(IAgentTool tool) =>
        tool is IAgentToolCapabilityDescriptor descriptor &&
        descriptor.Capabilities.Contains(AgentToolCapabilities.RequiresHumanSession);

    private sealed class StubCodeExecutionPort : ICodeExecutionPort
    {
        public Task<CodeExecutionOutcome> ExecuteAsync(
            CodeExecutionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(CodeExecutionOutcome.Succeeded(
                new CodeExecutionResult(string.Empty, string.Empty, 0),
                new CodeExecutionRouteIdentity(
                    "chrono-sandbox",
                    "svc-code-alpha",
                    CodeExecutionRouteIdentitySource.NyxIdUserServiceCatalog)));
    }
}
