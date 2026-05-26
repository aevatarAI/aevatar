using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelBotRegistrationProtoCompatibilityTests
{
    [Fact]
    public void ChannelBotRegistrationEntry_ShouldUseCompactFieldNumbers()
    {
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("id")!.FieldNumber.Should().Be(1);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("platform")!.FieldNumber.Should().Be(2);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_provider_slug")!.FieldNumber.Should().Be(3);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("scope_id")!.FieldNumber.Should().Be(4);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("created_at")!.FieldNumber.Should().Be(5);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("webhook_url")!.FieldNumber.Should().Be(6);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("tombstoned")!.FieldNumber.Should().Be(7);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("tombstone_state_version")!.FieldNumber.Should().Be(8);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_channel_bot_id")!.FieldNumber.Should().Be(9);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_agent_api_key_id")!.FieldNumber.Should().Be(10);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_conversation_route_id")!.FieldNumber.Should().Be(11);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("credential_ref").Should().BeNull();
    }

    [Fact]
    public void ChannelBotRegisterCommand_ShouldUseCompactFieldNumbers()
    {
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("platform")!.FieldNumber.Should().Be(1);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_provider_slug")!.FieldNumber.Should().Be(2);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("scope_id")!.FieldNumber.Should().Be(3);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("webhook_url")!.FieldNumber.Should().Be(4);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("requested_id")!.FieldNumber.Should().Be(5);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_channel_bot_id")!.FieldNumber.Should().Be(6);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_agent_api_key_id")!.FieldNumber.Should().Be(7);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_conversation_route_id")!.FieldNumber.Should().Be(8);
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("credential_ref").Should().BeNull();
    }

    [Fact]
    public void ChannelBotRegistrationDocument_ShouldUseCompactFieldNumbers()
    {
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("id")!.FieldNumber.Should().Be(1);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("platform")!.FieldNumber.Should().Be(2);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_provider_slug")!.FieldNumber.Should().Be(3);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("scope_id")!.FieldNumber.Should().Be(4);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("webhook_url")!.FieldNumber.Should().Be(5);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("state_version")!.FieldNumber.Should().Be(6);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("last_event_id")!.FieldNumber.Should().Be(7);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("updated_at_utc")!.FieldNumber.Should().Be(8);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("actor_id")!.FieldNumber.Should().Be(9);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_channel_bot_id")!.FieldNumber.Should().Be(10);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_agent_api_key_id")!.FieldNumber.Should().Be(11);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_conversation_route_id")!.FieldNumber.Should().Be(12);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("credential_ref").Should().BeNull();
    }
}
