using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf.Collections;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationBotMessageLedgerTests
{
    [Fact]
    public void RecordBotSentMessageId_ShouldAppendBareMessageId()
    {
        var ledger = new RepeatedField<string>();

        ConversationBotMessageLedger.RecordBotSentMessageId(ledger, "om_bot_1");

        ledger.Should().ContainSingle().Which.Should().Be("om_bot_1");
    }

    [Fact]
    public void RecordBotSentMessageId_ShouldIgnoreEmptyAndDuplicate()
    {
        var ledger = new RepeatedField<string>();

        ConversationBotMessageLedger.RecordBotSentMessageId(ledger, "om_bot_1");
        ConversationBotMessageLedger.RecordBotSentMessageId(ledger, "om_bot_1");
        ConversationBotMessageLedger.RecordBotSentMessageId(ledger, "   ");
        ConversationBotMessageLedger.RecordBotSentMessageId(ledger, null);

        ledger.Should().ContainSingle().Which.Should().Be("om_bot_1");
    }

    [Fact]
    public void RecordBotSentMessageId_ShouldCapAndEvictOldest()
    {
        var ledger = new RepeatedField<string>();

        for (var i = 0; i < ConversationBotMessageLedger.MaxTrackedBotMessageIds + 10; i++)
            ConversationBotMessageLedger.RecordBotSentMessageId(ledger, $"om_{i}");

        ledger.Count.Should().Be(ConversationBotMessageLedger.MaxTrackedBotMessageIds);
        ledger.Should().NotContain("om_0");
        ledger.Should().Contain($"om_{ConversationBotMessageLedger.MaxTrackedBotMessageIds + 9}");
    }

    [Fact]
    public void IsReplyToBotMessage_ShouldMatchTrackedParentId()
    {
        var ledger = new RepeatedField<string> { "om_bot_1", "om_bot_2" };

        ConversationBotMessageLedger.IsReplyToBotMessage(ledger, "om_bot_2").Should().BeTrue();
        ConversationBotMessageLedger.IsReplyToBotMessage(ledger, "om_other").Should().BeFalse();
        ConversationBotMessageLedger.IsReplyToBotMessage(ledger, "").Should().BeFalse();
        ConversationBotMessageLedger.IsReplyToBotMessage(ledger, null).Should().BeFalse();
    }
}
