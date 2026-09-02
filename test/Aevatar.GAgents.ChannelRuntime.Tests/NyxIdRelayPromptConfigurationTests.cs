using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayPromptConfigurationTests
{
    [Fact]
    public void ChannelRuntimeConfiguration_UsesProviderNeutralRelayGuidance()
    {
        var section = NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(options: null);

        section.Should().Contain("Aevatar's Nyx relay callback URL");
        section.Should().Contain("channel-management tools");
        section.Should().Contain("requested platform");
        section.Should().Contain("provider webhook details");
        section.Should().Contain("current turn");
        section.Should().Contain("same trusted entry");
        section.Should().NotContain("Lark");
        section.Should().NotContain("lark");
        section.Should().NotContain("api-lark-bot");
        section.Should().NotContain("lark_messages_");
    }

    [Fact]
    public void ChannelRuntimeConfiguration_DoesNotNameConcreteProviderTools()
    {
        var section = NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(options: null);

        section.Should().NotContain("lark_messages_send");
        section.Should().NotContain("lark_approvals_act");
        section.Should().NotContain("channel_registrations action=register_channel_via_nyx");
        section.Should().NotContain("nyxid_channel_bots action=show");
    }

    [Fact]
    public void ChannelRuntimeConfiguration_DoesNotHardcodeASpecificSkillNameOrProviderProvisioning()
    {
        // CLAUDE.md: production prompts/routing must not be aware of specific skill names;
        // provider-specific provisioning belongs to provider/tool boundaries.
        var section = NyxIdRelayPromptConfiguration.BuildChannelRuntimeConfigurationSection(options: null);

        section.Should().NotContain("lark-im-ops");
        section.Should().NotContain("lark-calendar-ops");
        section.Should().NotContain("lark-sheets-ops");
        section.Should().NotContain("lark-docx-ops");
        section.Should().NotContain("verification_token");
        section.Should().NotContain("developer console");
        section.Should().NotContain("/open-apis/");
    }
}
