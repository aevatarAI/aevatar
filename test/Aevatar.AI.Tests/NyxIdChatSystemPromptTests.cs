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
        prompt.Should().Contain("api-github");
        prompt.Should().Contain("deadline-monitor");
    }
}
