using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Platform.Lark;
using Aevatar.GAgents.Platform.Telegram;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayCapabilityProfilesTests
{
    [Theory]
    [InlineData("matrix")]
    [InlineData("slack")]
    [InlineData("unknown-chat-platform")]
    public void Resolve_OrdinaryChatWithoutExplicitProfile_UsesRelayLocalDefault(string platform)
    {
        var capabilities = NyxRelayCapabilityProfiles.Resolve(platform);

        capabilities.MaxMessageLength.Should().Be(2000);
        capabilities.SupportsEdit.Should().BeFalse();
        capabilities.ReplyMessageMultiplicity.Should().Be(ReplyMessageMultiplicity.Multiple);
    }

    [Theory]
    [InlineData("telegram", 4096, false, ReplyMessageMultiplicity.Multiple)]
    [InlineData(" TELEGRAM ", 4096, false, ReplyMessageMultiplicity.Multiple)]
    [InlineData("lark", 30000, true, ReplyMessageMultiplicity.Multiple)]
    [InlineData("feishu", 30000, true, ReplyMessageMultiplicity.Multiple)]
    [InlineData("aurinko", 262144, false, ReplyMessageMultiplicity.Single)]
    [InlineData("device", 0, false, ReplyMessageMultiplicity.None)]
    public void Resolve_ExplicitProfile_OverridesAllRelayDefaultControlFacts(
        string platform,
        int maximumLength,
        bool supportsEdit,
        ReplyMessageMultiplicity multiplicity)
    {
        var capabilities = NyxRelayCapabilityProfiles.Resolve(platform);

        capabilities.MaxMessageLength.Should().Be(maximumLength);
        capabilities.SupportsEdit.Should().Be(supportsEdit);
        capabilities.ReplyMessageMultiplicity.Should().Be(multiplicity);
    }

    [Fact]
    public void Resolve_ReturnsIndependentProfilesForEachLifecycle()
    {
        var first = NyxRelayCapabilityProfiles.Resolve("telegram");
        first.MaxMessageLength = 1;
        first.SupportsEdit = true;
        first.ReplyMessageMultiplicity = ReplyMessageMultiplicity.Single;

        var next = NyxRelayCapabilityProfiles.Resolve("telegram");

        next.MaxMessageLength.Should().Be(4096);
        next.SupportsEdit.Should().BeFalse();
        next.ReplyMessageMultiplicity.Should().Be(ReplyMessageMultiplicity.Multiple);
    }

    [Fact]
    public void Resolve_TelegramRelay_DoesNotChangeNativeTelegramCapabilities()
    {
        var nativeBefore = TelegramMessageComposer.DefaultCapabilities.Clone();

        var relay = NyxRelayCapabilityProfiles.Resolve("telegram");

        relay.SupportsEdit.Should().BeFalse();
        TelegramMessageComposer.DefaultCapabilities.Should().Be(nativeBefore);
        TelegramMessageComposer.DefaultCapabilities.MaxMessageLength.Should().Be(4096);
        TelegramMessageComposer.DefaultCapabilities.ReplyMessageMultiplicity.Should()
            .Be(ReplyMessageMultiplicity.Unspecified);
    }

    [Theory]
    [InlineData("lark")]
    [InlineData("feishu")]
    public void Resolve_LarkFamily_PreservesExistingEditSupportAndLimit(string platform)
    {
        var relay = NyxRelayCapabilityProfiles.Resolve(platform);

        relay.SupportsEdit.Should().Be(LarkMessageComposer.DefaultCapabilities.SupportsEdit);
        relay.MaxMessageLength.Should().Be(LarkMessageComposer.DefaultCapabilities.MaxMessageLength);
    }
}
