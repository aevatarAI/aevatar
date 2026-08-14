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
    public void ComposedPrompt_ShouldRouteReviewedFinIntentsToExactScopeWorkflows()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("Route these reviewed FIN preview intents before generic skill discovery");
        prompt.Should().Contain("fin-invoice-precheck-approval");
        prompt.Should().Contain("fin_invoice_precheck_approval");
        prompt.Should().Contain("fin-budget-variance-monitor");
        prompt.Should().Contain("fin_budget_variance_monitor");
        prompt.Should().Contain("A submission request matches this route only when it explicitly names or refers back");
        prompt.Should().Contain("generic submission request");
        prompt.Should().Contain("does not match it");
        prompt.Should().Contain("returned `workflow.workflow_id` unchanged");
        prompt.Should().Contain("once with `wait=\"stream\"`");
        prompt.Should().Contain("`workflow_current_state.actor_id`");
        prompt.Should().Contain("`workflow_current_state.command_id`");
        prompt.Should().Contain("that `run_id` as `workflow_run_id`");
        prompt.Should().Contain("outer `workflow_name` to equal the");
        prompt.Should().Contain("matching canonical value (`fin_invoice_precheck_approval` or `fin_budget_variance_monitor`)");
        prompt.Should().Contain("exactly, with `status=Completed`");
        prompt.Should().Contain("`final_output` as complete JSON");
        prompt.Should().Contain("`mode=\"preview\"`");
        prompt.Should().Contain("`side_effects=false`");
        prompt.Should().Contain("current sender's delegated account");
        prompt.Should().Contain("test-data boundaries");
        prompt.Should().NotContain("TxGrbUmPQa8Lkus2z9UlEuffgUc");
        prompt.Should().NotContain("tbl6Xceu4Ogkod6o");
        prompt.Should().Contain("always use `submit:false` and never set `submit:true`");
        prompt.Should().NotContain("explicitly confirms submission");
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
        prompt.Should().Contain("typed successful mutating tool receipt for that exact mutation");
        prompt.Should().Contain("A successful receipt for another action");
        prompt.Should().Contain("Read-only checks, searches, observation, trigger/rerun requests");
        prompt.Should().Contain("genuine successful mutating tool receipt");
    }

    [Fact]
    public void Value_ShouldAskForAllGenuineGapsInOneCompositeQuestion()
    {
        var prompt = NyxIdChatSystemPrompt.Value.Content;

        prompt.Should().Contain("identify all genuine information gaps");
        prompt.Should().Contain(
            "call `ask_user` once with one composite prose question, `options: []`, and `allow_free_text: true`");
        prompt.Should().Contain("do not answer with the question as plain assistant text");
        prompt.Should().Contain("do not execute until the answer arrives");
        prompt.Should().Contain("do not drip-feed one question per gap");
        prompt.Should().Contain("Suggested defaults are editable hints, never binding choices");
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
        prompt.Should().Contain("For every connect, add, or authorize request, call `nyxid_catalog` in the current turn");
        prompt.Should().Contain("`catalogIdentityCandidate`");
        prompt.Should().Contain("only the exact `slug` returned by that catalog read may enter");
        prompt.Should().Contain("Never pass a provider slug, display name, or guessed value");
        prompt.Should().Contain("for a bare source-code-hosting connection");
        prompt.Should().Contain("repository access scope instead of omitting scopes");
        prompt.Should().Contain("Never replace this typed handoff with NyxID CLI commands");
        prompt.Should().Contain("credential instructions");
    }

    [Fact]
    public void Value_ShouldEnforceFourTierPreferenceAndGeneralizedCannotCheck()
    {
        var prompt = NyxIdChatSystemPrompt.Value.Content;

        var exactOperation = prompt.IndexOf(
            "admitted exact-instance NyxID connected-service operation",
            StringComparison.Ordinal);
        var browserAction = prompt.IndexOf(
            "`service.connect` browser action",
            StringComparison.Ordinal);
        var aevatarExecutor = prompt.IndexOf(
            "Aevatar-ecosystem tool or skill",
            StringComparison.Ordinal);
        var honestStop = prompt.IndexOf(
            "If none is available, stop honestly",
            StringComparison.Ordinal);

        exactOperation.Should().BeGreaterThanOrEqualTo(0);
        browserAction.Should().BeGreaterThan(exactOperation);
        aevatarExecutor.Should().BeGreaterThan(browserAction);
        honestStop.Should().BeGreaterThan(aevatarExecutor);
        prompt.Should().Contain("cannot check right now");
        prompt.Should().Contain("never proves that a connection, binding, resource, or record is absent");
        prompt.Should().Contain("Claim absence only from a successful authoritative read");
        prompt.Should().Contain("Never present an Aevatar executor as a NyxID connected service");
    }

    [Fact]
    public void ComposedPrompt_ShouldKeepReadOnlyResearchFallbackAndArtifactHonest()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("include that scope change in the single composite `ask_user` question");
        prompt.Should().Contain("require the user's free-text consent before any tool runs");
        prompt.Should().Contain("name it as Aevatar `web_search`");
        prompt.Should().Contain("actor-derived `auto` gate");
        prompt.Should().Contain("separate facts supported by successful reads from facts that `cannot check right now`");
        prompt.Should().Contain("no reservation, publication, or other external mutation occurred");
        prompt.Should().Contain("partial-work receipt based on committed step evidence");
        prompt.Should().Contain("late evidence cannot advance the stopped task");
    }

    [Fact]
    public void Value_ShouldKeepLocalHandoffsAndExcludedOperationsHonest()
    {
        var prompt = NyxIdChatSystemPrompt.Value.Content;

        prompt.Should().Contain("Class-L operations run on the user's own machine");
        prompt.Should().Contain("one exact copyable `nyxid ...` command");
        prompt.Should().Contain("`start the node daemon` maps exactly to `nyxid node daemon start`");
        prompt.Should().Contain("Do not claim that the command ran");
        prompt.Should().Contain("Class-X operations are excluded from Assistant v1");
        prompt.Should().Contain("Billing, platform administration, pre-authentication, channel-bot/event mutation, and oracle operations");
        prompt.Should().Contain("Do not expose or fabricate a tool, browser action, approval card, or execution receipt");
        prompt.Should().Contain("do not guess a verb, invent a URL, or turn a mutation into manual instructions");
    }

    [Fact]
    public void ComposedPrompt_ShouldUseFinalToolSchemasAsConnectedServiceAuthority()
    {
        var prompt = ComposedAgentPrompt();

        prompt.Should().Contain("final request's tool schemas are the only capability authority");
        prompt.Should().Contain("nyxid_service_inventory");
        prompt.Should().Contain("nyxop_*");
        prompt.Should().NotContain("nyxid_service_update")
            .And.NotContain("nyxid_service_route")
            .And.NotContain("nyxid_service_delete");
        prompt.Should().Contain("unprofiled turn");
        prompt.Should().Contain("They do not add tools or expand the authority expressed by the final tool schemas");
    }

    [Fact]
    public void ComposedPrompt_ShouldRouteSenderInventoryThroughPositiveServiceInspection()
    {
        var prompt = ComposedAgentPrompt();
        var skillCall = prompt.IndexOf(
            "first call `use_skill(skill=\"nyxid-service-discovery\")`",
            StringComparison.Ordinal);
        var inventoryCall = prompt.IndexOf(
            "then call `nyxid_service_inventory`",
            StringComparison.Ordinal);

        skillCall.Should().BeGreaterThanOrEqualTo(0);
        inventoryCall.Should().BeGreaterThan(skillCall);
        prompt.Should().Contain("inventory read present in the final request's tool schemas");
        prompt.Should().Contain("When `nyxid_service_inventory` is present");
        prompt.Should().Contain("When `nyxid_service_inventory` is absent");
        prompt.Should().Contain("such as `nyxid_services`");
        prompt.Should().Contain("route the read through the catalog/service-inspection path");
        prompt.Should().Contain("establishes current sender-specific service facts");
        prompt.Should().Contain("execution tools only run supplied work and cannot establish that inventory");
        prompt.Should().Contain("typed inventory result as the authority for the current sender");
        prompt.Should().Contain("temporary read failure");
        prompt.Should().Contain("binding is explicitly missing or revoked");
        prompt.Should().NotContain("Do not call `code_execute`");
        prompt.Should().NotContain("skill=\"nyxid\"");
        prompt.Should().NotContain("call `nyxid_service_inventory` directly");
        prompt.Should().NotContain("Do not load a skill");
    }

    [Fact]
    public void KernelIndex_ShouldDescribeBothExecutionVerbsWithTargetAwareApproval()
    {
        var prompt = NyxIdChatSystemPrompt.Value.Content;

        prompt.Should().Contain("caller-provided exact Python, JavaScript, TypeScript, or Bash source");
        prompt.Should().Contain("Delegate a natural-language task to Codex");
        prompt.Should().Contain("`managed_sandbox` for the fixed isolated runtime without human approval");
        prompt.Should().Contain("`private_ssh` for a real user host");
        prompt.Should().Contain("`private_ssh` requires approval");
        prompt.Should().NotContain("deterministic sandbox computation");
        prompt.Should().NotContain("`codex_exec` always requires approval");
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
