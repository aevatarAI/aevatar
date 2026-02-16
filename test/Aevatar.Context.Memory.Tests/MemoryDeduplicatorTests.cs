using Aevatar.Context.Abstractions;
using Aevatar.Context.Retrieval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace Aevatar.Context.Memory.Tests;

public sealed class MemoryDeduplicatorTests
{
    private readonly IContextVectorIndex _vectorIndex = Substitute.For<IContextVectorIndex>();
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
    private readonly MemoryDeduplicator _sut;

    public MemoryDeduplicatorTests()
    {
        _sut = new MemoryDeduplicator(_vectorIndex, _embedder);
    }

    private void SetupEmbedder(float[] vector)
    {
        var embedding = new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(vector)]);
        _embedder
            .GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(embedding);
    }

    // ─── Create ───

    [Fact]
    public async Task Deduplicate_NoSimilarExisting_ReturnsCreate()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());

        var candidates = new List<CandidateMemory>
        {
            new(MemoryCategory.Preferences, "User prefers dark mode", "conversation"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results.Should().ContainSingle();
        results[0].Decision.Should().Be(DeduplicationDecision.Create);
    }

    [Fact]
    public async Task Deduplicate_LowSimilarity_ReturnsCreate()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>
            {
                new(AevatarUri.Parse("aevatar://user/u1/memories/preferences/old.md"),
                    ContextType.Memory, true, "Old preference", 0.5f),
            });

        var candidates = new List<CandidateMemory>
        {
            new(MemoryCategory.Preferences, "User prefers dark mode", "conversation"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results[0].Decision.Should().Be(DeduplicationDecision.Create);
    }

    // ─── Update ───

    [Fact]
    public async Task Deduplicate_HighSimilarity_MergeableCategory_ReturnsUpdate()
    {
        SetupEmbedder([1f, 0f, 0f]);

        var existingUri = AevatarUri.Parse("aevatar://user/u1/memories/preferences/theme.md");
        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>
            {
                new(existingUri, ContextType.Memory, true, "Theme preference", 0.90f),
            });

        var candidates = new List<CandidateMemory>
        {
            new(MemoryCategory.Preferences, "User changed to light mode", "conversation"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results[0].Decision.Should().Be(DeduplicationDecision.Update);
        results[0].ExistingUri.Should().Be(existingUri);
    }

    // ─── Skip ───

    [Fact]
    public async Task Deduplicate_VerySimilar_ImmutableCategory_ReturnsSkip()
    {
        SetupEmbedder([1f, 0f, 0f]);

        var existingUri = AevatarUri.Parse("aevatar://user/u1/memories/events/decision.md");
        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>
            {
                new(existingUri, ContextType.Memory, true, "Same decision", 0.96f),
            });

        var candidates = new List<CandidateMemory>
        {
            new(MemoryCategory.Events, "Decision: use PostgreSQL", "conversation"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results[0].Decision.Should().Be(DeduplicationDecision.Skip);
    }

    [Fact]
    public async Task Deduplicate_SimilarButNotExtreme_ImmutableCategory_ReturnsCreate()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>
            {
                new(AevatarUri.Parse("aevatar://agent/a1/memories/cases/old.md"),
                    ContextType.Memory, true, "Similar case", 0.90f),
            });

        var candidates = new List<CandidateMemory>
        {
            new(MemoryCategory.Cases, "Fix: added index to DB query", "conversation"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results[0].Decision.Should().Be(DeduplicationDecision.Create);
    }

    // ─── Target URI resolution ───

    [Theory]
    [InlineData(MemoryCategory.Profile, "user")]
    [InlineData(MemoryCategory.Preferences, "user")]
    [InlineData(MemoryCategory.Entities, "user")]
    [InlineData(MemoryCategory.Events, "user")]
    [InlineData(MemoryCategory.Cases, "agent")]
    [InlineData(MemoryCategory.Patterns, "agent")]
    public async Task Deduplicate_ResolvesTargetUriToCorrectScope(MemoryCategory category, string expectedScope)
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());

        var candidates = new List<CandidateMemory>
        {
            new(category, "Some content", "source"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results[0].TargetScope.Scope.Should().Be(expectedScope);
    }

    // ─── Multiple candidates ───

    [Fact]
    public async Task Deduplicate_MultipleCandidates_ProcessesEach()
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>());

        var candidates = new List<CandidateMemory>
        {
            new(MemoryCategory.Profile, "Name: Alice", "source"),
            new(MemoryCategory.Preferences, "Prefers Vim", "source"),
            new(MemoryCategory.Cases, "Fixed auth bug", "source"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.Decision == DeduplicationDecision.Create);
    }

    // ─── Mergeable categories ───

    [Theory]
    [InlineData(MemoryCategory.Profile, true)]
    [InlineData(MemoryCategory.Preferences, true)]
    [InlineData(MemoryCategory.Entities, true)]
    [InlineData(MemoryCategory.Events, false)]
    [InlineData(MemoryCategory.Cases, false)]
    [InlineData(MemoryCategory.Patterns, true)]
    public async Task Deduplicate_MergeableCategory_DeterminesUpdateOrCreate(MemoryCategory category, bool isMergeable)
    {
        SetupEmbedder([1f, 0f, 0f]);

        _vectorIndex
            .SearchAsync(Arg.Any<ReadOnlyMemory<float>>(), 3, Arg.Any<AevatarUri?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VectorSearchResult>
            {
                new(AevatarUri.Parse("aevatar://user/u1/memories/existing.md"),
                    ContextType.Memory, true, "Existing", 0.90f),
            });

        var candidates = new List<CandidateMemory>
        {
            new(category, "New content", "source"),
        };

        var results = await _sut.DeduplicateAsync(candidates, "u1", "a1");

        if (isMergeable)
            results[0].Decision.Should().Be(DeduplicationDecision.Update);
        else
            results[0].Decision.Should().Be(DeduplicationDecision.Create);
    }
}
