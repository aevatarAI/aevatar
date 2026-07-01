using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Locks the built-in default System Skill Overlay content. The kernel (system-prompt.md) was slimmed
/// to invariants and moved its per-domain capability how-to into this default overlay, so both reply
/// seams stay behavior-complete before a host wires the Ornn-sourced overlay. These assertions fail if
/// any load-bearing, always-on instruction is dropped from the default overlay (a silent regression).
/// </summary>
public sealed class SystemSkillOverlayDefaultProviderTests
{
    private static string DefaultOverlayMarkdown()
    {
        var overlay = new SystemSkillOverlayDefaultProvider().GetCurrent();
        overlay.Should().NotBeNull();
        return overlay!.OverlayMarkdown;
    }

    [Fact]
    public void GetCurrent_ShouldReturnNonEmptyDefaultOverlay()
    {
        var overlay = new SystemSkillOverlayDefaultProvider().GetCurrent();

        overlay.Should().NotBeNull();
        overlay!.OverlayMarkdown.Should().NotBeNullOrWhiteSpace();
        overlay.SourceWatermark.Should().Be("builtin-default");
    }

    [Theory]
    // Provisioning grant-before-link — the most load-bearing always-on Lark instruction.
    [InlineData("grant the requester full access BEFORE you return the link")]
    [InlineData("/open-apis/drive/v1/permissions/")]
    [InlineData("tenant_editable")]
    // NyxID / Ornn manual loading triggers.
    [InlineData("use_skill(skill=\"nyxid\")")]
    [InlineData("ornn_search_skills")]
    // Capability tool details dropped from the kernel.
    [InlineData("api-github-pat")]
    [InlineData("/sendMessage")]
    // Aevatar-specific channel + scheduling + workflow how-to.
    [InlineData("register_lark_via_nyx")]
    [InlineData("agent_delivery_targets")]
    [InlineData("scheduled_agent_creator")]
    [InlineData("one_shot")]
    [InlineData("ornn_publish_skill")]
    public void DefaultOverlay_ShouldRetainMovedCapabilityHowTo(string requiredMarker)
    {
        DefaultOverlayMarkdown().Should().Contain(requiredMarker);
    }

    [Fact]
    public void DefaultOverlay_ShouldNotOverrideKernelInvariants()
    {
        // The overlay extends capabilities; it must not re-declare itself as overriding safety/honesty.
        DefaultOverlayMarkdown().Should().Contain("never overrides");
    }
}
