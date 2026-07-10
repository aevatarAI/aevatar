using FluentAssertions;
using Google.Protobuf;
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
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("nyx_reply_credential_ref").Should().BeNull();
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("last_inbound_at_utc")!.FieldNumber.Should().Be(14);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("workflow_result_delivery_credential")!.FieldNumber.Should().Be(15);
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
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("nyx_reply_credential_ref").Should().BeNull();
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("workflow_result_delivery_credential")!.FieldNumber.Should().Be(11);
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
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("nyx_reply_credential_ref").Should().BeNull();
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("last_inbound_at_utc")!.FieldNumber.Should().Be(15);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("workflow_result_delivery_credential")!.FieldNumber.Should().Be(16);
    }

    [Fact]
    public void ChannelInboundEvent_ShouldReserveRuntimeCredentialCarrier()
    {
        // Refactor (v1/issue1466-first):
        //   Old: registration_token was a typed durable field on ChannelInboundEvent.
        //   New: field 9 and registration_token are unavailable on the descriptor.
        //   Principle: inbound protobuf facts must not carry runtime credentials.
        ChannelInboundEvent.Descriptor.FindFieldByName("registration_token").Should().BeNull();
        ChannelInboundEvent.Descriptor.Fields.InFieldNumberOrder()
            .Should().NotContain(field => field.FieldNumber == 9);
    }

    [Fact]
    public void ChannelInboundEvent_ShouldSerializeCredentialFreeDurableFacts()
    {
        // Refactor (v1/issue1466-first):
        //   Old: durable inbound payloads could persist bearer-like registration_token bytes.
        //   New: a complete inbound event serializes only stable channel/routing facts.
        //   Principle: runtime tokens stay outside the durable channel inbound fact model.
        var inboundEvent = new ChannelInboundEvent
        {
            Text = "hello",
            SenderId = "ou_user_1",
            SenderName = "User One",
            ConversationId = "oc_group_chat_1",
            MessageId = "msg-1",
            ChatType = "group",
            Platform = "lark",
            RegistrationId = "reg-1",
            RegistrationScopeId = "scope-1",
            NyxProviderSlug = "provider-1",
        };
        inboundEvent.Extra["event_id"] = "evt-1";

        var serialized = inboundEvent.ToByteArray();

        serialized.Should().NotContain((byte)0x4a);
        ChannelInboundEvent.Parser.ParseFrom(serialized).Should().BeEquivalentTo(inboundEvent);
        ChannelInboundEvent.Descriptor.FindFieldByName("registration_token").Should().BeNull();
    }
}
