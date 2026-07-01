using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class NyxIdChatSystemPromptTests
{
    // The kernel (NyxIdChatSystemPrompt.Value) was slimmed to invariants; the per-domain capability
    // how-to moved into the force-injected System Skill Overlay. What the agent actually sees is the
    // composed prompt = kernel + default overlay, so capability-content assertions run against that
    // composition while invariant assertions stay on the kernel itself.
    private static string ComposedAgentPrompt()
    {
        var overlay = new SystemSkillOverlayDefaultProvider().GetCurrent(default);
        overlay.Should().NotBeNull();
        return $"{NyxIdChatSystemPrompt.Value}\n\n{overlay!.OverlayMarkdown}";
    }

    [Fact]
    public void ComposedPrompt_ShouldContainLongRunningTaskAutomationPlaybook()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Contain("Long-running task automation playbook");
        prompt.Should().Contain("Recognize the request as automation.");
        prompt.Should().Contain("reply_with_interaction");
        prompt.Should().Contain("ornn_publish_skill");
        prompt.Should().Contain("scheduled_agent_creator");
        prompt.Should().Contain("agent_delivery_targets");
        prompt.Should().Contain("loaded skill metadata and instructions");
        prompt.Should().Contain("fetch live data through `nyxid_proxy`");
        prompt.Should().Contain("required_service_slugs");
        prompt.Should().Contain("derive the digest from current facts");
        prompt.Should().Contain("post the digest to the negotiated chat target");
        prompt.Should().Contain("api-github");
        prompt.Should().NotContain("deadline-monitor");
    }

    [Fact]
    public void ComposedPrompt_ShouldStateScheduledAgentCreationDoesNotRequestRemoteApproval()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("This write command does not request remote approval");
        prompt.Should().Contain("Do not say it is waiting for remote approval");
        prompt.Should().NotContain("waiting for NyxID approval");
    }

    [Fact]
    public void Value_ShouldContainHonestSuccessRule()
    {
        var prompt = NyxIdChatSystemPrompt.Value;

        prompt.Should().Contain("## Honest Success Rule");
        prompt.Should().Contain("successful mutating tool result or typed success receipt");
        prompt.Should().Contain("Read-only checks, searches, observation, trigger/rerun requests");
        prompt.Should().Contain("genuine successful mutating tool receipt");
    }
}
