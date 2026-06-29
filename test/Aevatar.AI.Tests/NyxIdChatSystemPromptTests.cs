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
        prompt.Should().Contain("Treat runnable/page-visible workflow creation as a Scope Workflow upsert");
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
    public void Value_ShouldRouteLarkWorkflowCreationIntentByCapability()
    {
        var prompt = NyxIdChatSystemPrompt.Value;

        prompt.Should().Contain("## Workflow creation semantics");
        prompt.Should().Contain("### Scope Workflow vs Ornn publish intent");
        prompt.Should().Contain("`scope_workflows_upsert`");
        prompt.Should().Contain("This is the default for runnable workflow creation");
        prompt.Should().Contain("Do not use `ornn_publish_skill` for ordinary runnable/page-visible workflow creation");
        prompt.Should().Contain("call `scope_workflows_upsert` first as the primary runnable store");
        prompt.Should().Contain("then call `ornn_publish_skill` only for the explicit package/export part");
        prompt.Should().Contain("`scope_workflows_get` or `scope_workflows_list`");
        prompt.Should().Contain("then call `aevatar_start_workflow` with the stable `workflow_id`");
        prompt.Should().Contain("Do not publish an Ornn skill just to run an existing workflow");
    }

    [Fact]
    public void Value_ShouldStateScheduledAgentCreationDoesNotRequestRemoteApproval()
    {
        var prompt = NyxIdChatSystemPrompt.Value;

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
