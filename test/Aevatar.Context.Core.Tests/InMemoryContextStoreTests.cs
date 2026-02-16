using Aevatar.Context.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.Context.Core.Tests;

public sealed class InMemoryContextStoreTests
{
    private readonly InMemoryContextStore _store = new();

    // ─── Write / Read ───

    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var uri = AevatarUri.Parse("aevatar://skills/test-skill/SKILL.md");
        await _store.WriteAsync(uri, "# Test Skill\nA test skill.");

        var content = await _store.ReadAsync(uri);
        content.Should().Be("# Test Skill\nA test skill.");
    }

    [Fact]
    public async Task Read_NonExistent_Throws()
    {
        var uri = AevatarUri.Parse("aevatar://skills/missing/SKILL.md");
        var act = async () => await _store.ReadAsync(uri);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ─── Exists ───

    [Fact]
    public async Task Exists_AfterWrite_ReturnsTrue()
    {
        var uri = AevatarUri.Parse("aevatar://skills/test/SKILL.md");
        await _store.WriteAsync(uri, "content");

        (await _store.ExistsAsync(uri)).Should().BeTrue();
    }

    [Fact]
    public async Task Exists_NonExistent_ReturnsFalse()
    {
        var uri = AevatarUri.Parse("aevatar://skills/missing/SKILL.md");
        (await _store.ExistsAsync(uri)).Should().BeFalse();
    }

    [Fact]
    public async Task Exists_DirectoryWithChildren_ReturnsTrue()
    {
        var fileUri = AevatarUri.Parse("aevatar://skills/test/SKILL.md");
        await _store.WriteAsync(fileUri, "content");

        var dirUri = AevatarUri.Parse("aevatar://skills/test/");
        (await _store.ExistsAsync(dirUri)).Should().BeTrue();
    }

    // ─── Delete ───

    [Fact]
    public async Task Delete_File_RemovesFile()
    {
        var uri = AevatarUri.Parse("aevatar://resources/test.md");
        await _store.WriteAsync(uri, "data");
        await _store.DeleteAsync(uri);

        (await _store.ExistsAsync(uri)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_DirectoryRecursive_RemovesAll()
    {
        var dir = AevatarUri.Parse("aevatar://resources/project/");
        var f1 = AevatarUri.Parse("aevatar://resources/project/a.md");
        var f2 = AevatarUri.Parse("aevatar://resources/project/sub/b.md");
        await _store.WriteAsync(f1, "a");
        await _store.WriteAsync(f2, "b");

        await _store.DeleteAsync(dir, recursive: true);

        (await _store.ExistsAsync(f1)).Should().BeFalse();
        (await _store.ExistsAsync(f2)).Should().BeFalse();
    }

    // ─── List ───

    [Fact]
    public async Task List_ReturnsDirectChildrenOnly()
    {
        await _store.WriteAsync(AevatarUri.Parse("aevatar://skills/a/SKILL.md"), "skill a");
        await _store.WriteAsync(AevatarUri.Parse("aevatar://skills/b/SKILL.md"), "skill b");
        await _store.WriteAsync(AevatarUri.Parse("aevatar://skills/b/sub/deep.md"), "deep");

        var root = AevatarUri.Parse("aevatar://skills/");
        var entries = await _store.ListAsync(root);

        entries.Should().HaveCount(2);
        entries.Select(e => e.Name).Should().Contain("a").And.Contain("b");
        entries.Should().OnlyContain(e => e.IsDirectory);
    }

    [Fact]
    public async Task List_SkipsHiddenFiles()
    {
        var dir = AevatarUri.Parse("aevatar://skills/test/");
        await _store.WriteAsync(dir.Join("SKILL.md"), "content");
        await _store.WriteAsync(dir.Join(".abstract.md"), "abstract");

        var entries = await _store.ListAsync(dir);
        entries.Should().ContainSingle(e => e.Name == "SKILL.md");
    }

    // ─── Glob ───

    [Fact]
    public async Task Glob_FindsMatchingFiles()
    {
        await _store.WriteAsync(AevatarUri.Parse("aevatar://skills/a/SKILL.md"), "a");
        await _store.WriteAsync(AevatarUri.Parse("aevatar://skills/b/SKILL.md"), "b");
        await _store.WriteAsync(AevatarUri.Parse("aevatar://skills/c/readme.txt"), "c");

        var root = AevatarUri.Parse("aevatar://skills/");
        var results = await _store.GlobAsync("**/*.md", root);

        results.Should().HaveCount(2);
    }

    // ─── CreateDirectory ───

    [Fact]
    public async Task CreateDirectory_MakesDirectoryExist()
    {
        var dir = AevatarUri.Parse("aevatar://agent/a1/memories/cases/");
        await _store.CreateDirectoryAsync(dir);

        (await _store.ExistsAsync(dir)).Should().BeTrue();
    }

    // ─── L0/L1 ───

    [Fact]
    public async Task GetAbstract_ReturnsAbstractFile()
    {
        var dir = AevatarUri.Parse("aevatar://skills/test/");
        var abstractUri = dir.Join(".abstract.md");
        await _store.WriteAsync(abstractUri, "A test skill for searching.");

        var result = await _store.GetAbstractAsync(dir);
        result.Should().Be("A test skill for searching.");
    }

    [Fact]
    public async Task GetOverview_ReturnsOverviewFile()
    {
        var dir = AevatarUri.Parse("aevatar://skills/test/");
        var overviewUri = dir.Join(".overview.md");
        await _store.WriteAsync(overviewUri, "# Test Skill\nDetailed overview.");

        var result = await _store.GetOverviewAsync(dir);
        result.Should().Be("# Test Skill\nDetailed overview.");
    }

    [Fact]
    public async Task GetAbstract_MissingFile_ReturnsNull()
    {
        var dir = AevatarUri.Parse("aevatar://skills/no-abstract/");
        await _store.CreateDirectoryAsync(dir);

        var result = await _store.GetAbstractAsync(dir);
        result.Should().BeNull();
    }
}
