using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationProtoCompatibilityTests
{
    [Fact]
    public void ChannelBotRegistrationEntry_ShouldKeepLegacyFieldNumbersStable()
    {
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_user_token")!.FieldNumber.Should().Be(4);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("verification_token")!.FieldNumber.Should().Be(5);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("scope_id")!.FieldNumber.Should().Be(6);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("webhook_url")!.FieldNumber.Should().Be(8);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("encrypt_key")!.FieldNumber.Should().Be(9);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("credential_ref")!.FieldNumber.Should().Be(10);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_refresh_token")!.FieldNumber.Should().Be(13);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_channel_bot_id")!.FieldNumber.Should().Be(14);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_agent_api_key_id")!.FieldNumber.Should().Be(15);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_conversation_route_id")!.FieldNumber.Should().Be(16);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("legacy_direct_binding")!.FieldNumber.Should().Be(17);
    }

    [Fact]
    public void ChannelBotCommandAndEventContracts_ShouldKeepLegacyTokenFieldNumbersStable()
    {
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_user_token")!.FieldNumber.Should().Be(3);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("verification_token")!.FieldNumber.Should().Be(4);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("scope_id")!.FieldNumber.Should().Be(5);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("webhook_url")!.FieldNumber.Should().Be(6);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("encrypt_key")!.FieldNumber.Should().Be(8);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("credential_ref")!.FieldNumber.Should().Be(9);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_refresh_token")!.FieldNumber.Should().Be(10);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_channel_bot_id")!.FieldNumber.Should().Be(11);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_agent_api_key_id")!.FieldNumber.Should().Be(12);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_conversation_route_id")!.FieldNumber.Should().Be(13);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("legacy_direct_binding")!.FieldNumber.Should().Be(14);

        ChannelBotUpdateTokenCommand.Descriptor.FindFieldByName("nyx_user_token")!.FieldNumber.Should().Be(2);
        ChannelBotUpdateTokenCommand.Descriptor.FindFieldByName("nyx_refresh_token")!.FieldNumber.Should().Be(3);
        ChannelBotUpdateTokenCommand.Descriptor.FindFieldByName("legacy_direct_binding")!.FieldNumber.Should().Be(4);

        ChannelBotTokenUpdatedEvent.Descriptor.FindFieldByName("nyx_user_token")!.FieldNumber.Should().Be(2);
        ChannelBotTokenUpdatedEvent.Descriptor.FindFieldByName("nyx_refresh_token")!.FieldNumber.Should().Be(3);
        ChannelBotTokenUpdatedEvent.Descriptor.FindFieldByName("legacy_direct_binding")!.FieldNumber.Should().Be(4);
    }

    [Fact]
    public void ChannelBotRegistrationDocument_ShouldKeepLegacyReadModelFieldNumbersStable()
    {
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_user_token")!.FieldNumber.Should().Be(11);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("encrypt_key")!.FieldNumber.Should().Be(12);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("credential_ref")!.FieldNumber.Should().Be(13);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_refresh_token")!.FieldNumber.Should().Be(14);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_channel_bot_id")!.FieldNumber.Should().Be(15);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_agent_api_key_id")!.FieldNumber.Should().Be(16);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_conversation_route_id")!.FieldNumber.Should().Be(17);
    }
}
