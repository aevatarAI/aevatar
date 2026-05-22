using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ResponsesRequestNormalizerTests
{
    [Fact]
    public void Normalize_ShouldTrimPrompt_AndKeepContinuationToolResults()
    {
        var result = ResponsesRequestNormalizer.Normalize(new ResponsesCommandRequest(
            "  openai/gpt-5  ",
            " first \n\n second ",
            [new ResponsesToolResultInput(" call_1 ", """{"ok":true}""", " hash-1 ")],
            true,
            " resp_previous ",
            0.7,
            128,
            [new ResponsesApplicationToolDeclaration("lookup", "Lookup", "{}", "hash")]));

        result.Succeeded.Should().BeTrue();
        var normalized = result.Request!;
        normalized.Model.Should().Be("openai/gpt-5");
        normalized.Prompt.Should().Be("first\nsecond");
        normalized.Stream.Should().BeTrue();
        normalized.PreviousResponseId.Should().Be("resp_previous");
        normalized.ToolResults.Should().ContainSingle().Which.Should().Be(
            new ResponsesToolResultInput("call_1", """{"ok":true}""", "hash-1"));
        normalized.DeclaredTools.Should().ContainSingle(tool => tool.Name == "lookup");
    }

    [Fact]
    public void Normalize_ShouldFoldToolResultIntoPrompt_WhenNoPreviousResponseId()
    {
        var result = ResponsesRequestNormalizer.Normalize(new ResponsesCommandRequest(
            "model",
            "continue",
            [new ResponsesToolResultInput("call_1", "tool output", null)],
            false,
            null,
            null,
            null,
            []));

        result.Succeeded.Should().BeTrue();
        result.Request!.ToolResults.Should().BeEmpty();
        result.Request.Prompt.Should().Be("continue\n[tool_result call_id=call_1] tool output");
    }

    [Fact]
    public void Normalize_ShouldRejectEmptyInput_AndInvalidMaxOutputTokens()
    {
        var emptyInput = ResponsesRequestNormalizer.Normalize(new ResponsesCommandRequest(
            "model",
            "   ",
            [],
            false,
            null,
            null,
            null,
            []));

        emptyInput.Succeeded.Should().BeFalse();
        emptyInput.ErrorCode.Should().Be("invalid_input");

        var invalidTokens = ResponsesRequestNormalizer.Normalize(new ResponsesCommandRequest(
            "model",
            "hello",
            [],
            false,
            null,
            null,
            0,
            []));

        invalidTokens.Succeeded.Should().BeFalse();
        invalidTokens.ErrorCode.Should().Be("invalid_max_output_tokens");
    }
}
