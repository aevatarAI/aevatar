using System.Linq;
using Aevatar.Foundation.Abstractions.Interactions;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelAbstractionsProtoTests
{
    [Fact]
    public void EmitResult_ShouldRoundtripTypedFailureDiagnostics()
    {
        var result = EmitResult.Failed(
            "relay_reply_update_rejected",
            "rate limited",
            TimeSpan.FromSeconds(4),
            ComposeCapability.Unsupported,
            FailureKind.TransientAdapterError,
            httpStatus: 429,
            rawErrorKey: "rate_limited",
            rawErrorCode: 1005);

        var parsed = EmitResult.Parser.ParseFrom(result.ToByteArray());

        parsed.ShouldBe(result);
        parsed.FailureKind.ShouldBe(FailureKind.TransientAdapterError);
        parsed.RetryAfter.ToTimeSpan().ShouldBe(TimeSpan.FromSeconds(4));
        parsed.HttpStatus.ShouldBe(429);
        parsed.RawErrorKey.ShouldBe("rate_limited");
        parsed.RawErrorCode.ShouldBe(1005);
    }

    [Fact]
    public void ChatActivity_ShouldRoundtripWithNestedContracts()
    {
        var activity = new ChatActivity
        {
            Id = "slack:evt:1",
            Type = ActivityType.Message,
            ChannelId = new ChannelId { Value = "slack" },
            Bot = new BotInstanceId { Value = "ops-bot" },
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "slack" },
                Bot = new BotInstanceId { Value = "ops-bot" },
                Partition = "team-1",
                Scope = ConversationScope.Thread,
                CanonicalKey = "slack:team-1:C123:thread:1710000.123",
            },
            From = new ParticipantRef
            {
                CanonicalId = "U123",
                DisplayName = "Casey",
            },
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Content = new MessageContent
            {
                Text = "hello",
                Disposition = MessageDisposition.Ephemeral,
                CardAction = new CardActionSubmission
                {
                    ActionId = "approve",
                    SubmittedValue = "true",
                    SourceMessageId = "om_123",
                    ActionKind = ActionElementKind.FormSubmit,
                    WorkflowResume = new WorkflowResumeActionPayload
                    {
                        ActorId = "workflow-actor-1",
                        RunId = "run-1",
                        StepId = "tool-step",
                        ToolApproval = new WorkflowToolApprovalResumeActionPayload
                        {
                            ExecutionId = "exec-1",
                            ToolCallId = "tool-call-1",
                            ApprovalRequestId = "approval-1",
                        },
                    },
                    NyxIdApproval = new NyxIdApprovalActionPayload
                    {
                        RequestId = "nyx-approval-1",
                        Approved = true,
                    },
                },
            },
            ReplyToActivityId = "orig-1",
            RawPayloadBlobRef = "blob://payload/1",
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = "relay-msg-1",
                CorrelationId = "corr-relay-1",
            },
            TransportExtras = new TransportExtras
            {
                NyxMessageId = "nyx-msg-1",
                NyxAgentApiKeyId = "nyx-key-1",
                NyxPlatform = "lark",
                NyxConversationId = "nyx-conv-1",
                NyxPlatformMessageId = "om_123",
            },
        };
        activity.Mentions.Add(new ParticipantRef
        {
            CanonicalId = "U999",
            DisplayName = "Taylor",
        });
        activity.Content.Attachments.Add(new AttachmentRef
        {
            AttachmentId = "att-1",
            Kind = AttachmentKind.Image,
            Name = "diagram.png",
            ContentType = "image/png",
            BlobRef = "blob://attachment/1",
            SizeBytes = 1234,
            ExternalUrl = "https://example.test/diagram.png",
        });
        activity.Content.Actions.Add(new ActionElement
        {
            Kind = ActionElementKind.Button,
            ActionId = "ack",
            Label = "Ack",
            Value = "ack",
            IsPrimary = true,
            NyxIdApproval = new NyxIdApprovalActionPayload
            {
                RequestId = "nyx-approval-1",
                Approved = false,
            },
        });
        activity.Content.Cards.Add(new CardBlock
        {
            Kind = CardBlockKind.Section,
            BlockId = "summary",
            Title = "Summary",
            Text = "Primary content",
        });

        var parsed = ChatActivity.Parser.ParseFrom(activity.ToByteArray());

        parsed.ShouldBe(activity);
        parsed.Content.CardAction.ActionId.ShouldBe("approve");
        parsed.Content.CardAction.ActionKind.ShouldBe(ActionElementKind.FormSubmit);
        parsed.Content.CardAction.WorkflowResume.ToolApproval.ExecutionId.ShouldBe("exec-1");
        parsed.Content.CardAction.WorkflowResume.ToolApproval.ToolCallId.ShouldBe("tool-call-1");
        parsed.Content.CardAction.WorkflowResume.ToolApproval.ApprovalRequestId.ShouldBe("approval-1");
        parsed.Content.CardAction.NyxIdApproval.RequestId.ShouldBe("nyx-approval-1");
        parsed.Content.CardAction.NyxIdApproval.Approved.ShouldBeTrue();
        parsed.Content.Actions[0].Kind.ShouldBe(ActionElementKind.Button);
        parsed.Content.Actions[0].NyxIdApproval.Approved.ShouldBeFalse();
        parsed.Conversation.Scope.ShouldBe(ConversationScope.Thread);
        parsed.OutboundDelivery.ReplyMessageId.ShouldBe("relay-msg-1");
        parsed.TransportExtras.NyxAgentApiKeyId.ShouldBe("nyx-key-1");
        parsed.TransportExtras.NyxPlatformMessageId.ShouldBe("om_123");
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ChatActivity));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(MessageContent));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(CardActionSubmission));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(NyxIdApprovalActionPayload));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(WorkflowToolApprovalResumeActionPayload));
        WorkflowResumeActionPayload.Descriptor.FindFieldByName("tool_approval")!.FieldNumber.ShouldBe(8);
        ActionElement.Descriptor.FindFieldByName("nyx_id_approval")!.FieldNumber.ShouldBe(13);
        CardActionSubmission.Descriptor.FindFieldByName("nyx_id_approval")!.FieldNumber.ShouldBe(9);
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(OutboundDeliveryContext));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(TransportExtras));
    }

    [Fact]
    public void ChannelContracts_ShouldExposeCapabilitiesAndStreamingDescriptor()
    {
        var emitResult = new EmitResult
        {
            Success = false,
            SentActivityId = "msg-1",
            Capability = ComposeCapability.Degraded,
            RetryAfter = Duration.FromTimeSpan(TimeSpan.FromSeconds(3)),
            ErrorCode = "rate_limited",
            ErrorMessage = "retry later",
        };
        var context = new ComposeContext
        {
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "discord" },
                Bot = new BotInstanceId { Value = "helper" },
                Scope = ConversationScope.Channel,
                CanonicalKey = "discord:guild-1:channel-1",
            },
            Capabilities = new ChannelCapabilities
            {
                SupportsModal = true,
                SupportsTyping = true,
                Streaming = StreamingSupport.Native,
                RecommendedStreamDebounceMs = 200,
                Transport = TransportMode.Gateway,
            },
        };
        context.Annotations["surface"] = "modal";

        emitResult.Clone().ShouldBe(emitResult);
        context.Clone().ShouldBe(context);
        context.Annotations["surface"].ShouldBe("modal");
        context.Capabilities.Streaming.ShouldBe(StreamingSupport.Native);
        context.Capabilities.Transport.ShouldBe(TransportMode.Gateway);
        ChannelContractsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(EmitResult));
        ChannelContractsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(StreamChunk));
        var binding = new ChannelTransportBinding
        {
            Bot = new ChannelBotDescriptor
            {
                RegistrationId = "bot-reg-1",
                Bot = new BotInstanceId { Value = "helper" },
                Channel = new ChannelId { Value = "discord" },
                ScopeId = "scope-1",
            },
            VerificationToken = "verify-me",
        };
        binding.Clone().ShouldBe(binding);
        binding.Bot.ScopeId.ShouldBe("scope-1");
        ChannelTransportBinding.Descriptor.FindFieldByName("credential_ref").ShouldBeNull();
        OutboundDeliveryContext.Descriptor.FindFieldByName("reply_access_token").ShouldBeNull();
        OutboundDeliveryContext.Descriptor.FindFieldByName("correlation_id")!.FieldNumber.ShouldBe(3);
        var channelCapabilities = ChannelContractsReflection.Descriptor.MessageTypes
            .Single(x => x.Name == nameof(ChannelCapabilities));
        channelCapabilities.FindFieldByName("transport").FieldNumber.ShouldBe(17);
        ChannelContractsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ChannelBotDescriptor));
        ChannelContractsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ChannelTransportBinding));
        ScheduleReflection.Descriptor.EnumTypes.Select(x => x.Name)
            .ShouldContain(nameof(ProjectionVerdict));
    }

    [Fact]
    public void DeliveryLedgerContracts_ShouldExposeStableFieldNumbers()
    {
        DeliveryKind.TextMessage.ShouldBe((DeliveryKind)1);
        DeliveryKind.StreamingCard.ShouldBe((DeliveryKind)2);
        DeliveryKind.InteractiveCard.ShouldBe((DeliveryKind)3);
        DeliveryKind.FailureNotification.ShouldBe((DeliveryKind)4);
        DeliveryStatus.Succeeded.ShouldBe((DeliveryStatus)1);
        DeliveryStatus.FailedPreSend.ShouldBe((DeliveryStatus)2);
        DeliveryStatus.FailedPostSend.ShouldBe((DeliveryStatus)3);

        AssertField<DeliveryTarget>("channel", 1, FieldType.Message);
        AssertField<DeliveryTarget>("conversation_key", 2, FieldType.String);
        AssertField<DeliveryTarget>("platform", 3, FieldType.String);
        AssertField<DeliveryTarget>("address_id", 4, FieldType.String);
        AssertField<DeliveryTarget>("address_type", 5, FieldType.String);
        AssertField<DeliveryTarget>("conversation_id", 6, FieldType.String);
        AssertField<DeliveryTarget>("reply_message_id", 7, FieldType.String);

        AssertField<DeliveryProducedEvent>("run_id", 1, FieldType.String);
        AssertField<DeliveryProducedEvent>("turn_id", 2, FieldType.String);
        AssertField<DeliveryProducedEvent>("delivery_kind", 3, FieldType.Enum);
        AssertField<DeliveryProducedEvent>("target", 4, FieldType.Message);
        AssertField<DeliveryProducedEvent>("status", 5, FieldType.Enum);
        AssertField<DeliveryProducedEvent>("provider_message_id", 6, FieldType.String);
        AssertField<DeliveryProducedEvent>("card_id", 7, FieldType.String);
        AssertField<DeliveryProducedEvent>("request_id", 8, FieldType.String);
        AssertField<DeliveryProducedEvent>("source_event_id", 9, FieldType.String);
        AssertField<DeliveryProducedEvent>("produced_at_version", 10, FieldType.Int64);

        AssertField<DeliveryLedgerEntry>("delivery_kind", 1, FieldType.Enum);
        AssertField<DeliveryLedgerEntry>("status", 2, FieldType.Enum);
        AssertField<DeliveryLedgerEntry>("target", 3, FieldType.Message);
        AssertField<DeliveryLedgerEntry>("provider_message_id", 4, FieldType.String);
        AssertField<DeliveryLedgerEntry>("card_id", 5, FieldType.String);
        AssertField<DeliveryLedgerEntry>("request_id", 6, FieldType.String);
        AssertField<DeliveryLedgerEntry>("source_event_id", 7, FieldType.String);
        AssertField<DeliveryLedgerEntry>("produced_at_version", 8, FieldType.Int64);
    }

    [Fact]
    public void DeliveryProducedEvent_ShouldRoundTripTypedLedgerFields()
    {
        var produced = new DeliveryProducedEvent
        {
            RunId = "run-1",
            TurnId = "turn-1",
            DeliveryKind = DeliveryKind.InteractiveCard,
            Target = new DeliveryTarget
            {
                Channel = ChannelId.From("lark"),
                ConversationKey = "lark:tenant:thread",
                Platform = "lark",
                AddressId = "oc_1",
                AddressType = "chat_id",
                ConversationId = "conv-1",
                ReplyMessageId = "reply-1",
            },
            Status = DeliveryStatus.FailedPostSend,
            ProviderMessageId = "om_1",
            CardId = "card-1",
            RequestId = "request-1",
            SourceEventId = "chunk-1",
            ProducedAtVersion = 42,
        };

        var parsed = DeliveryProducedEvent.Parser.ParseFrom(produced.ToByteArray());

        parsed.ShouldBe(produced);
        parsed.Target.Channel.Value.ShouldBe("lark");
        parsed.Status.ShouldBe(DeliveryStatus.FailedPostSend);
    }

    [Fact]
    public void DeliveryLedgerEntry_ShouldRoundTripReadModelRow()
    {
        var entry = new DeliveryLedgerEntry
        {
            DeliveryKind = DeliveryKind.TextMessage,
            Status = DeliveryStatus.Succeeded,
            Target = new DeliveryTarget
            {
                Channel = ChannelId.From("nyxid"),
                ConversationKey = "nyxid:user",
            },
            ProviderMessageId = "om_2",
            CardId = "card-2",
            RequestId = "request-2",
            SourceEventId = "event-2",
            ProducedAtVersion = 7,
        };

        var parsed = DeliveryLedgerEntry.Parser.ParseFrom(entry.ToByteArray());

        parsed.ShouldBe(entry);
        parsed.Target.Channel.Value.ShouldBe("nyxid");
    }

    [Fact]
    public void InteractionSpecMapper_ShouldProjectTypedSpecToMessageContent()
    {
        var spec = new InteractionSpec
        {
            Title = "Review",
            Body = "Deploy v1?",
            Disposition = InteractionDisposition.Ephemeral,
        };
        spec.Actions.Add(new InteractionAction
        {
            Kind = InteractionActionKind.Select,
            ActionId = "route",
            Label = "Route",
            Style = InteractionActionStyle.Primary,
            ApprovalDecision = InteractionApprovalDecision.Approve,
            Options =
            {
                new InteractionOption { Label = "Canary", Value = "canary" },
            },
        });
        spec.Fields.Add(new InteractionField
        {
            Title = "Env",
            Text = "prod",
            IsShort = true,
        });

        var content = InteractionSpecMapper.ToMessageContent(spec);

        content.Text.ShouldBe("Review\nDeploy v1?");
        content.Disposition.ShouldBe(MessageDisposition.Ephemeral);
        content.Actions.ShouldHaveSingleItem().Kind.ShouldBe(ActionElementKind.Select);
        content.Actions[0].WorkflowResume.Approved.ShouldBeTrue();
        content.Actions[0].Options.ShouldHaveSingleItem().Value.ShouldBe("canary");
        content.Cards.ShouldHaveSingleItem().Fields.ShouldHaveSingleItem().IsShort.ShouldBeTrue();
    }

    private static void AssertField<TMessage>(string name, int number, FieldType type)
        where TMessage : IMessage<TMessage>, new()
    {
        var descriptor = new TMessage().Descriptor;
        var field = descriptor.FindFieldByName(name);
        field.ShouldNotBeNull();
        field!.FieldNumber.ShouldBe(number);
        field.FieldType.ShouldBe(type);
    }
}
