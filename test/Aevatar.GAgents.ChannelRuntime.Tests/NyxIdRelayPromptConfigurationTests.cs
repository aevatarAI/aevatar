using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayPromptConfigurationTests
{
    [Fact]
    public void ChannelRuntimeConfiguration_PointsUncoveredLarkOpsToSkillDiscovery()
    {
        var section = NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(options: null);

        // Lark operations the typed tools do not cover must steer the model to discover a
        // skill generically, which then drives nyxid_proxy against api-lark-bot.
        section.Should().Contain("ornn_search_skills");
        section.Should().Contain("typed tools above do not cover");
        section.Should().Contain("api-lark-bot");
    }

    [Fact]
    public void ChannelRuntimeConfiguration_StillNamesDurableTypedTools()
    {
        // The iter25 direction (name durable typed tools in the prompt) is preserved; the
        // discovery guidance is additive, not a replacement.
        var section = NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(options: null);

        section.Should().Contain("lark_messages_send");
        section.Should().Contain("lark_approvals_act");
    }

    [Fact]
    public void ChannelRuntimeConfiguration_DoesNotHardcodeASpecificSkillName()
    {
        // CLAUDE.md: production prompts/routing must not be aware of specific skill names;
        // the model reaches Lark skills only through generic discovery.
        var section = NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(options: null);

        section.Should().NotContain("lark-im-ops");
        section.Should().NotContain("lark-calendar-ops");
        section.Should().NotContain("lark-sheets-ops");
        section.Should().NotContain("lark-docx-ops");
    }
}
