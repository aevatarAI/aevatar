using Aevatar.AI.Abstractions.LLMProviders;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Extraction.Tests;

public sealed class LLMContextLayerGeneratorTests
{
    private readonly ILLMProviderFactory _factory = Substitute.For<ILLMProviderFactory>();
    private readonly ILLMProvider _provider = Substitute.For<ILLMProvider>();
    private readonly LLMContextLayerGenerator _sut;

    public LLMContextLayerGeneratorTests()
    {
        _factory.GetDefault().Returns(_provider);
        _sut = new LLMContextLayerGenerator(_factory);
    }

    // ─── GenerateAbstractAsync ───

    [Fact]
    public async Task GenerateAbstractAsync_ReturnsLLMContent()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "A web search skill." });

        var result = await _sut.GenerateAbstractAsync("# Web Search\nSearches the web.", "SKILL.md");

        result.Should().Be("A web search skill.");
    }

    [Fact]
    public async Task GenerateAbstractAsync_TrimsWhitespace()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "  trimmed result  \n" });

        var result = await _sut.GenerateAbstractAsync("content", "test.md");

        result.Should().Be("trimmed result");
    }

    [Fact]
    public async Task GenerateAbstractAsync_NullContent_ReturnsEmpty()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = null });

        var result = await _sut.GenerateAbstractAsync("content", "test.md");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateAbstractAsync_TruncatesLongContent()
    {
        var longContent = new string('x', 20_000);
        LLMRequest? captured = null;
        _provider
            .ChatAsync(Arg.Do<LLMRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "summary" });

        await _sut.GenerateAbstractAsync(longContent, "big.md");

        captured.Should().NotBeNull();
        var prompt = captured!.Messages[0].Content!;
        prompt.Should().Contain("(truncated)");
    }

    [Fact]
    public async Task GenerateAbstractAsync_SetsMaxTokens150()
    {
        LLMRequest? captured = null;
        _provider
            .ChatAsync(Arg.Do<LLMRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "ok" });

        await _sut.GenerateAbstractAsync("content", "test.md");

        captured!.MaxTokens.Should().Be(150);
    }

    // ─── GenerateOverviewAsync ───

    [Fact]
    public async Task GenerateOverviewAsync_ReturnsLLMContent()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "# Overview\nDetailed overview." });

        var result = await _sut.GenerateOverviewAsync("content", "test.md");

        result.Should().Be("# Overview\nDetailed overview.");
    }

    [Fact]
    public async Task GenerateOverviewAsync_SetsMaxTokens2500()
    {
        LLMRequest? captured = null;
        _provider
            .ChatAsync(Arg.Do<LLMRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "ok" });

        await _sut.GenerateOverviewAsync("content", "test.md");

        captured!.MaxTokens.Should().Be(2500);
    }

    // ─── GenerateDirectoryLayersAsync ───

    [Fact]
    public async Task GenerateDirectoryLayersAsync_ReturnsBothLayers()
    {
        var callCount = 0;
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                callCount++;
                return callCount == 1
                    ? new LLMResponse { Content = "Directory abstract" }
                    : new LLMResponse { Content = "Directory overview" };
            });

        var (abstractText, overviewText) = await _sut.GenerateDirectoryLayersAsync(
            "skills", ["Skill A summary", "Skill B summary"]);

        abstractText.Should().Be("Directory abstract");
        overviewText.Should().Be("Directory overview");
    }

    [Fact]
    public async Task GenerateDirectoryLayersAsync_IncludesChildSummariesInPrompt()
    {
        var requests = new List<LLMRequest>();
        _provider
            .ChatAsync(Arg.Do<LLMRequest>(r => requests.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "result" });

        await _sut.GenerateDirectoryLayersAsync("docs", ["Child A", "Child B", "Child C"]);

        requests.Should().HaveCount(2);
        var firstPrompt = requests[0].Messages[0].Content!;
        firstPrompt.Should().Contain("Child A");
        firstPrompt.Should().Contain("Child B");
        firstPrompt.Should().Contain("Child C");
    }

    [Fact]
    public async Task GenerateDirectoryLayersAsync_MakesExactlyTwoLLMCalls()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "ok" });

        await _sut.GenerateDirectoryLayersAsync("test", ["summary"]);

        await _provider.Received(2).ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>());
    }
}
