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
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("default_skill_name")!.FieldNumber.Should().Be(16);
        ChannelBotRegistrationEntry.Descriptor.FindFieldByName("workflow_result_delivery_repair")!.FieldNumber.Should().Be(17);
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
        ChannelBotRegisterCommand.Descriptor.FindFieldByName("default_skill_name")!.FieldNumber.Should().Be(12);
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
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("default_skill_name")!.FieldNumber.Should().Be(17);
        ChannelBotRegistrationDocument.Descriptor.FindFieldByName("workflow_result_delivery_repair")!.FieldNumber.Should().Be(18);
    }

    [Fact]
    public void WorkflowResultDeliveryRepairContracts_ShouldUseStableFieldNumbers()
    {
        AssertEnum(
            "ChannelWorkflowResultDeliveryCapabilityStatus",
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_CAPABILITY_STATUS_UNSPECIFIED", 0),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_CAPABILITY_STATUS_ENABLED", 1),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_CAPABILITY_STATUS_REPAIR_REQUIRED", 2),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_CAPABILITY_STATUS_REPAIRING", 3),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_CAPABILITY_STATUS_REPAIR_FAILED", 4));

        AssertEnum(
            "ChannelWorkflowResultDeliveryRepairStatus",
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_STATUS_UNSPECIFIED", 0),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_STATUS_REQUESTED", 1),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_STATUS_CREDENTIAL_PREPARED", 2),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_STATUS_FAILED", 3));

        AssertEnum(
            "ChannelWorkflowResultDeliveryRepairPhase",
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_UNSPECIFIED", 0),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_REQUEST_ADMISSION", 1),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_ROTATED_KEY_RECOVERY", 2),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_API_KEY_ROTATION", 3),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_VAULT_STORAGE", 4),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_CREDENTIAL_PREPARATION", 5),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_ROUTE_REBINDING", 6),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_PHASE_ACTOR_COMPLETION", 7));

        AssertEnum(
            "ChannelWorkflowResultDeliveryRepairFailureReason",
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_UNSPECIFIED", 0),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_REGISTRATION_NOT_FOUND", 1),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_UNAUTHORIZED_OWNER", 2),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_UNSUPPORTED_PLATFORM", 3),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_ALREADY_ENABLED", 4),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_INVALID_REQUEST", 5),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_REQUEST_CONFLICT", 6),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_STALE_ACTIVE_KEY", 7),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_ROTATION_FAILED", 8),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_VAULT_STORAGE_FAILED", 9),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_ROUTE_UPDATE_FAILED", 10),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_COMPLETION_FAILED", 11),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_AMBIGUOUS_ROTATED_KEY_RECOVERY", 12),
                ("CHANNEL_WORKFLOW_RESULT_DELIVERY_REPAIR_FAILURE_REASON_OBSERVATION_UNAVAILABLE", 13));

        AssertFields<ChannelWorkflowResultDeliveryRepairState>(
            ("request_id", 1),
            ("status", 2),
            ("expected_api_key_id", 3),
            ("expected_conversation_route_id", 4),
            ("rotated_api_key_id", 5),
            ("prepared_secret_reference", 6),
            ("failure_phase", 7),
            ("failure_reason", 8),
            ("requested_by_subject_id", 9),
            ("requested_at_unix_ms", 10),
            ("updated_at_unix_ms", 11));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairRequestCommand>(
            ("registration_id", 1),
            ("request_id", 2),
            ("expected_api_key_id", 3),
            ("expected_conversation_route_id", 4),
            ("requested_by_subject_id", 5),
            ("requested_at_unix_ms", 6));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairPrepareCommand>(
            ("registration_id", 1),
            ("request_id", 2),
            ("expected_api_key_id", 3),
            ("rotated_api_key_id", 4),
            ("prepared_secret_reference", 5),
            ("updated_at_unix_ms", 6));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairCompleteCommand>(
            ("registration_id", 1),
            ("request_id", 2),
            ("expected_api_key_id", 3),
            ("rotated_api_key_id", 4),
            ("prepared_secret_reference", 5),
            ("updated_at_unix_ms", 6));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairFailCommand>(
            ("registration_id", 1),
            ("request_id", 2),
            ("expected_api_key_id", 3),
            ("rotated_api_key_id", 4),
            ("prepared_secret_reference", 5),
            ("failure_phase", 6),
            ("failure_reason", 7),
            ("updated_at_unix_ms", 8));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairRequestedEvent>(
            ("registration_id", 1),
            ("repair", 2));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairPreparedEvent>(
            ("registration_id", 1),
            ("repair", 2));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairCompletedEvent>(
            ("registration_id", 1),
            ("request_id", 2),
            ("expected_api_key_id", 3),
            ("rotated_api_key_id", 4),
            ("prepared_secret_reference", 5),
            ("completed_at_unix_ms", 6));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairFailedEvent>(
            ("registration_id", 1),
            ("repair", 2));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairRejectedEvent>(
            ("registration_id", 1),
            ("request_id", 2),
            ("phase", 3),
            ("reason", 4),
            ("rejected_at_unix_ms", 5));
        AssertFields<ChannelBotWorkflowResultDeliveryRepairOutcome>(
            ("requested", 1),
            ("prepared", 2),
            ("completed", 3),
            ("failed", 4),
            ("rejected", 5));
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

    private static void AssertFields<T>(params (string Name, int Number)[] expected)
        where T : IMessage<T>, new() =>
        new T().Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => (field.Name, field.FieldNumber))
            .Should().Equal(expected);

    private static void AssertEnum(string name, params (string Name, int Number)[] expected) =>
        ChannelBotRegistrationReflection.Descriptor.EnumTypes
            .Single(descriptor => string.Equals(descriptor.Name, name, StringComparison.Ordinal))
            .Values
            .Select(static value => (value.Name, value.Number))
            .Should().Equal(expected);
}
