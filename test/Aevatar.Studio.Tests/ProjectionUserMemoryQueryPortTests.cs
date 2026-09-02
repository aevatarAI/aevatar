using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Projection.QueryPorts;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http;
using ActorUserMemoryCategory = Aevatar.GAgents.UserMemory.UserMemoryCategory;
using ActorUserMemoryEntry = Aevatar.GAgents.UserMemory.UserMemoryEntryProto;
using ActorUserMemorySource = Aevatar.GAgents.UserMemory.UserMemorySource;
using ActorUserMemoryState = Aevatar.GAgents.UserMemory.UserMemoryState;

namespace Aevatar.Studio.Tests;

public sealed class ProjectionUserMemoryQueryPortTests
{
    [Fact]
    public async Task GetAsync_ShouldReadOwnerCurrentStateWithoutConflatingConversationOrSessionIdentity()
    {
        const string conversationId = "conversation-alpha";
        const string sessionId = "session-beta";
        var owner = UserMemoryOwnerKey.ForScope("user-gamma");
        var state = new ActorUserMemoryState
        {
            Entries =
            {
                new ActorUserMemoryEntry
                {
                    Id = "memory-delta",
                    Category = ActorUserMemoryCategory.Preference,
                    Content = "Prefer concise status updates",
                    Source = ActorUserMemorySource.Explicit,
                    CreatedAtMs = 1_750_000_000_000,
                    UpdatedAtMs = 1_750_000_001_000,
                },
            },
        };
        var reader = new RecordingDocumentReader
        {
            Document = new UserMemoryCurrentStateDocument
            {
                Id = "user-memory-user-gamma",
                ActorId = "user-memory-user-gamma",
                StateVersion = 17,
                StateRoot = Any.Pack(state),
            },
        };
        var port = new ProjectionUserMemoryQueryPort(new StubScopeResolver("user-gamma"), reader);

        var snapshot = await port.GetAsync();

        reader.Keys.Should().ContainSingle("user-memory-user-gamma");
        reader.Keys.Should().NotContain(conversationId);
        reader.Keys.Should().NotContain(sessionId);
        snapshot.Owner.Should().Be(owner);
        snapshot.StateVersion.Should().Be(17);
        var entry = snapshot.Entries.Should().ContainSingle().Subject;
        entry.Id.Should().Be("memory-delta");
        entry.Category.Should().Be(UserMemoryCategory.Preference);
        entry.Source.Should().Be(UserMemorySource.Explicit);
        entry.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000));
    }

    [Fact]
    public async Task GetAsync_WhenProjectionIsMissing_ShouldReturnEmptyVersionedSnapshot()
    {
        var owner = UserMemoryOwnerKey.ForScope("user-gamma");
        var reader = new RecordingDocumentReader();
        var port = new ProjectionUserMemoryQueryPort(new StubScopeResolver("user-gamma"), reader);

        var snapshot = await port.GetAsync();

        snapshot.Should().Be(UserMemorySnapshot.Empty(owner));
        reader.Keys.Should().ContainSingle("user-memory-user-gamma");
    }

    [Fact]
    public async Task GetAsync_WhenProjectionContainsUnreadableTimestamp_ShouldSkipInvalidEntry()
    {
        var reader = new RecordingDocumentReader
        {
            Document = new UserMemoryCurrentStateDocument
            {
                Id = "user-memory-user-gamma",
                ActorId = "user-memory-user-gamma",
                StateVersion = 18,
                StateRoot = Any.Pack(new ActorUserMemoryState
                {
                    Entries =
                    {
                        new ActorUserMemoryEntry
                        {
                            Id = "memory-invalid",
                            Category = ActorUserMemoryCategory.Context,
                            Content = "Unreadable legacy timestamp",
                            Source = ActorUserMemorySource.Inferred,
                            CreatedAtMs = 0,
                            UpdatedAtMs = long.MaxValue,
                        },
                    },
                }),
            },
        };
        var port = new ProjectionUserMemoryQueryPort(new StubScopeResolver("user-gamma"), reader);

        var snapshot = await port.GetAsync();

        snapshot.StateVersion.Should().Be(18);
        snapshot.Entries.Should().BeEmpty();
    }

    private sealed class RecordingDocumentReader
        : IProjectionDocumentReader<UserMemoryCurrentStateDocument, string>
    {
        public UserMemoryCurrentStateDocument? Document { get; init; }
        public List<string> Keys { get; } = [];

        public Task<UserMemoryCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Keys.Add(key);
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<UserMemoryCurrentStateDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            Task.FromResult(ProjectionDocumentQueryResult<UserMemoryCurrentStateDocument>.Empty);
    }

    private sealed class StubScopeResolver(string scopeId) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            new(scopeId, "test");

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => true;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }
}
