using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Memory.Tests;

public sealed class LLMMemoryExtractorTests
{
    private readonly ILLMProviderFactory _factory = Substitute.For<ILLMProviderFactory>();
    private readonly ILLMProvider _provider = Substitute.For<ILLMProvider>();
    private readonly LLMMemoryExtractor _sut;

    public LLMMemoryExtractorTests()
    {
        _factory.GetDefault().Returns(_provider);
        _sut = new LLMMemoryExtractor(_factory);
    }

    [Fact]
    public async Task ExtractAsync_ValidJson_ReturnsCandidateMemories()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                [
                    {"category": "preferences", "content": "User prefers dark mode", "source": "chat"},
                    {"category": "entities", "content": "Project: Aevatar", "source": "chat"}
                ]
                """
            });

        var results = await _sut.ExtractAsync(["User said they prefer dark mode and are working on Aevatar"]);

        results.Should().HaveCount(2);
        results[0].Category.Should().Be(MemoryCategory.Preferences);
        results[0].Content.Should().Be("User prefers dark mode");
        results[1].Category.Should().Be(MemoryCategory.Entities);
        results[1].Content.Should().Be("Project: Aevatar");
    }

    [Fact]
    public async Task ExtractAsync_EmptyMessages_ReturnsEmpty()
    {
        var results = await _sut.ExtractAsync([]);

        results.Should().BeEmpty();
        await _provider.DidNotReceive().ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_EmptyJsonArray_ReturnsEmpty()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "[]" });

        var results = await _sut.ExtractAsync(["just a greeting"]);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_JsonInCodeFence_ParsesCorrectly()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                ```json
                [{"category": "profile", "content": "Name: Alice", "source": "intro"}]
                ```
                """
            });

        var results = await _sut.ExtractAsync(["My name is Alice"]);

        results.Should().ContainSingle();
        results[0].Category.Should().Be(MemoryCategory.Profile);
    }

    [Fact]
    public async Task ExtractAsync_InvalidJson_ReturnsEmpty()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "not valid json" });

        var results = await _sut.ExtractAsync(["test message"]);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_LLMThrows_ReturnsEmpty()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns<LLMResponse>(_ => throw new InvalidOperationException("network error"));

        var results = await _sut.ExtractAsync(["test"]);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_NullContent_ReturnsEmpty()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = null });

        var results = await _sut.ExtractAsync(["test"]);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_FiltersOutEmptyContent()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                [
                    {"category": "profile", "content": "Valid", "source": "chat"},
                    {"category": "events", "content": "", "source": "chat"},
                    {"category": "cases", "content": null, "source": "chat"}
                ]
                """
            });

        var results = await _sut.ExtractAsync(["conversation text"]);

        results.Should().ContainSingle();
        results[0].Content.Should().Be("Valid");
    }

    [Theory]
    [InlineData("profile", MemoryCategory.Profile)]
    [InlineData("preferences", MemoryCategory.Preferences)]
    [InlineData("entities", MemoryCategory.Entities)]
    [InlineData("events", MemoryCategory.Events)]
    [InlineData("cases", MemoryCategory.Cases)]
    [InlineData("patterns", MemoryCategory.Patterns)]
    [InlineData("PROFILE", MemoryCategory.Profile)]
    [InlineData("unknown", MemoryCategory.Events)]
    public async Task ExtractAsync_ParsesCategories(string input, MemoryCategory expected)
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = $$"""[{"category": "{{input}}", "content": "test", "source": "s"}]"""
            });

        var results = await _sut.ExtractAsync(["msg"]);

        results.Should().ContainSingle();
        results[0].Category.Should().Be(expected);
    }

    [Fact]
    public async Task ExtractAsync_TruncatesLongConversation()
    {
        LLMRequest? captured = null;
        _provider
            .ChatAsync(Arg.Do<LLMRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "[]" });

        var longMessage = new string('x', 15000);
        await _sut.ExtractAsync([longMessage]);

        captured.Should().NotBeNull();
        var prompt = captured!.Messages[0].Content!;
        prompt.Should().Contain("(truncated)");
    }
}
