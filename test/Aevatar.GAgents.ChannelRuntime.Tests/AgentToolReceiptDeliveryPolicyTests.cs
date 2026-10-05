using Aevatar.AI.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentToolReceiptDeliveryPolicyTests
{
    [Fact]
    public void Build_WhenNoReceipts_ShouldPersistFinalAssistantReplyInHistory()
    {
        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Tomorrow at 7 PM for 2 people is available. Please reply confirm.",
            outboundIntent: null,
            appendedHistory: [],
            receipts: [],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Be("Tomorrow at 7 PM for 2 people is available. Please reply confirm.");
        delivery.AppendedHistory.Should().ContainSingle();
        delivery.AppendedHistory[0].Role.Should().Be("assistant");
        delivery.AppendedHistory[0].Content.Should().Be(delivery.ReplyText);
    }

    [Fact]
    public void Build_ShouldKeepOnlyUserAndFinalAssistantInAppendedHistory()
    {
        var history = new[]
        {
            new ConversationHistoryEntry { Role = "user", Content = "Can I book tomorrow at 7?" },
            new ConversationHistoryEntry
            {
                Role = "assistant",
                ToolCalls =
                {
                    new ConversationToolCallEntry
                    {
                        Id = "call-1",
                        Name = "calendar_list_events",
                        ArgumentsJson = "{}",
                    },
                },
            },
            new ConversationHistoryEntry { Role = "tool", ToolCallId = "call-1", Content = "[]" },
        };

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            "Tomorrow at 7 PM is available. Please reply confirm.",
            outboundIntent: null,
            appendedHistory: history,
            receipts: [],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.AppendedHistory.Should().HaveCount(2);
        delivery.AppendedHistory[0].Role.Should().Be("user");
        delivery.AppendedHistory[0].Content.Should().Be("Can I book tomorrow at 7?");
        delivery.AppendedHistory[1].Role.Should().Be("assistant");
        delivery.AppendedHistory[1].Content.Should().Be(delivery.ReplyText);
        delivery.AppendedHistory.Should().NotContain(entry => entry.Role == "tool" || entry.ToolCalls.Count > 0);
    }

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
    public void Build_WhenReadOnlyServiceScopeDenialIsCalleeConfirmed_ShouldKeepRecoveryNarrativeOnly()
    {
        const string recoveryNarrative =
            "Send `/init` in this Telegram chat and update Google Workspace authorization.";
        var receipt = Receipt(
            "call-google",
            AgentToolReceiptStatus.Error,
            AgentToolReceiptEffect.ReadOnly,
            toolName: "nyxid_invoke_operation");
        receipt.ErrorCode = "NYXID_PROXY_SERVICE_SCOPE_FORBIDDEN";
        receipt.ErrorMessage = "The NyxID caller credential is not authorized for this service.";
        receipt.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            recoveryNarrative,
            outboundIntent: null,
            appendedHistory: [],
            receipts: [receipt],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Be(recoveryNarrative);
        delivery.ReplyText.Should().NotContain("[tool receipt]");
        delivery.AppendedHistory.Should().ContainSingle();
        delivery.AppendedHistory[0].Content.Should().Be(recoveryNarrative);
    }

    [Fact]
    public void Build_WhenMutatingServiceScopeDenialIsCalleeConfirmed_ShouldReplaceRecoveryNarrative()
    {
        const string recoveryNarrative =
            "Send `/init` in this Telegram chat and update Google Workspace authorization.";
        var receipt = Receipt(
            "call-google",
            AgentToolReceiptStatus.Error,
            AgentToolReceiptEffect.Mutating,
            toolName: "nyxid_invoke_operation");
        receipt.ErrorCode = "NYXID_PROXY_SERVICE_SCOPE_FORBIDDEN";
        receipt.ErrorMessage = "The NyxID caller credential is not authorized for this service.";
        receipt.FailureOutcome = AgentToolFailureOutcome.CalleeConfirmed;

        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            recoveryNarrative,
            outboundIntent: null,
            appendedHistory: [],
            receipts: [receipt],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().NotContain(recoveryNarrative);
        delivery.ReplyText.Should().StartWith("[tool receipt] Failed: nyxid_invoke_operation");
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
    public void Build_WhenSelectorGuidancePrecedesCorrectedRead_ShouldKeepFinalNarrative()
    {
        var selectorGuidance = Receipt(
            "call-invalid-selector",
            AgentToolReceiptStatus.Success,
            AgentToolReceiptEffect.ReadOnly,
            toolName: "nyxid_invoke_operation");
        selectorGuidance.ResultJson =
            """{"status":"guidance","invoked":false,"error":"service_selector_not_visible"}""";
        selectorGuidance.ErrorCode = string.Empty;
        selectorGuidance.ErrorMessage = string.Empty;
        var correctedRead = Receipt(
            "call-corrected-read",
            AgentToolReceiptStatus.Success,
            AgentToolReceiptEffect.ReadOnly,
            toolName: "nyxid_invoke_operation");
        correctedRead.ResultJson = """{"status":"succeeded","data":{"items":[]}}""";
        correctedRead.ErrorCode = string.Empty;
        correctedRead.ErrorMessage = string.Empty;

        const string narrative = "Google Workspace is connected and the calendar list is available.";
        var delivery = AgentToolReceiptDeliveryPolicy.Build(
            narrative,
            outboundIntent: null,
            appendedHistory: [],
            receipts: [selectorGuidance, correctedRead],
            toolCalls: [],
            new AgentToolReceiptRenderer());

        delivery.ReplyText.Should().Be(narrative);
        delivery.ReplyText.Should().NotContain("[tool receipt] Failed");
        delivery.AppendedHistory.Should().ContainSingle();
        delivery.AppendedHistory[0].Content.Should().Be(narrative);
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

        delivery.AppendedHistory.Should().ContainSingle();
        delivery.AppendedHistory[0].Role.Should().Be("assistant");
        delivery.AppendedHistory[0].Content.Should().Be(delivery.ReplyText);
        delivery.AppendedHistory[0].ToolCalls.Should().BeEmpty();
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
