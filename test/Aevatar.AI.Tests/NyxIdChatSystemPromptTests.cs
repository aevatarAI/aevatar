using System.Reflection;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Prompting;
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
        var floor = new BuiltInPromptFloorProvider().GetFloor();
        return SystemPromptLayerComposer.Compose(
            NyxIdChatSystemPrompt.Value,
            floor,
            global: null,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null).Prompt;
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
        var prompt = NyxIdChatSystemPrompt.Value.Content;

        prompt.Should().Contain("## Honest Success Rule");
        prompt.Should().Contain("successful mutating tool result or typed success receipt");
        prompt.Should().Contain("Read-only checks, searches, observation, trigger/rerun requests");
        prompt.Should().Contain("genuine successful mutating tool receipt");
    }

    [Fact]
    public void DecorateSystemPrompt_ShouldUseCatalogSlotsWithoutShadowCandidateBody()
    {
        var agent = new NyxIdChatGAgent(new SystemSkillOverlayPromptInjectionTests.StubBuiltInPromptFloorProvider());
        var profileLayer = new ProfileRoutingPromptLayer(
            "profile routing layer",
            new ProfileRoutingPromptProvenance("profile-test"),
            new PromptLayerBounds(1_024, 256));
        var selectedLayer = new SelectedSkillPromptLayer(
            "selected skill body",
            new SelectedSkillPromptProvenance("selected-test"),
            new PromptLayerBounds(1_024, 256));
        var enforced = new AgentProfileTurnCatalog(
            [], profileLayer, selectedLayer, "intent-alpha", "intent-alpha");
        var shadow = new AgentProfileTurnCatalog(
            [], profileLayer, null, null, "intent-shadow");

        var enforcedPrompt = Decorate(agent, enforced);
        var shadowPrompt = Decorate(agent, shadow);

        enforcedPrompt.Should().Contain("profile routing layer");
        enforcedPrompt.Should().Contain("<selected-skill-procedure>\nselected skill body");
        shadowPrompt.Should().Contain("profile routing layer");
        shadowPrompt.Should().NotContain("selected skill body");
        shadowPrompt.Should().NotContain("selected-skill-procedure");
    }

    private static string Decorate(NyxIdChatGAgent agent, AgentProfileTurnCatalog catalog)
    {
        var method = typeof(NyxIdChatGAgent).GetMethod(
            "DecorateSystemPrompt",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string)method.Invoke(agent, ["kernel", catalog])!;
    }
}
