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
    public void Floor_DiscoversAndExecutesSkillWorkflowsThroughGenericLifecycle()
    {
        var floor = FloorContent();

        var searchCall = floor.IndexOf(
            "call `ornn_search_skills` to find a matching skill",
            StringComparison.Ordinal);
        var loadCall = floor.IndexOf(
            "then `use_skill` to load it",
            StringComparison.Ordinal);
        var workflowExecution = floor.IndexOf(
            "When the loaded skill identifies a runnable Scope Workflow",
            StringComparison.Ordinal);

        searchCall.Should().BeGreaterThanOrEqualTo(0);
        loadCall.Should().BeGreaterThan(searchCall);
        workflowExecution.Should().BeGreaterThan(loadCall);
        floor.Should().Contain("exact workflow identity from the loaded skill");
        floor.Should().NotContain("`scope_workflows_get`");
        floor.Should().Contain("`aevatar_start_workflow.workflow_id`");
        floor.Should().Contain("Build workflow inputs only from the loaded skill's contract");
        floor.Should().Contain("do not encode or override those rules in this built-in overlay");
        floor.Should().Contain("once with `wait=\"stream\"`");
        floor.Should().Contain("`workflow_current_state.actor_id`");
        floor.Should().Contain("`workflow_current_state.command_id`");
        floor.Should().Contain("that `run_id` as `workflow_run_id`");
        floor.Should().Contain("`actor_id` as `actor_id`");
        floor.Should().Contain("Claim completion only from the committed report");
        floor.Should().Contain("workflow and matching command");
        floor.Should().Contain("If the artifact is pending, retry the read");
        floor.Should().Contain("report that limitation instead of inferring the missing content");
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
