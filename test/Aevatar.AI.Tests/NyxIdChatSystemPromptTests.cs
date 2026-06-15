using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class NyxIdChatSystemPromptTests
{
    [Fact]
    public void Value_ShouldContainLongRunningTaskAutomationPlaybook()
    {
        var prompt = NyxIdChatSystemPrompt.Value;

        prompt.Should().NotBeNullOrWhiteSpace();
        prompt.Should().Contain("## Long-running task automation playbook");
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
    public void Value_ShouldStateScheduledAgentCreationDoesNotRequestRemoteApproval()
    {
        var prompt = NyxIdChatSystemPrompt.Value;

        prompt.Should().Contain("This write command does not request remote approval");
        prompt.Should().Contain("Do not say it is waiting for remote approval");
        prompt.Should().NotContain("waiting for NyxID approval");
    }
}
