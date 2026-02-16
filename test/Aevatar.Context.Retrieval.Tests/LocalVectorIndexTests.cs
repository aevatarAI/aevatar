using Aevatar.Context.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.Context.Retrieval.Tests;

public sealed class LocalVectorIndexTests
{
    private readonly LocalVectorIndex _sut = new();

    private static VectorIndexEntry MakeEntry(
        string uri,
        string parentUri,
        ContextType type = ContextType.Resource,
        bool isLeaf = true,
        float[]? vector = null,
        string @abstract = "test")
    {
        vector ??= [1f, 0f, 0f];
        return new VectorIndexEntry(
            AevatarUri.Parse(uri),
            AevatarUri.Parse(parentUri),
            type,
            isLeaf,
            vector,
            @abstract,
            AevatarUri.Parse(uri).Name);
    }

    // ─── Index / Search ───

    [Fact]
    public async Task IndexAndSearch_FindsIndexedEntry()
    {
        var entry = MakeEntry(
            "aevatar://skills/search/SKILL.md",
            "aevatar://skills/search/",
            ContextType.Skill,
            vector: [1f, 0f, 0f]);

        await _sut.IndexAsync(entry);

        float[] query = [1f, 0f, 0f];
        var results = await _sut.SearchAsync(query, topK: 5);

        results.Should().ContainSingle();
        results[0].Uri.Should().Be(AevatarUri.Parse("aevatar://skills/search/SKILL.md"));
        results[0].Score.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public async Task Search_ReturnsOrderedByDescendingScore()
    {
        await _sut.IndexAsync(MakeEntry(
            "aevatar://resources/a.md", "aevatar://resources/",
            vector: [1f, 0f, 0f], @abstract: "A"));

        await _sut.IndexAsync(MakeEntry(
            "aevatar://resources/b.md", "aevatar://resources/",
            vector: [0.7f, 0.7f, 0f], @abstract: "B"));

        await _sut.IndexAsync(MakeEntry(
            "aevatar://resources/c.md", "aevatar://resources/",
            vector: [0f, 1f, 0f], @abstract: "C"));

        float[] query = [1f, 0f, 0f];
        var results = await _sut.SearchAsync(query, topK: 10);

        results.Should().HaveCount(3);
        results[0].Abstract.Should().Be("A");
        results[0].Score.Should().BeGreaterThan(results[1].Score);
        results[1].Score.Should().BeGreaterThan(results[2].Score);
    }

    [Fact]
    public async Task Search_RespectsTopK()
    {
        for (var i = 0; i < 10; i++)
        {
            await _sut.IndexAsync(MakeEntry(
                $"aevatar://resources/doc{i}.md", "aevatar://resources/",
                vector: [1f, 0f, 0f]));
        }

        float[] query = [1f, 0f, 0f];
        var results = await _sut.SearchAsync(query, topK: 3);

        results.Should().HaveCount(3);
    }

    // ─── Scope Filter ───

    [Fact]
    public async Task Search_ScopeFilter_FiltersCorrectly()
    {
        await _sut.IndexAsync(MakeEntry(
            "aevatar://skills/a/SKILL.md", "aevatar://skills/a/",
            ContextType.Skill, vector: [1f, 0f, 0f]));

        await _sut.IndexAsync(MakeEntry(
            "aevatar://resources/b.md", "aevatar://resources/",
            ContextType.Resource, vector: [1f, 0f, 0f]));

        float[] query = [1f, 0f, 0f];
        var scopeFilter = AevatarUri.SkillsRoot();
        var results = await _sut.SearchAsync(query, topK: 10, scopeFilter: scopeFilter);

        results.Should().ContainSingle();
        results[0].Uri.Scope.Should().Be("skills");
    }

    [Fact]
    public async Task Search_ScopeFilterWithPath_FiltersNested()
    {
        await _sut.IndexAsync(MakeEntry(
            "aevatar://user/u1/memories/prefs/style.md",
            "aevatar://user/u1/memories/prefs/",
            ContextType.Memory, vector: [1f, 0f, 0f]));

        await _sut.IndexAsync(MakeEntry(
            "aevatar://user/u2/memories/prefs/style.md",
            "aevatar://user/u2/memories/prefs/",
            ContextType.Memory, vector: [1f, 0f, 0f]));

        float[] query = [1f, 0f, 0f];
        var scopeFilter = AevatarUri.UserRoot("u1");
        var results = await _sut.SearchAsync(query, topK: 10, scopeFilter: scopeFilter);

        results.Should().ContainSingle();
        results[0].Uri.ToString().Should().Contain("u1");
    }

    // ─── SearchChildren ───

    [Fact]
    public async Task SearchChildren_FindsDirectChildren()
    {
        var parent = AevatarUri.Parse("aevatar://skills/search/");

        await _sut.IndexAsync(MakeEntry(
            "aevatar://skills/search/SKILL.md", "aevatar://skills/search/",
            vector: [1f, 0f, 0f], @abstract: "child"));

        await _sut.IndexAsync(MakeEntry(
            "aevatar://skills/other/SKILL.md", "aevatar://skills/other/",
            vector: [1f, 0f, 0f], @abstract: "other"));

        float[] query = [1f, 0f, 0f];
        var results = await _sut.SearchChildrenAsync(query, parent, topK: 10);

        results.Should().ContainSingle();
        results[0].Abstract.Should().Be("child");
    }

    // ─── IndexBatch ───

    [Fact]
    public async Task IndexBatch_IndexesMultipleEntries()
    {
        var entries = new List<VectorIndexEntry>
        {
            MakeEntry("aevatar://resources/a.md", "aevatar://resources/", vector: [1f, 0f, 0f]),
            MakeEntry("aevatar://resources/b.md", "aevatar://resources/", vector: [0f, 1f, 0f]),
            MakeEntry("aevatar://resources/c.md", "aevatar://resources/", vector: [0f, 0f, 1f]),
        };

        await _sut.IndexBatchAsync(entries);

        float[] query = [1f, 0f, 0f];
        var results = await _sut.SearchAsync(query, topK: 10);

        results.Should().HaveCount(3);
    }

    // ─── DeleteByPrefix ───

    [Fact]
    public async Task DeleteByPrefix_RemovesMatchingEntries()
    {
        await _sut.IndexAsync(MakeEntry(
            "aevatar://skills/a/SKILL.md", "aevatar://skills/a/",
            vector: [1f, 0f, 0f]));
        await _sut.IndexAsync(MakeEntry(
            "aevatar://skills/a/readme.md", "aevatar://skills/a/",
            vector: [0f, 1f, 0f]));
        await _sut.IndexAsync(MakeEntry(
            "aevatar://skills/b/SKILL.md", "aevatar://skills/b/",
            vector: [0f, 0f, 1f]));

        await _sut.DeleteByPrefixAsync(AevatarUri.Parse("aevatar://skills/a/"));

        float[] query = [1f, 1f, 1f];
        var results = await _sut.SearchAsync(query, topK: 10);

        results.Should().ContainSingle();
        results[0].Uri.ToString().Should().Contain("/b/");
    }

    // ─── Edge cases ───

    [Fact]
    public async Task Search_EmptyIndex_ReturnsEmptyList()
    {
        float[] query = [1f, 0f, 0f];
        var results = await _sut.SearchAsync(query, topK: 10);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_SameUri_OverwritesPrevious()
    {
        await _sut.IndexAsync(MakeEntry(
            "aevatar://resources/doc.md", "aevatar://resources/",
            vector: [1f, 0f, 0f], @abstract: "old"));

        await _sut.IndexAsync(MakeEntry(
            "aevatar://resources/doc.md", "aevatar://resources/",
            vector: [0f, 1f, 0f], @abstract: "new"));

        float[] query = [0f, 1f, 0f];
        var results = await _sut.SearchAsync(query, topK: 10);

        results.Should().ContainSingle();
        results[0].Abstract.Should().Be("new");
    }
}
