using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Context.Abstractions;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Retrieval.Tests;

public sealed class IntentAnalyzerTests
{
    private readonly ILLMProviderFactory _factory = Substitute.For<ILLMProviderFactory>();
    private readonly ILLMProvider _provider = Substitute.For<ILLMProvider>();
    private readonly IntentAnalyzer _sut;

    public IntentAnalyzerTests()
    {
        _factory.GetDefault().Returns(_provider);
        _sut = new IntentAnalyzer(_factory);
    }

    [Fact]
    public async Task AnalyzeAsync_ValidJson_ReturnsTypedQueries()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                [
                    {"query": "search web info", "contextType": "skill", "intent": "find skill", "priority": 1},
                    {"query": "RFC template", "contextType": "resource", "intent": "find doc", "priority": 2}
                ]
                """
            });

        var results = await _sut.AnalyzeAsync("search the web and write an RFC");

        results.Should().HaveCount(2);
        results[0].Query.Should().Be("search web info");
        results[0].ContextType.Should().Be(ContextType.Skill);
        results[0].Priority.Should().Be(1);
        results[1].Query.Should().Be("RFC template");
        results[1].ContextType.Should().Be(ContextType.Resource);
    }

    [Fact]
    public async Task AnalyzeAsync_EmptyArray_ReturnsEmptyList()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "[]" });

        var results = await _sut.AnalyzeAsync("hello");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_JsonWrappedInCodeFence_ParsesCorrectly()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                ```json
                [{"query": "test", "contextType": "memory", "intent": "recall", "priority": 1}]
                ```
                """
            });

        var results = await _sut.AnalyzeAsync("remember my preference");

        results.Should().ContainSingle();
        results[0].ContextType.Should().Be(ContextType.Memory);
    }

    [Fact]
    public async Task AnalyzeAsync_LLMThrows_ReturnsFallbackQuery()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns<LLMResponse>(_ => throw new InvalidOperationException("LLM error"));

        var results = await _sut.AnalyzeAsync("some query");

        results.Should().ContainSingle();
        results[0].Query.Should().Be("some query");
        results[0].ContextType.Should().Be(ContextType.Resource);
        results[0].Intent.Should().Be("fallback");
    }

    [Fact]
    public async Task AnalyzeAsync_InvalidJson_ReturnsFallbackQuery()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "not json at all" });

        var results = await _sut.AnalyzeAsync("test query");

        results.Should().ContainSingle();
        results[0].Query.Should().Be("test query");
        results[0].Intent.Should().Be("fallback");
    }

    [Fact]
    public async Task AnalyzeAsync_WithSession_IncludesRecentMessages()
    {
        LLMRequest? captured = null;
        _provider
            .ChatAsync(Arg.Do<LLMRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "[]" });

        var session = new SessionInfo("s1", ["msg1", "msg2"]);
        await _sut.AnalyzeAsync("query", session);

        captured.Should().NotBeNull();
        var prompt = captured!.Messages[0].Content!;
        prompt.Should().Contain("msg1");
        prompt.Should().Contain("msg2");
    }

    [Fact]
    public async Task AnalyzeAsync_PriorityClampedTo1_5()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                [
                    {"query": "q1", "contextType": "resource", "intent": "test", "priority": 0},
                    {"query": "q2", "contextType": "resource", "intent": "test", "priority": 10}
                ]
                """
            });

        var results = await _sut.AnalyzeAsync("test");

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Priority >= 1 && r.Priority <= 5);
    }

    [Fact]
    public async Task AnalyzeAsync_LimitsTo5Queries()
    {
        var jsonItems = Enumerable.Range(1, 8)
            .Select(i => $$"""{"query": "q{{i}}", "contextType": "resource", "intent": "test", "priority": {{i}}}""");
        var json = $"[{string.Join(",", jsonItems)}]";

        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = json });

        var results = await _sut.AnalyzeAsync("test");

        results.Should().HaveCountLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task AnalyzeAsync_NullContent_ReturnsEmpty()
    {
        _provider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = null });

        var results = await _sut.AnalyzeAsync("hello");

        results.Should().BeEmpty();
    }
}
