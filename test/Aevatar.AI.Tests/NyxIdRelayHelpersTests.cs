using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public class NyxIdRelayHelpersTests
{
    // ── NyxIdRelayPayloads ──────────────────────────────────────────────────

    [Fact]
    public void GetContentType_ShouldReturnEmpty_WhenContentIsNull()
    {
        NyxIdRelayPayloads.GetContentType(null).Should().BeEmpty();
    }

    [Fact]
    public void GetContentType_ShouldPreferContentTypeOverType_WhenBothPresent()
    {
        var content = new RelayContent { ContentType = "Card_Action", Type = "text" };

        NyxIdRelayPayloads.GetContentType(content).Should().Be("card_action");
    }

    [Fact]
    public void GetContentType_ShouldFallBackToType_WhenContentTypeIsBlank()
    {
        var content = new RelayContent { ContentType = "   ", Type = "  TEXT  " };

        NyxIdRelayPayloads.GetContentType(content).Should().Be("text");
    }

    [Fact]
    public void GetContentType_ShouldReturnEmpty_WhenBothFieldsBlank()
    {
        NyxIdRelayPayloads.GetContentType(new RelayContent()).Should().BeEmpty();
    }

    [Fact]
    public void NormalizeContentType_ShouldLowercaseAndTrim()
    {
        NyxIdRelayPayloads.NormalizeContentType("  Card_Action  ").Should().Be("card_action");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeOptional_ShouldReturnNull_ForBlankInput(string? input)
    {
        NyxIdRelayPayloads.NormalizeOptional(input).Should().BeNull();
    }

    [Fact]
    public void NormalizeOptional_ShouldTrim_NonBlankInput()
    {
        NyxIdRelayPayloads.NormalizeOptional("  hello  ").Should().Be("hello");
    }

    // ── NyxIdRelayReplyAccumulator ──────────────────────────────────────────

    [Fact]
    public void Accumulator_ShouldUseDefaultMaxChars_WhenCtorReceivesNonPositive()
    {
        new NyxIdRelayReplyAccumulator(0).MaxChars.Should().Be(16 * 1024);
        new NyxIdRelayReplyAccumulator(-1).MaxChars.Should().Be(16 * 1024);
    }

    [Fact]
    public void Accumulator_ShouldReportEmpty_BeforeAnyAppend()
    {
        var accumulator = new NyxIdRelayReplyAccumulator(64);

        accumulator.IsEmpty.Should().BeTrue();
        accumulator.WasTruncated.Should().BeFalse();
        accumulator.Snapshot().Should().BeEmpty();
        accumulator.GetErrorMessage().Should().BeNull();
    }

    [Fact]
    public void Accumulator_AppendsBeyondCap_ShouldTruncateAndStopAccepting()
    {
        var accumulator = new NyxIdRelayReplyAccumulator(5);

        accumulator.Append("hello");
        accumulator.Append(" world");
        accumulator.Append("!");

        accumulator.Snapshot().Should().Be("hello");
        accumulator.WasTruncated.Should().BeTrue();
        accumulator.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Accumulator_PartialAppendAtCap_ShouldFillRemainingThenMarkTruncated()
    {
        var accumulator = new NyxIdRelayReplyAccumulator(8);

        accumulator.Append("hello");
        accumulator.Append(" world");

        accumulator.Snapshot().Should().Be("hello wo");
        accumulator.WasTruncated.Should().BeTrue();
    }

    [Fact]
    public void Accumulator_Append_ShouldIgnoreNullOrEmpty()
    {
        var accumulator = new NyxIdRelayReplyAccumulator(64);

        accumulator.Append(null);
        accumulator.Append(string.Empty);

        accumulator.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Accumulator_SetError_ShouldIgnoreBlank_AndTrimNonBlank()
    {
        var accumulator = new NyxIdRelayReplyAccumulator(64);

        accumulator.SetError(null);
        accumulator.SetError("   ");
        accumulator.GetErrorMessage().Should().BeNull();

        accumulator.SetError("  boom  ");
        accumulator.GetErrorMessage().Should().Be("boom");
    }

    // ── NyxIdRelayReplies (classification + LLM error extraction) ───────────

    [Theory]
    [InlineData("Got 403 Forbidden", "403 Forbidden")]
    [InlineData("forbidden by upstream", "403 Forbidden")]
    [InlineData("HTTP 401 Unauthorized", "authentication with the AI service failed")]
    [InlineData("authentication failure", "authentication with the AI service failed")]
    [InlineData("rate limit hit", "AI service is busy")]
    [InlineData("HTTP 429 too many requests", "AI service is busy")]
    [InlineData("upstream timeout", "took too long")]
    [InlineData("model `gpt-x` not found", "configured AI model is not available")]
    [InlineData("totally unrelated", "something went wrong")]
    public void ClassifyError_ShouldMapTechnicalErrorsToFriendlyMessages(string error, string expectedFragment)
    {
        NyxIdRelayReplies.ClassifyError(error).Should().Contain(expectedFragment);
    }

    [Fact]
    public void TryExtractLlmError_ShouldReturnFalse_ForNullOrUnrelatedContent()
    {
        NyxIdRelayReplies.TryExtractLlmError(null, out var error1).Should().BeFalse();
        error1.Should().BeEmpty();

        NyxIdRelayReplies.TryExtractLlmError("just a normal reply", out var error2).Should().BeFalse();
        error2.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractLlmError_ShouldStripAevatarMarkerPrefix()
    {
        NyxIdRelayReplies.TryExtractLlmError("[[AEVATAR_LLM_ERROR]]  upstream 500  ", out var error)
            .Should().BeTrue();
        error.Should().Be("upstream 500");
    }

    [Fact]
    public void TryExtractLlmError_ShouldKeepLlmFailedPrefixVerbatim()
    {
        NyxIdRelayReplies.TryExtractLlmError("LLM request failed: timeout after 30s", out var error)
            .Should().BeTrue();
        error.Should().Be("LLM request failed: timeout after 30s");
    }

    // ── NyxIdRelayWorkflowCards.TryBuildCommand ─────────────────────────────

    [Fact]
    public void TryBuildCommand_ShouldReturnFalse_WhenContentTypeIsNotCardAction()
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Content = new RelayContent { Type = "text", Text = "{}" },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeFalse();
        command.Should().BeNull();
    }

    [Fact]
    public void TryBuildCommand_ShouldReturnFalse_WhenPayloadIsBlank()
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Content = new RelayContent { ContentType = "card_action", Text = "   " },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeFalse();
        command.Should().BeNull();
    }

    [Fact]
    public void TryBuildCommand_ShouldReturnFalse_WhenPayloadIsNotJson()
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Content = new RelayContent { ContentType = "card_action", Text = "not json" },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeFalse();
        command.Should().BeNull();
    }

    [Fact]
    public void TryBuildCommand_ShouldReturnFalse_WhenRequiredKeysMissing()
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Content = new RelayContent
            {
                ContentType = "card_action",
                Text = "{\"value\":{\"actor_id\":\"a\",\"run_id\":\"r\"}}",
            },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeFalse();
        command.Should().BeNull();
    }

    [Theory]
    [InlineData("reject", false)]
    [InlineData("rejected", false)]
    [InlineData("deny", false)]
    [InlineData("denied", false)]
    [InlineData("cancel", false)]
    [InlineData("approve", true)]
    [InlineData("anything-else", true)]
    public void TryBuildCommand_ShouldHonorActionDecisionLikeValues(string action, bool expectedApproved)
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Content = new RelayContent
            {
                ContentType = "card_action",
                Text = $"{{\"value\":{{\"actor_id\":\"a\",\"run_id\":\"r\",\"step_id\":\"s\",\"action\":\"{action}\"}}}}",
            },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeTrue();
        command!.Approved.Should().Be(expectedApproved);
    }

    [Fact]
    public void TryBuildCommand_ApprovedPath_ShouldPreferEditedContentForUserInput()
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Platform = "lark",
            Conversation = new RelayConversation { PlatformId = "oc_chat" },
            Content = new RelayContent
            {
                ContentType = "card_action",
                Text = "{\"value\":{\"actor_id\":\"a\",\"run_id\":\"r\",\"step_id\":\"s\",\"approved\":true,\"edited_content\":\"ship it\",\"user_input\":\"comment\"}}",
            },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeTrue();
        command!.Approved.Should().BeTrue();
        command.UserInput.Should().Be("ship it");
        command.EditedContent.Should().Be("ship it");
        command.Feedback.Should().Be("comment");
        command.Metadata!["channel.platform"].Should().Be("lark");
        command.Metadata["channel.conversation_id"].Should().Be("oc_chat");
    }

    [Fact]
    public void TryBuildCommand_RejectedPath_ShouldPreferUserInputForFeedback()
    {
        var message = new RelayMessage
        {
            MessageId = "msg-1",
            Conversation = new RelayConversation { Id = "conv-id" },
            Content = new RelayContent
            {
                ContentType = "card_action",
                Text = "{\"value\":{\"actor_id\":\"a\",\"run_id\":\"r\",\"step_id\":\"s\",\"approved\":false,\"user_input\":\"please redo\",\"edited_content\":\"unused\"}}",
            },
        };

        NyxIdRelayWorkflowCards.TryBuildCommand(message, out var command).Should().BeTrue();
        command!.Approved.Should().BeFalse();
        command.UserInput.Should().Be("please redo");
        command.Feedback.Should().Be("please redo");
        command.EditedContent.Should().Be("unused");
        command.Metadata!["channel.conversation_id"].Should().Be("conv-id");
    }
}
