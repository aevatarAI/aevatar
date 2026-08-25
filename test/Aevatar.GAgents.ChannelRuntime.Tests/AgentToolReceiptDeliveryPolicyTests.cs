using Aevatar.AI.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentToolReceiptDeliveryPolicyTests
{
    [Theory]
    [InlineData(AgentToolReceiptStatus.Error, "Failed")]
    [InlineData(AgentToolReceiptStatus.ApprovalRequired, "Approval pending")]
    [InlineData(AgentToolReceiptStatus.Denied, "Denied")]
    [InlineData(AgentToolReceiptStatus.AuthorizationRequired, "Authorization required")]
    [InlineData(AgentToolReceiptStatus.Unspecified, "Outcome unverified")]
    public void Build_WhenMutatingReceiptIsUnresolved_ShouldReplaceModelNarrative(
        AgentToolReceiptStatus status,
        string expectedStatus)
    {
        var outbound = new MessageContent
        {
            Text = "Submission confirmed",
            Actions = { new ActionElement { ActionId = "open", Label = "Open" } },
        };
        var history = new[]
        {
            new ConversationHistoryEntry { Role = "assistant", Content = "Submission confirmed" },
        };
        var receipt = Receipt("call-1", status, AgentToolReceiptEffect.Mutating);
        if (status == AgentToolReceiptStatus.AuthorizationRequired)
        {
            receipt.AuthorizationRequired = new NyxIdAuthorizationRequiredEvent
            {
                SafeMessage = "Connect the approval service to continue.",
            };
        }

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Submission confirmed",
            outbound,
            history,
            [receipt],
            [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Contain($"[tool receipt] {expectedStatus}: submit_record");
        delivery.ReplyText.Should().NotContain("Submission confirmed");
        delivery.OutboundIntent.Should().NotBeNull();
        delivery.OutboundIntent!.Text.Should().Be(delivery.ReplyText);
        delivery.OutboundIntent.Actions.Should().BeEmpty();
        delivery.AppendedHistory.Should().ContainSingle();
        delivery.AppendedHistory[0].Content.Should().Be(delivery.ReplyText);
    }

    [Fact]
    public void Build_WhenReadOnlyToolFails_ShouldPreserveFallbackNarrativeAndActions()
    {
        var outbound = new MessageContent
        {
            Text = "I recovered the answer from the fallback source.",
            Actions = { new ActionElement { ActionId = "details", Label = "Details" } },
        };
        var history = new[]
        {
            new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = "I recovered the answer from the fallback source.",
            },
        };

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "I recovered the answer from the fallback source.",
            outbound,
            history,
            [Receipt("call-read", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.ReadOnly)],
            [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().StartWith("I recovered the answer from the fallback source.");
        delivery.ReplyText.Should().Contain("[tool receipt] Failed: submit_record");
        delivery.OutboundIntent!.Actions.Should().ContainSingle();
        delivery.OutboundIntent.Text.Should().Contain("[tool receipt] Failed: submit_record");
        delivery.AppendedHistory.Should().ContainSingle();
        delivery.AppendedHistory[0].Content.Should().Be(delivery.ReplyText);
    }

    [Fact]
    public void Build_WhenSameCallHasLaterSuccess_ShouldUseTerminalSuccessAndKeepNarrative()
    {
        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Submission confirmed",
            outboundIntent: null,
            appendedHistory: [],
            receipts:
            [
                Receipt("call-1", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.Mutating),
                Receipt("call-1", AgentToolReceiptStatus.Success, AgentToolReceiptEffect.Mutating),
            ],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Be("Submission confirmed");
    }

    [Fact]
    public void Build_WhenSameCallIdBelongsToDifferentTools_ShouldKeepFailedMutation()
    {
        var failed = Receipt("call-1", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.Mutating);
        var unrelatedSuccess = Receipt(
            "call-1",
            AgentToolReceiptStatus.Success,
            AgentToolReceiptEffect.Mutating,
            toolName: "probe_workflow");

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Submission confirmed",
            outboundIntent: null,
            appendedHistory: [],
            receipts: [failed, unrelatedSuccess],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Contain("[tool receipt] Failed: submit_record");
        delivery.ReplyText.Should().NotContain("Submission confirmed");
    }

    [Fact]
    public void Build_WhenBlockingCallHasAssistantToolCallNarrative_ShouldClearNarrativeAndKeepPairing()
    {
        var history = new[]
        {
            new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = "Submission confirmed",
                ToolCalls =
                {
                    new ConversationToolCallEntry
                    {
                        Id = "call-1",
                        Name = "submit_record",
                        ArgumentsJson = "{}",
                    },
                },
            },
            new ConversationHistoryEntry
            {
                Role = "tool",
                ToolCallId = "call-1",
                Content = "failed",
            },
            new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = "Submission confirmed again",
            },
        };

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Submission confirmed again",
            outboundIntent: null,
            history,
            [Receipt("call-1", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.Mutating)],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.AppendedHistory[0].Content.Should().BeEmpty();
        delivery.AppendedHistory[0].ToolCalls.Should().ContainSingle(call => call.Id == "call-1");
        delivery.AppendedHistory[1].Content.Should().Be("failed");
        delivery.AppendedHistory[2].Content.Should().Be(delivery.ReplyText);
        delivery.AppendedHistory.Should().NotContain(entry =>
            entry.Content.Contains("Submission confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WhenBlankCallIdsConflict_ShouldKeepBothReceiptsDistinctAndBlock()
    {
        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Submission confirmed",
            outboundIntent: null,
            appendedHistory: [],
            receipts:
            [
                Receipt("", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.Mutating),
                Receipt("", AgentToolReceiptStatus.Success, AgentToolReceiptEffect.Mutating),
            ],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Contain("[tool receipt] Failed");
        delivery.ReplyText.Should().NotContain("Submission confirmed");
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "record.submit")]
    public void Build_WhenLegacyReceiptDeclaresMutation_ShouldBlock(
        bool isDestructive,
        string sideEffectKind)
    {
        var receipt = Receipt("legacy", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.Unspecified);
        receipt.IsDestructive = isDestructive;
        receipt.SideEffectKind = sideEffectKind;

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Submission confirmed",
            outboundIntent: null,
            appendedHistory: [],
            receipts: [receipt],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().NotContain("Submission confirmed");
    }

    [Fact]
    public void Build_WhenLegacyReceiptHasNoMutationEvidence_ShouldNotBlock()
    {
        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Fallback answer",
            outboundIntent: null,
            appendedHistory: [],
            receipts: [Receipt("legacy", AgentToolReceiptStatus.Error, AgentToolReceiptEffect.Unspecified)],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().StartWith("Fallback answer");
        delivery.ReplyText.Should().Contain("[tool receipt] Failed");
    }

    private static AgentToolReceipt Receipt(
        string callId,
        AgentToolReceiptStatus status,
        AgentToolReceiptEffect effect,
        string toolName = "submit_record") =>
        new()
        {
            CallId = callId,
            ToolName = toolName,
            Status = status,
            Effect = effect,
            ErrorMessage = "The operation was not confirmed.",
        };
}
