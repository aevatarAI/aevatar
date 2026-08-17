using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class BuiltInPromptFloorProviderTests
{
    private static string FloorContent() => new BuiltInPromptFloorProvider().GetFloor().Content;

    [Fact]
    public void GetFloor_ReturnsNonEmptyMandatoryLayerWithEmbeddedProvenance()
    {
        var floor = new BuiltInPromptFloorProvider().GetFloor();

        floor.Content.Should().NotBeNullOrWhiteSpace();
        floor.Provenance.Source.Should().Be("embedded:system-skill-overlay-default.md");
        floor.Bounds.MaxUtf8Bytes.Should().Be(32 * 1024);
        floor.Bounds.MaxEstimatedTokens.Should().Be(8192);
        floor.ActualUtf8Bytes.Should().BeLessThanOrEqualTo(floor.Bounds.MaxUtf8Bytes);
        floor.EstimatedTokens.Should().BeLessThanOrEqualTo(floor.Bounds.MaxEstimatedTokens);
    }

    [Fact]
    public void AddNyxIdChat_AlwaysRegistersFloorWithoutInventingGlobalProvider()
    {
        var services = new ServiceCollection();

        services.AddNyxIdChat();

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IBuiltInPromptFloorProvider));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(ISystemSkillOverlayProvider));
    }

    [Theory]
    [InlineData("grant the requester access BEFORE you return the link")]
    [InlineData("organization-scoped sharing mechanism")]
    [InlineData("provider-specific typed sharing tool")]
    [InlineData("use_skill(skill=\"nyxid-service-discovery\")")]
    [InlineData("then call `nyxid_service_inventory`")]
    [InlineData("temporary read failure")]
    [InlineData("binding is explicitly missing or revoked")]
    [InlineData("ornn_search_skills")]
    [InlineData("api-github-pat")]
    [InlineData("provider-backed relay registration")]
    [InlineData("agent_delivery_targets")]
    [InlineData("scheduled_agent_creator")]
    [InlineData("one_shot")]
    [InlineData("ornn_publish_skill")]
    public void Floor_RetainsMandatoryCapabilityInstructions(string requiredMarker)
    {
        FloorContent().Should().Contain(requiredMarker);
    }

    [Fact]
    public void Floor_LoadsNyxIdDiscoverySkillBeforeReadingSenderInventory()
    {
        var floor = FloorContent();
        var skillCall = floor.IndexOf(
            "first call `use_skill(skill=\"nyxid-service-discovery\")`",
            StringComparison.Ordinal);
        var inventoryCall = floor.IndexOf(
            "then call `nyxid_service_inventory`",
            StringComparison.Ordinal);

        skillCall.Should().BeGreaterThanOrEqualTo(0);
        inventoryCall.Should().BeGreaterThan(skillCall);
        floor.Should().Contain("inventory read present in the final request's tool schemas");
        floor.Should().Contain("When `nyxid_service_inventory` is present");
        floor.Should().Contain("When `nyxid_service_inventory` is absent");
        floor.Should().Contain("such as `nyxid_services`");
        floor.Should().Contain("route the read through the catalog/service-inspection path");
        floor.Should().Contain("establishes current sender-specific service facts");
        floor.Should().Contain("execution tools only run supplied work and cannot establish that inventory");
        floor.Should().Contain("typed inventory result as the authority for the current sender");
        floor.Should().NotContain("Do not call `code_execute`");
        floor.Should().NotContain("typed-tool exception");
        floor.Should().NotContain("call `nyxid_service_inventory` directly");
        floor.Should().NotContain("Do not call `use_skill`");
    }

    [Fact]
    public void Floor_RoutesFinanceIntentsThroughExactWorkflowSkills()
    {
        var floor = FloorContent();

        floor.Should().Contain("`ornn_search_skills` with query `发票预检`");
        floor.Should().Contain("exact slug\n  `fin-invoice-precheck-approval`");
        floor.Should().Contain("`use_skill(skill=\"fin-invoice-precheck-approval\")`");
        floor.Should().Contain("`fin_invoice_precheck_approval`");
        floor.Should().Contain("`ornn_search_skills` with query `预算差异`");
        floor.Should().Contain("exact slug\n  `fin-budget-variance-monitor`");
        floor.Should().Contain("`use_skill(skill=\"fin-budget-variance-monitor\")`");
        floor.Should().Contain("`fin_budget_variance_monitor`");
        floor.Should().Contain("A submission request matches this route only when it explicitly names or refers back");
        floor.Should().Contain("generic submission request");
        floor.Should().Contain("does not match it");
        floor.Should().Contain("returned `workflow.workflow_id` unchanged");
        floor.Should().Contain("`aevatar_start_workflow.workflow_id`");
        floor.Should().Contain("once with `wait=\"stream\"`");
        floor.Should().Contain("`workflow_current_state.actor_id`");
        floor.Should().Contain("`workflow_current_state.command_id`");
        floor.Should().Contain("that `run_id` as `workflow_run_id`");
        floor.Should().Contain("`actor_id` as `actor_id`");
        floor.Should().Contain("outer `workflow_name` to equal the");
        floor.Should().Contain("matching canonical value (`fin_invoice_precheck_approval` or `fin_budget_variance_monitor`)");
        floor.Should().Contain("exactly, with `status=Completed`");
        floor.Should().Contain("`final_output` as complete JSON");
        floor.Should().Contain("If `final_output` is truncated");
        floor.Should().Contain("result is unproven");
        floor.Should().Contain("current sender's delegated account");
        floor.Should().Contain("test-data boundaries");
        floor.Should().Contain("always use `submit:false` and never set `submit:true`");
        floor.Should().NotContain("post-preview confirmation contract");
        floor.Should().NotContain("explicitly confirms submission");

        floor.IndexOf("Route these reviewed FIN preview intents", StringComparison.Ordinal)
            .Should().BeLessThan(floor.IndexOf("When the user mentions a named skill", StringComparison.Ordinal));
    }

    [Fact]
    public void Floor_PairsExactSourceExecutionWithTargetAwareCodexDelegation()
    {
        var floor = FloorContent();

        floor.Should().Contain("caller-provided exact Python, JavaScript, TypeScript, or Bash source");
        floor.Should().Contain("Delegate a natural-language task to Codex");
        floor.Should().Contain("`managed_sandbox` for the fixed isolated runtime without human approval");
        floor.Should().Contain("`private_ssh` for a real user host");
        floor.Should().Contain("`private_ssh` requires approval");
        floor.Should().NotContain("deterministic sandbox computation");
        floor.Should().NotContain("`codex_exec` always requires approval");
    }

    [Fact]
    public void Floor_DoesNotOverrideKernelInvariants()
    {
        FloorContent().Should().Contain("never overrides");
    }

    [Fact]
    public void Floor_DoesNotLeakProviderSpecificRelayPrompting()
    {
        var floor = FloorContent();

        floor.Should().NotContain("Lark");
        floor.Should().NotContain("lark");
        floor.Should().NotContain("Feishu");
        floor.Should().NotContain("api-lark-bot");
        floor.Should().NotContain("lark_messages_");
        floor.Should().NotContain("developer-console");
        floor.Should().NotContain("/open-apis/");
    }
}
