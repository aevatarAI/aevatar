using System.Linq;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelAbstractionsProtoTests
{
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
                ValidatedScopeId = "scope-1",
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
        parsed.Content.Actions[0].Kind.ShouldBe(ActionElementKind.Button);
        parsed.Conversation.Scope.ShouldBe(ConversationScope.Thread);
        parsed.OutboundDelivery.ReplyMessageId.ShouldBe("relay-msg-1");
        parsed.TransportExtras.NyxAgentApiKeyId.ShouldBe("nyx-key-1");
        parsed.TransportExtras.NyxPlatformMessageId.ShouldBe("om_123");
        parsed.TransportExtras.ValidatedScopeId.ShouldBe("scope-1");
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ChatActivity));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(MessageContent));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(CardActionSubmission));
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
}
