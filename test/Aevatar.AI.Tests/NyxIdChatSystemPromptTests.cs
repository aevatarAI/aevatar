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
        prompt.Should().Contain("required_nyx_services");
        prompt.Should().Contain("derive the digest from current facts");
        prompt.Should().Contain("post the digest to the negotiated chat target");
        prompt.Should().Contain("api-github");
        prompt.Should().NotContain("deadline-monitor");
    }

    [Fact]
    public void ComposedPrompt_ShouldKeepExternalCapabilitySecretsOutOfChat()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain(
            "Never ask the user to paste an API key, bearer token, OAuth secret, or downstream credential into chat");
        prompt.Should().Contain("NyxID or the Host-owned Connector configuration owns credentials");
        prompt.Should().NotContain(
            "Credentials the user provides to configure a service are expected input. Accept them");
    }

    [Fact]
    public void ComposedPrompt_ShouldRequireTypedReadinessBeforeWorkflowWrites()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("`list_external_workflow_capabilities`");
        prompt.Should().Contain("`inspect_external_workflow_capability_readiness`");
        prompt.Should().Contain("typed readiness status is `READY`");
        prompt.Should().Contain("`user_service_id` is the NyxID capability identity");
        prompt.Should().Contain("`service_slug_snapshot` is only a display and routing snapshot");
        prompt.Should().Contain("`required_nyx_services`");
        prompt.Should().NotContain("`required_service_slugs`");
        prompt.Should().NotContain("Omit slug → discover all proxyable services");
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
    public void Value_ShouldRequireTypedMissingServiceBlocker()
    {
        var prompt = NyxIdChatSystemPrompt.Value.Content;

        prompt.Should().Contain("`nyxid_require_service`");
        prompt.Should().Contain("not listed in `<connected-services>`");
        prompt.Should().Contain("verify live typed readiness");
        prompt.Should().Contain("`SERVICE_REGISTRATION_REQUIRED`");
        prompt.Should().Contain("must not fabricate a missing-service blocker");
        prompt.Should().Contain("does not create a pending approval");
        prompt.Should().Contain("catalog definitions are not connected UserServices");
        prompt.Should().Contain("after resolving its exact catalog slug, call `nyxid_require_service`");
        prompt.Should().Contain("Never replace this typed handoff with NyxID CLI commands");
        prompt.Should().Contain("credential instructions");
    }

    [Fact]
    public void ComposedPrompt_ShouldUseFinalToolSchemasAsConnectedServiceAuthority()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("final request's tool schemas are the only capability authority");
        prompt.Should().Contain("nyxid_service_inventory");
        prompt.Should().Contain("nyxid_service_operation__");
        prompt.Should().Contain("unprofiled turn");
        prompt.Should().Contain("They do not add tools or expand the authority expressed by the final tool schemas");
    }

    [Fact]
    public void Value_ShouldLoadNyxIdServiceDiscoveryBeforeReadingSenderInventory()
    {
        var prompt = NyxIdChatSystemPrompt.Value.Content;
        var skillCall = prompt.IndexOf(
            "first call `use_skill(skill=\"nyxid-service-discovery\")`",
            StringComparison.Ordinal);
        var inventoryCall = prompt.IndexOf(
            "then call `nyxid_service_inventory`",
            StringComparison.Ordinal);

        skillCall.Should().BeGreaterThanOrEqualTo(0);
        inventoryCall.Should().BeGreaterThan(skillCall);
        prompt.Should().Contain("current sender's live inventory");
        prompt.Should().Contain("temporary read failure");
        prompt.Should().Contain("binding is explicitly missing or revoked");
        prompt.Should().Contain("Do not call `code_execute`");
        prompt.Should().Contain("`nyxid service list`");
        prompt.Should().NotContain("skill=\"nyxid\"");
        prompt.Should().NotContain("call `nyxid_service_inventory` directly");
        prompt.Should().NotContain("Do not load a skill");
    }

    [Fact]
    public void ComposedPrompt_ShouldRouteNyxIdServiceWorkToCurrentSkills()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("use_skill(skill=\"nyxid-service-connect\")");
        prompt.Should().Contain("use_skill(skill=\"nyxid-service-discovery\")");
        prompt.Should().Contain("use_skill(skill=\"nyxid-service-maintenance\")");
        prompt.Should().Contain("use_skill(skill=\"nyxid-service-call\")");
        prompt.Should().NotContain("skill=\"nyxid\"");
    }

    [Fact]
    public void ComposedPrompt_ShouldKeepGenericRuntimePromptProviderNeutral()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("<channel-context>");
        prompt.Should().Contain("identity_hints");
        prompt.Should().Contain("subject");
        prompt.Should().Contain("kind");
        prompt.Should().Contain("value");
        prompt.Should().Contain("provider-backed relay registration");
        prompt.Should().NotContain("Lark");
        prompt.Should().NotContain("lark");
        prompt.Should().NotContain("Feishu");
        prompt.Should().NotContain("api-lark-bot");
        prompt.Should().NotContain("lark_messages_");
        prompt.Should().NotContain("developer-console");
        prompt.Should().NotContain("/open-apis/");
        prompt.Should().NotContain("open_id");
        prompt.Should().NotContain("union_id");
        prompt.Should().NotContain("employee_id");
        prompt.Should().NotContain("lark_chat_id");
        prompt.Should().NotContain("lark_union_id");
    }

    [Fact]
    public void DecorateSystemPrompt_ShouldUseCatalogSlotsWithoutShadowCandidateBody()
    {
        var agent = new NyxIdChatGAgent(
            new SystemSkillOverlayPromptInjectionTests.StubBuiltInPromptFloorProvider(),
            TestAgentToolExecutionPort.Instance);
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
