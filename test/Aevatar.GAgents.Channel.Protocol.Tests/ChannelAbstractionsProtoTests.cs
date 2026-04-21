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
            },
            ReplyToActivityId = "orig-1",
            RawPayloadBlobRef = "blob://payload/1",
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
        parsed.Content.Actions[0].Kind.ShouldBe(ActionElementKind.Button);
        parsed.Conversation.Scope.ShouldBe(ConversationScope.Thread);
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ChatActivity));
        ChatActivityReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(MessageContent));
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
            },
        };
        context.Annotations["surface"] = "modal";

        emitResult.Clone().ShouldBe(emitResult);
        context.Clone().ShouldBe(context);
        context.Annotations["surface"].ShouldBe("modal");
        context.Capabilities.Streaming.ShouldBe(StreamingSupport.Native);
        ChannelContractsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(EmitResult));
        ChannelContractsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(StreamChunk));
        ScheduleReflection.Descriptor.EnumTypes.Select(x => x.Name)
            .ShouldContain(nameof(ProjectionVerdict));
    }
}
