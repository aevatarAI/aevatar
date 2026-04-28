using System.Linq;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Aevatar.GAgents.Channel.Protocol.Tests;

public sealed class ChannelRuntimeProtoTests
{
    [Fact]
    public void ConversationContinueFailedEvent_ShouldPreserveRetryPolicyOneof()
    {
        var failed = new ConversationContinueFailedEvent
        {
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            Kind = FailureKind.TransientAdapterError,
            ErrorCode = "rate_limited",
            ErrorSummary = "retry later",
            RetryAfterMs = 500,
            FailedAtUnixMs = 42,
        };

        var parsed = ConversationContinueFailedEvent.Parser.ParseFrom(failed.ToByteArray());

        parsed.ShouldBe(failed);
        parsed.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.RetryAfterMs);

        failed.NotRetryable = new Empty();
        failed.RetryPolicyCase.ShouldBe(ConversationContinueFailedEvent.RetryPolicyOneofCase.NotRetryable);
    }

    [Fact]
    public void LeaseToken_ShouldKeepOwnerBytesAtFieldOneAndExpiryAtFieldTwo()
    {
        var ownerField = LeaseToken.Descriptor.FindFieldByNumber(1);
        var expiresField = LeaseToken.Descriptor.FindFieldByNumber(2);

        ownerField.ShouldNotBeNull();
        ownerField.Name.ShouldBe("owner");
        ownerField.FieldType.ShouldBe(FieldType.Bytes);
        expiresField.ShouldNotBeNull();
        expiresField.Name.ShouldBe("expires_at_unix_ms");
        expiresField.FieldType.ShouldBe(FieldType.Int64);
    }

    [Fact]
    public void RuntimeReflections_ShouldExposeChannelRuntimeSchemaMessages()
    {
        var completed = new ConversationTurnCompletedEvent
        {
            ProcessedActivityId = "activity-1",
            CausationCommandId = "cmd-1",
            SentActivityId = "sent-1",
            AuthPrincipal = "bot",
            Conversation = new ConversationReference
            {
                Channel = new ChannelId { Value = "slack" },
                Bot = new BotInstanceId { Value = "ops-bot" },
                Scope = ConversationScope.Channel,
                CanonicalKey = "slack:team:channel",
            },
            Outbound = new MessageContent
            {
                Text = "done",
                Disposition = MessageDisposition.Normal,
            },
            CompletedAtUnixMs = 123,
            OutboundDelivery = new OutboundDeliveryReceipt
            {
                ReplyMessageId = "relay-msg-1",
            },
        };
        completed.Clone().ShouldBe(completed);
        completed.OutboundDelivery.ReplyMessageId.ShouldBe("relay-msg-1");
        var llmRequested = new NeedsLlmReplyEvent
        {
            CorrelationId = "activity-1",
            TargetActorId = "conversation:actor",
            RegistrationId = "bot-reg-1",
            Activity = new ChatActivity
            {
                Id = "activity-1",
                Conversation = completed.Conversation.Clone(),
                Content = new MessageContent { Text = "hello" },
            },
            RequestedAtUnixMs = 42,
        };
        var llmReady = new LlmReplyReadyEvent
        {
            CorrelationId = "activity-1",
            RegistrationId = "bot-reg-1",
            SourceActorId = "llm-worker-1",
            Activity = llmRequested.Activity.Clone(),
            Outbound = new MessageContent { Text = "reply" },
            TerminalState = LlmReplyTerminalState.Completed,
            ReadyAtUnixMs = 43,
        };
        ConversationEventsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ConversationTurnCompletedEvent));
        ConversationEventsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(OutboundDeliveryReceipt));
        ConversationEventsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(NeedsLlmReplyEvent));
        ConversationEventsReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(LlmReplyReadyEvent));
        ChannelBotRegistrationReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(ChannelBotRegistrationEntry));
        UserAgentCatalogReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(UserAgentCatalogEntry));
        SessionStoreReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(LeaseToken));
        InteractionJournalReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(PreAckJournalEntry));
        PayloadQuarantineReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(PlatformQuarantineEnvelope));
        llmRequested.Clone().ShouldBe(llmRequested);
        llmReady.Clone().ShouldBe(llmReady);
    }
}
