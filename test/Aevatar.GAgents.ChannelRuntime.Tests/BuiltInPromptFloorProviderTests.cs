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
    [InlineData("`nyxid service list`")]
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
        floor.Should().Contain("current sender's live inventory");
        floor.Should().Contain("Do not call `code_execute`");
        floor.Should().NotContain("typed-tool exception");
        floor.Should().NotContain("call `nyxid_service_inventory` directly");
        floor.Should().NotContain("Do not call `use_skill`");
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
