using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.GAgentService.Application.Responses;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class MessagesRequestNormalizerTests
{
    [Fact]
    public void Normalize_ShouldBuildTypedMessagesRequest_AndPreserveDeclaredTools()
    {
        var result = MessagesRequestNormalizer.Normalize(new MessagesCommandRequest(
            " claude-sonnet ",
            1024,
            [ChatMessage.System("system"), ChatMessage.User("hello")],
            [new ResponsesApplicationToolDeclaration("lookup", "Lookup", "{}", "hash")],
            true,
            0.5,
            null,
            null,
            null,
            true,
            false,
            null));

        result.Succeeded.Should().BeTrue();
        var normalized = result.Request!;
        normalized.Model.Should().Be("claude-sonnet");
        normalized.MaxTokens.Should().Be(1024);
        normalized.Stream.Should().BeTrue();
        normalized.DroppedImageContent.Should().BeTrue();
        normalized.ChatMessages.Should().HaveCount(2);
        normalized.DeclaredTools.Should().ContainSingle(tool => tool.Name == "lookup");
    }

    [Fact]
    public void Normalize_ShouldDropDeclaredTools_WhenToolChoiceDisablesTools()
    {
        var result = MessagesRequestNormalizer.Normalize(new MessagesCommandRequest(
            "claude-sonnet",
            100,
            [ChatMessage.User("hello")],
            [new ResponsesApplicationToolDeclaration("lookup", "Lookup", "{}", "hash")],
            false,
            null,
            null,
            null,
            null,
            false,
            true,
            null));

        result.Succeeded.Should().BeTrue();
        result.Request!.DeclaredTools.Should().BeEmpty();
    }

    [Fact]
    public void Normalize_ShouldRejectUnsupportedControls_AndEmptyMessages()
    {
        var unsupported = MessagesRequestNormalizer.Normalize(new MessagesCommandRequest(
            "claude-sonnet",
            100,
            [ChatMessage.User("hello")],
            [],
            false,
            null,
            0.5,
            null,
            null,
            false,
            false,
            null));

        unsupported.Succeeded.Should().BeFalse();
        unsupported.ErrorCode.Should().Be("unsupported_parameter");

        var emptyMessages = MessagesRequestNormalizer.Normalize(new MessagesCommandRequest(
            "claude-sonnet",
            100,
            [],
            [],
            false,
            null,
            null,
            null,
            null,
            false,
            false,
            null));

        emptyMessages.Succeeded.Should().BeFalse();
        emptyMessages.ErrorCode.Should().Be("invalid_messages");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ShouldApplyDefaultModel_WhenCallerOmitsModel(string? callerModel)
    {
        var result = MessagesRequestNormalizer.Normalize(
            new MessagesCommandRequest(
                callerModel!,
                100,
                [ChatMessage.User("hello")],
                [],
                false,
                null,
                null,
                null,
                null,
                false,
                false,
                null),
            defaultModel: "chrono-llm-public/gpt-5.5");

        result.Succeeded.Should().BeTrue();
        result.Request!.Model.Should().Be("chrono-llm-public/gpt-5.5");
    }

    [Fact]
    public void Normalize_ShouldPreferExplicitModel_OverDefault()
    {
        var result = MessagesRequestNormalizer.Normalize(
            new MessagesCommandRequest(
                " claude-sonnet ",
                100,
                [ChatMessage.User("hello")],
                [],
                false,
                null,
                null,
                null,
                null,
                false,
                false,
                null),
            defaultModel: "chrono-llm-public/gpt-5.5");

        result.Succeeded.Should().BeTrue();
        result.Request!.Model.Should().Be("claude-sonnet");
    }

    [Fact]
    public void Normalize_ShouldRequireModel_WhenOmittedAndNoDefaultConfigured()
    {
        var result = MessagesRequestNormalizer.Normalize(
            new MessagesCommandRequest(
                "  ",
                100,
                [ChatMessage.User("hello")],
                [],
                false,
                null,
                null,
                null,
                null,
                false,
                false,
                null));

        result.Succeeded.Should().BeFalse();
        result.ErrorCode.Should().Be("model_required");
    }
}
