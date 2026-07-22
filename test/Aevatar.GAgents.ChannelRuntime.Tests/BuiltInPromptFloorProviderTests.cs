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
    [InlineData("grant the requester full access BEFORE you return the link")]
    [InlineData("/open-apis/drive/v1/permissions/")]
    [InlineData("tenant_editable")]
    [InlineData("use_skill(skill=\"nyxid\")")]
    [InlineData("ornn_search_skills")]
    [InlineData("api-github-pat")]
    [InlineData("/sendMessage")]
    [InlineData("register_channel_via_nyx")]
    [InlineData("agent_delivery_targets")]
    [InlineData("scheduled_agent_creator")]
    [InlineData("one_shot")]
    [InlineData("ornn_publish_skill")]
    public void Floor_RetainsMandatoryCapabilityInstructions(string requiredMarker)
    {
        FloorContent().Should().Contain(requiredMarker);
    }

    [Fact]
    public void Floor_DoesNotOverrideKernelInvariants()
    {
        FloorContent().Should().Contain("never overrides");
    }
}
