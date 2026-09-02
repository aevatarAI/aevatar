using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed class UserMemoryPromptContextProviderTests
{
    [Fact]
    public async Task BuildAsync_ShouldDeriveBoundedPromptFromAuthenticatedUserMemoryOwner()
    {
        const string conversationId = "conversation-alpha";
        const string sessionId = "session-beta";
        var query = new RecordingQueryPort(
        [
            Entry("memory-context", UserMemoryCategory.Context, "Works in Singapore", 1),
            Entry("memory-new", UserMemoryCategory.Preference, "Prefer direct answers", 3),
            Entry("memory-old", UserMemoryCategory.Preference, "Prefer examples", 2),
        ]);
        var provider = new UserMemoryPromptContextProvider(
            query,
            NullLogger<UserMemoryPromptContextProvider>.Instance);

        var prompt = await provider.BuildAsync(160);

        query.Calls.Should().Be(1);
        query.Owner.ScopeId.Should().NotBe(conversationId);
        query.Owner.ScopeId.Should().NotBe(sessionId);
        prompt.Should().StartWith("<user-memory>\n## Preferences\n");
        prompt.Should().Contain("- Prefer direct answers\n- Prefer examples\n");
        prompt.Should().Contain("## Context\n- Works in Singapore\n");
        prompt.Should().EndWith("</user-memory>");
        prompt.Length.Should().BeLessThanOrEqualTo(160);
    }

    [Fact]
    public async Task BuildAsync_WhenReadModelFails_ShouldReturnEmptyContext()
    {
        var provider = new UserMemoryPromptContextProvider(
            new ThrowingQueryPort(),
            NullLogger<UserMemoryPromptContextProvider>.Instance);

        var prompt = await provider.BuildAsync(2_000);

        prompt.Should().BeEmpty();
    }

    private static UserMemoryEntrySnapshot Entry(
        string id,
        UserMemoryCategory category,
        string content,
        long updatedAtSeconds) =>
        new(
            id,
            category,
            content,
            UserMemorySource.Explicit,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(updatedAtSeconds));

    private sealed class RecordingQueryPort(IReadOnlyList<UserMemoryEntrySnapshot> entries)
        : IUserMemoryQueryPort
    {
        public UserMemoryOwnerKey Owner { get; } = UserMemoryOwnerKey.ForScope("user-gamma");
        public int Calls { get; private set; }

        public Task<UserMemorySnapshot> GetAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new UserMemorySnapshot(Owner, 9, entries));
        }
    }

    private sealed class ThrowingQueryPort : IUserMemoryQueryPort
    {
        public Task<UserMemorySnapshot> GetAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("projection unavailable");
    }
}
