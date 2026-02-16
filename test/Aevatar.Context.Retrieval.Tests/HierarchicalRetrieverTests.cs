using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Context.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Retrieval.Tests;

public sealed class HierarchicalRetrieverTests
{
    private readonly IContextVectorIndex _vectorIndex = Substitute.For<IContextVectorIndex>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly ILLMProviderFactory _llmFactory = Substitute.For<ILLMProviderFactory>();
    private readonly ILLMProvider _llmProvider = Substitute.For<ILLMProvider>();
    private readonly IntentAnalyzer _intentAnalyzer;
    private readonly HierarchicalRetriever _sut;

    public HierarchicalRetrieverTests()
    {
        _llmFactory.GetDefault().Returns(_llmProvider);
        _intentAnalyzer = new IntentAnalyzer(_llmFactory);
        _sut = new HierarchicalRetriever(_vectorIndex, _embedder, _intentAnalyzer);
    }

    private void SetupEmbedder(float[] vector)
    {
        var embedding = new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]);
        _embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(embedding);
    }

    // ─── FindAsync ───

    [Fact]
    public async Task FindAsync_ReturnsResultsCategorizedByType()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 10, null, Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>
            {
                new(AevatarUri.Parse("aevatar://skills/a/SKILL.md"), ContextType.Skill, true, "Skill A", 0.9f),
                new(AevatarUri.Parse("aevatar://resources/b.md"), ContextType.Resource, true, "Resource B", 0.8f),
                new(AevatarUri.Parse("aevatar://user/u1/memories/pref.md"), ContextType.Memory, true, "Memory C", 0.7f),
            });

        var result = await _sut.FindAsync("test query");

        result.Skills.Should().ContainSingle();
        result.Resources.Should().ContainSingle();
        result.Memories.Should().ContainSingle();
        result.Total.Should().Be(3);
    }

    [Fact]
    public async Task FindAsync_WithScopeFilter_PassesFilterToVectorIndex()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 10, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());

        var scope = AevatarUri.SkillsRoot();
        await _sut.FindAsync("query", scope);

        await _vectorIndex.Received(1).SearchAsync(
            Arg.Any<ReadOnlyMemory<float>>(), 10, scope, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindAsync_EmptyResults_ReturnsEmptyFindResult()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 10, null, Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());

        var result = await _sut.FindAsync("query");

        result.Total.Should().Be(0);
        result.Skills.Should().BeEmpty();
        result.Resources.Should().BeEmpty();
        result.Memories.Should().BeEmpty();
    }

    // ─── SearchAsync ───

    [Fact]
    public async Task SearchAsync_EmptyIntentAnalysis_ReturnsEmpty()
    {
        _llmProvider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse { Content = "[]" });

        var session = new SessionInfo("s1", ["hello"]);
        var result = await _sut.SearchAsync("hello", session);

        result.Should().Be(FindResult.Empty);
    }

    [Fact]
    public async Task SearchAsync_WithTypedQueries_PerformsHierarchicalSearch()
    {
        _llmProvider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """[{"query": "web search", "contextType": "skill", "intent": "find", "priority": 1}]"""
            });

        SetupEmbedder([1f, 0f, 0f]);

        var skillResult = new VectorSearchResult(
            AevatarUri.Parse("aevatar://skills/web/SKILL.md"),
            ContextType.Skill, true, "Web search skill", 0.9f);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult> { skillResult });

        var session = new SessionInfo("s1", []);
        var result = await _sut.SearchAsync("search the web", session);

        result.Skills.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesByUri()
    {
        _llmProvider
            .ChatAsync(Arg.Any<LLMRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMResponse
            {
                Content = """
                [
                    {"query": "q1", "contextType": "resource", "intent": "find", "priority": 1},
                    {"query": "q2", "contextType": "resource", "intent": "find", "priority": 2}
                ]
                """
            });

        SetupEmbedder([1f, 0f, 0f]);

        var sameResult = new VectorSearchResult(
            AevatarUri.Parse("aevatar://resources/doc.md"),
            ContextType.Resource, true, "Same doc", 0.85f);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult> { sameResult });

        var session = new SessionInfo("s1", []);
        var result = await _sut.SearchAsync("find doc", session);

        result.Resources.Should().ContainSingle();
    }
}
