using Aevatar.Context.Abstractions;
using Aevatar.Context.Core;
using FluentAssertions;
using Xunit;

namespace Aevatar.Context.Memory.Tests;

public sealed class MemoryWriterTests
{
    private readonly InMemoryContextStore _store = new();
    private readonly MemoryWriter _sut;

    public MemoryWriterTests()
    {
        _sut = new MemoryWriter(_store);
    }

    // ─── Create ───

    [Fact]
    public async Task Write_Create_WritesNewFile()
    {
        var targetScope = AevatarUri.Create("user", "u1/memories/preferences");
        var candidate = new CandidateMemory(MemoryCategory.Preferences, "Prefers dark mode", "session");
        var result = new DeduplicationResult(candidate, DeduplicationDecision.Create, targetScope, null);

        await _sut.WriteAsync([result]);

        var entries = await _store.ListAsync(targetScope);
        entries.Should().ContainSingle();
        var content = await _store.ReadAsync(entries[0].Uri);
        content.Should().Be("Prefers dark mode");
    }

    [Fact]
    public async Task Write_Create_GeneratesTimestampedFileName()
    {
        var targetScope = AevatarUri.Create("user", "u1/memories/preferences");
        var candidate = new CandidateMemory(MemoryCategory.Preferences, "Prefers Vim editor", "session");
        var result = new DeduplicationResult(candidate, DeduplicationDecision.Create, targetScope, null);

        await _sut.WriteAsync([result]);

        var entries = await _store.ListAsync(targetScope);
        entries.Should().ContainSingle();
        entries[0].Name.Should().EndWith(".md");
        entries[0].Name.Should().Contain("Prefers-Vim-editor");
    }

    // ─── Update ───

    [Fact]
    public async Task Write_Update_OverwritesExistingFile()
    {
        var existingUri = AevatarUri.Parse("aevatar://user/u1/memories/preferences/theme.md");
        await _store.WriteAsync(existingUri, "Old theme preference.");

        var candidate = new CandidateMemory(MemoryCategory.Preferences, "New theme: dark mode", "session");
        var result = new DeduplicationResult(
            candidate, DeduplicationDecision.Update,
            AevatarUri.Create("user", "u1/memories/preferences"), existingUri);

        await _sut.WriteAsync([result]);

        var content = await _store.ReadAsync(existingUri);
        content.Should().Be("New theme: dark mode");
    }

    // ─── Merge ───

    [Fact]
    public async Task Write_Merge_AppendsToExistingContent()
    {
        var existingUri = AevatarUri.Parse("aevatar://user/u1/memories/entities/project.md");
        await _store.WriteAsync(existingUri, "Project Alpha: backend service.");

        var candidate = new CandidateMemory(MemoryCategory.Entities, "Project Alpha: also has frontend.", "session");
        var result = new DeduplicationResult(
            candidate, DeduplicationDecision.Merge,
            AevatarUri.Create("user", "u1/memories/entities"), existingUri);

        await _sut.WriteAsync([result]);

        var content = await _store.ReadAsync(existingUri);
        content.Should().Contain("Project Alpha: backend service.");
        content.Should().Contain("---");
        content.Should().Contain("Project Alpha: also has frontend.");
    }

    // ─── Skip ───

    [Fact]
    public async Task Write_Skip_DoesNotModifyStore()
    {
        var existingUri = AevatarUri.Parse("aevatar://user/u1/memories/events/event.md");
        await _store.WriteAsync(existingUri, "Original event.");

        var candidate = new CandidateMemory(MemoryCategory.Events, "Duplicate event", "session");
        var result = new DeduplicationResult(
            candidate, DeduplicationDecision.Skip,
            AevatarUri.Create("user", "u1/memories/events"), existingUri);

        await _sut.WriteAsync([result]);

        var content = await _store.ReadAsync(existingUri);
        content.Should().Be("Original event.");
    }

    // ─── Mixed results ───

    [Fact]
    public async Task Write_MixedDecisions_ProcessesCorrectly()
    {
        var prefUri = AevatarUri.Parse("aevatar://user/u1/memories/preferences/existing.md");
        await _store.WriteAsync(prefUri, "Existing preference.");

        var results = new List<DeduplicationResult>
        {
            new(
                new CandidateMemory(MemoryCategory.Profile, "Name: Alice", "s"),
                DeduplicationDecision.Create,
                AevatarUri.Create("user", "u1/memories"),
                null),
            new(
                new CandidateMemory(MemoryCategory.Preferences, "Updated pref", "s"),
                DeduplicationDecision.Update,
                AevatarUri.Create("user", "u1/memories/preferences"),
                prefUri),
            new(
                new CandidateMemory(MemoryCategory.Events, "Skipped", "s"),
                DeduplicationDecision.Skip,
                AevatarUri.Create("user", "u1/memories/events"),
                AevatarUri.Parse("aevatar://user/u1/memories/events/skip.md")),
        };

        await _sut.WriteAsync(results);

        var profileEntries = await _store.ListAsync(AevatarUri.Create("user", "u1/memories"));
        profileEntries.Should().NotBeEmpty();

        var updatedContent = await _store.ReadAsync(prefUri);
        updatedContent.Should().Be("Updated pref");
    }

    // ─── Empty input ───

    [Fact]
    public async Task Write_EmptyList_DoesNothing()
    {
        var act = async () => await _sut.WriteAsync([]);

        await act.Should().NotThrowAsync();
    }

    // ─── File name generation edge cases ───

    [Fact]
    public async Task Write_Create_HandlesSpecialCharsInContent()
    {
        var targetScope = AevatarUri.Create("user", "u1/memories/preferences");
        var candidate = new CandidateMemory(MemoryCategory.Preferences, "a/b/c special!@#$", "session");
        var result = new DeduplicationResult(candidate, DeduplicationDecision.Create, targetScope, null);

        await _sut.WriteAsync([result]);

        var entries = await _store.ListAsync(targetScope);
        entries.Should().ContainSingle();
        entries[0].Name.Should().NotContain("/");
        entries[0].Name.Should().NotContain("!");
    }

    [Fact]
    public async Task Write_Create_HandlesEmptyContentSlug()
    {
        var targetScope = AevatarUri.Create("user", "u1/memories/preferences");
        var candidate = new CandidateMemory(MemoryCategory.Preferences, "!@#$%", "session");
        var result = new DeduplicationResult(candidate, DeduplicationDecision.Create, targetScope, null);

        await _sut.WriteAsync([result]);

        var entries = await _store.ListAsync(targetScope);
        entries.Should().ContainSingle();
        entries[0].Name.Should().Contain("memory");
    }
}
