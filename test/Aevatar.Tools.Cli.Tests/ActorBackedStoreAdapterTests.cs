using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Type = System.Type;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aevatar.Studio.Infrastructure.Storage;

namespace Aevatar.Tools.Cli.Tests;

public sealed class ActorBackedStoreAdapterTests
{
    // ════════════════════════════════════════════════════════════
    // Fakes
    // ════════════════════════════════════════════════════════════

    private sealed class FakeAgent<TState> : IAgent<TState> where TState : class, IMessage
    {
        public FakeAgent(string id, TState state)
        {
            Id = id;
            State = state;
        }

        public string Id { get; }
        public TState State { get; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeActor : IActor
    {
        private readonly List<EventEnvelope> _received = [];

        public FakeActor(string id, IAgent? agent = null)
        {
            Id = id;
            Agent = agent ?? new FakeAgent<UserConfigGAgentState>(id, new UserConfigGAgentState());
        }

        public string Id { get; }
        public IAgent Agent { get; }
        public IReadOnlyList<EventEnvelope> ReceivedEnvelopes => _received;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _received.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>
    /// Fake runtime that supports typed agent state for read tests.
    /// </summary>
    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, FakeActor> _actors = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, FakeActor> Actors => _actors;

        public void RegisterActor(string id, IAgent agent)
        {
            _actors[id] = new FakeActor(id, agent);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            if (!_actors.ContainsKey(actorId))
                _actors[actorId] = new FakeActor(actorId);
            return Task.FromResult<IActor>(_actors[actorId]);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            if (!_actors.ContainsKey(actorId))
                _actors[actorId] = new FakeActor(actorId);
            return Task.FromResult<IActor>(_actors[actorId]);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult<IActor?>(_actors.GetValueOrDefault(id));

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeScopeResolver : IAppScopeResolver
    {
        public string? ScopeIdToReturn { get; set; }

        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            ScopeIdToReturn is not null
                ? new AppScopeContext(ScopeIdToReturn, "test")
                : null;
    }

    // UserConfigStore tests removed — ActorBackedUserConfigStore replaced by
    // IUserConfigQueryPort (projection) + IUserConfigCommandService (dispatch).
    // See ActorDispatchUserConfigCommandService tests in projection test project.

    // ════════════════════════════════════════════════════════════
    // NyxIdUserLlmPreferencesStore: delegation
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task NyxIdUserLlmPreferencesStore_ExtractsDefaultModelAndRoute()
    {
        var stubConfigStore = new StubUserConfigStore(new UserConfig(
            DefaultModel: "claude-opus",
            PreferredLlmRoute: "/api/v1/proxy/s/anthropic",
            MaxToolRounds: 7));

        var store = new ActorBackedNyxIdUserLlmPreferencesStore(stubConfigStore);

        var prefs = await store.GetAsync();

        prefs.DefaultModel.Should().Be("claude-opus");
        prefs.PreferredRoute.Should().Be("/api/v1/proxy/s/anthropic");
        prefs.MaxToolRounds.Should().Be(7);
    }

    [Fact]
    public async Task NyxIdUserLlmPreferencesStore_NormalizesGatewayRoute()
    {
        var stubConfigStore = new StubUserConfigStore(new UserConfig(
            DefaultModel: "gpt-4",
            PreferredLlmRoute: "gateway"));

        var store = new ActorBackedNyxIdUserLlmPreferencesStore(stubConfigStore);

        var prefs = await store.GetAsync();

        prefs.PreferredRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway,
            "gateway should normalize to empty string");
    }

    [Fact]
    public async Task NyxIdUserLlmPreferencesStore_DefaultConfig_ReturnsEmptyDefaults()
    {
        var stubConfigStore = new StubUserConfigStore(new UserConfig(
            DefaultModel: string.Empty));

        var store = new ActorBackedNyxIdUserLlmPreferencesStore(stubConfigStore);

        var prefs = await store.GetAsync();

        prefs.DefaultModel.Should().BeEmpty();
        prefs.PreferredRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        prefs.MaxToolRounds.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════
    // GAgentActorStore: scope isolation
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task GAgentActorStore_GetAsync_NoActor_ReturnsEmptyList()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "empty-scope" };
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, scopeResolver, logger);

        var groups = await store.GetAsync();

        groups.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════
    // GAgentActorStore: AddActorAsync command construction
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task GAgentActorStore_AddActorAsync_SendsActorRegisteredEvent()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "cmd-scope" };
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, scopeResolver, logger);

        await store.AddActorAsync("MyGAgent", "actor-123");

        var actorId = "gagent-registry-cmd-scope";
        runtime.Actors.Should().ContainKey(actorId);

        var actor = runtime.Actors[actorId];
        actor.ReceivedEnvelopes.Should().HaveCountGreaterThanOrEqualTo(1);

        // The last envelope should be the ActorRegisteredEvent command
        var envelope = actor.ReceivedEnvelopes.Last();
        envelope.Payload.Is(Aevatar.GAgents.Registry.ActorRegisteredEvent.Descriptor).Should().BeTrue();

        var evt = envelope.Payload.Unpack<Aevatar.GAgents.Registry.ActorRegisteredEvent>();
        evt.GagentType.Should().Be("MyGAgent");
        evt.ActorId.Should().Be("actor-123");
    }

    [Fact]
    public async Task GAgentActorStore_RemoveActorAsync_SendsActorUnregisteredEvent()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "cmd-scope" };
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, scopeResolver, logger);

        await store.RemoveActorAsync("MyGAgent", "actor-456");

        var actorId = "gagent-registry-cmd-scope";
        var actor = runtime.Actors[actorId];
        var envelope = actor.ReceivedEnvelopes.Last();
        envelope.Payload.Is(Aevatar.GAgents.Registry.ActorUnregisteredEvent.Descriptor).Should().BeTrue();

        var evt = envelope.Payload.Unpack<Aevatar.GAgents.Registry.ActorUnregisteredEvent>();
        evt.GagentType.Should().Be("MyGAgent");
        evt.ActorId.Should().Be("actor-456");
    }

    // ════════════════════════════════════════════════════════════
    // Helper: stub IUserConfigQueryPort for NyxId delegation tests
    // ════════════════════════════════════════════════════════════

    private sealed class StubUserConfigStore : IUserConfigQueryPort
    {
        private readonly UserConfig _config;

        public StubUserConfigStore(UserConfig config) => _config = config;

        public Task<UserConfig> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(_config);
    }

    // ════════════════════════════════════════════════════════════
    // ChatHistoryStore: command dispatch
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChatHistoryStore_SaveMessages_SendsMessagesReplacedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedChatHistoryStore>.Instance;
        var store = new ActorBackedChatHistoryStore(runtime, logger);

        var meta = new ConversationMeta(
            Id: "conv-1", Title: "Test", ServiceId: "svc",
            ServiceKind: "chat", CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow, MessageCount: 1);
        var messages = new List<StoredChatMessage>
        {
            new("msg-1", "user", "Hello", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "sent"),
        };

        await store.SaveMessagesAsync("scope-1", "conv-1", meta, messages);

        var actorId = "chat-scope-1-conv-1";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        actor.ReceivedEnvelopes.Should().HaveCount(1);
        actor.ReceivedEnvelopes[0].Payload.Is(MessagesReplacedEvent.Descriptor).Should().BeTrue();

        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<MessagesReplacedEvent>();
        evt.ScopeId.Should().Be("scope-1");
        evt.Messages.Should().HaveCount(1);
        evt.Messages[0].Role.Should().Be("user");
    }

    [Fact]
    public async Task ChatHistoryStore_DeleteConversation_SendsConversationDeletedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedChatHistoryStore>.Instance;
        var store = new ActorBackedChatHistoryStore(runtime, logger);

        await store.DeleteConversationAsync("scope-1", "conv-1");

        var actorId = "chat-scope-1-conv-1";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<ConversationDeletedEvent>();
        evt.ConversationId.Should().Be("conv-1");
        evt.ScopeId.Should().Be("scope-1");
    }

    [Fact]
    public async Task ChatHistoryStore_GetIndex_NoActor_ReturnsEmpty()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedChatHistoryStore>.Instance;
        var store = new ActorBackedChatHistoryStore(runtime, logger);

        var index = await store.GetIndexAsync("scope-1");

        index.Conversations.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatHistoryStore_GetIndex_MapsStateCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var state = new ChatHistoryIndexState();
        state.Conversations.Add(new ConversationMetaProto
        {
            Id = "conv-1",
            Title = "Test Chat",
            ServiceId = "svc",
            ServiceKind = "chat",
            CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageCount = 5,
            LlmRoute = "/api/proxy",
            LlmModel = "claude-opus",
        });
        runtime.RegisterActor("chat-index-scope-1",
            new FakeAgent<ChatHistoryIndexState>("chat-index-scope-1", state));
        var logger = NullLogger<ActorBackedChatHistoryStore>.Instance;
        var store = new ActorBackedChatHistoryStore(runtime, logger);

        var index = await store.GetIndexAsync("scope-1");

        index.Conversations.Should().HaveCount(1);
        index.Conversations[0].Id.Should().Be("conv-1");
        index.Conversations[0].Title.Should().Be("Test Chat");
        index.Conversations[0].LlmRoute.Should().Be("/api/proxy");
        index.Conversations[0].LlmModel.Should().Be("claude-opus");
        index.Conversations[0].MessageCount.Should().Be(5);
    }

    // ════════════════════════════════════════════════════════════
    // StreamingProxyParticipantStore: command dispatch + read
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ParticipantStore_AddAsync_SendsParticipantAddedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedStreamingProxyParticipantStore>.Instance;
        var store = new ActorBackedStreamingProxyParticipantStore(runtime, logger);

        await store.AddAsync("room-1", "agent-abc", "Alice");

        var actorId = "streaming-proxy-participants";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<ParticipantAddedEvent>();
        evt.RoomId.Should().Be("room-1");
        evt.AgentId.Should().Be("agent-abc");
        evt.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task ParticipantStore_RemoveRoomAsync_SendsRoomParticipantsRemovedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedStreamingProxyParticipantStore>.Instance;
        var store = new ActorBackedStreamingProxyParticipantStore(runtime, logger);

        await store.RemoveRoomAsync("room-1");

        var actorId = "streaming-proxy-participants";
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<RoomParticipantsRemovedEvent>();
        evt.RoomId.Should().Be("room-1");
    }

    [Fact]
    public async Task ParticipantStore_RemoveParticipantAsync_SendsParticipantRemovedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedStreamingProxyParticipantStore>.Instance;
        var store = new ActorBackedStreamingProxyParticipantStore(runtime, logger);

        await store.RemoveParticipantAsync("room-1", "agent-abc");

        var actorId = "streaming-proxy-participants";
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<ParticipantRemovedEvent>();
        evt.RoomId.Should().Be("room-1");
        evt.AgentId.Should().Be("agent-abc");
    }

    [Fact]
    public async Task ParticipantStore_ListAsync_NoActor_ReturnsEmpty()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedStreamingProxyParticipantStore>.Instance;
        var store = new ActorBackedStreamingProxyParticipantStore(runtime, logger);

        var participants = await store.ListAsync("room-1");

        participants.Should().BeEmpty();
    }

    [Fact]
    public async Task ParticipantStore_ListAsync_MapsStateCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var state = new StreamingProxyParticipantGAgentState();
        var room = new ParticipantList();
        var joinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        room.Participants.Add(new ParticipantEntry
        {
            AgentId = "agent-1",
            DisplayName = "Bot",
            JoinedAt = joinedAt,
        });
        state.Rooms["room-1"] = room;
        runtime.RegisterActor("streaming-proxy-participants",
            new FakeAgent<StreamingProxyParticipantGAgentState>(
                "streaming-proxy-participants", state));
        var logger = NullLogger<ActorBackedStreamingProxyParticipantStore>.Instance;
        var store = new ActorBackedStreamingProxyParticipantStore(runtime, logger);

        var participants = await store.ListAsync("room-1");

        participants.Should().HaveCount(1);
        participants[0].AgentId.Should().Be("agent-1");
        participants[0].DisplayName.Should().Be("Bot");
    }

    // ════════════════════════════════════════════════════════════
    // UserMemoryStore: command dispatch + read
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserMemoryStore_AddEntry_SendsMemoryEntryAddedEvent()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-1" };
        var logger = NullLogger<ActorBackedUserMemoryStore>.Instance;
        var store = new ActorBackedUserMemoryStore(runtime, scopeResolver, logger);

        var entry = await store.AddEntryAsync("preference", "Dark mode", "explicit");

        entry.Category.Should().Be("preference");
        entry.Content.Should().Be("Dark mode");
        entry.Source.Should().Be("explicit");
        entry.Id.Should().NotBeNullOrEmpty();

        var actorId = "user-memory-user-1";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<MemoryEntryAddedEvent>();
        evt.Entry.Content.Should().Be("Dark mode");
        evt.Entry.Category.Should().Be("preference");
    }

    [Fact]
    public async Task UserMemoryStore_GetAsync_NoActor_ReturnsEmpty()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-1" };
        var logger = NullLogger<ActorBackedUserMemoryStore>.Instance;
        var store = new ActorBackedUserMemoryStore(runtime, scopeResolver, logger);

        var doc = await store.GetAsync();

        doc.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task UserMemoryStore_GetAsync_MapsStateCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var state = new UserMemoryState();
        state.Entries.Add(new UserMemoryEntryProto
        {
            Id = "mem-1",
            Category = "context",
            Content = "Works on ML project",
            Source = "inferred",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        runtime.RegisterActor("user-memory-user-1",
            new FakeAgent<UserMemoryState>("user-memory-user-1", state));
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-1" };
        var logger = NullLogger<ActorBackedUserMemoryStore>.Instance;
        var store = new ActorBackedUserMemoryStore(runtime, scopeResolver, logger);

        var doc = await store.GetAsync();

        doc.Entries.Should().HaveCount(1);
        doc.Entries[0].Id.Should().Be("mem-1");
        doc.Entries[0].Content.Should().Be("Works on ML project");
        doc.Entries[0].Category.Should().Be("context");
    }

    [Fact]
    public async Task UserMemoryStore_NoScope_Throws()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = null };
        var logger = NullLogger<ActorBackedUserMemoryStore>.Instance;
        var store = new ActorBackedUserMemoryStore(runtime, scopeResolver, logger);

        var act = () => store.AddEntryAsync("preference", "test", "explicit");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ════════════════════════════════════════════════════════════
    // ConnectorCatalogStore: command dispatch
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectorCatalogStore_SaveCatalog_SendsCatalogSavedEvent()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedConnectorCatalogStore>.Instance;
        var store = new ActorBackedConnectorCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var catalog = new StoredConnectorCatalog(
            HomeDirectory: "test",
            FilePath: "test",
            FileExists: true,
            Connectors:
            [
                new StoredConnectorDefinition(
                    "my-conn", "http", true, 30000, 3,
                    new StoredHttpConnectorConfig("https://api.example.com", [], [], [],
                        new Dictionary<string, string>(),
                        new StoredConnectorAuthConfig("", "", "", "", "")),
                    new StoredCliConnectorConfig("", [], [], [], "",
                        new Dictionary<string, string>()),
                    new StoredMcpConnectorConfig("", "", "", [],
                        new Dictionary<string, string>(),
                        new Dictionary<string, string>(),
                        new StoredConnectorAuthConfig("", "", "", "", ""),
                        "", [], [])),
            ]);

        await store.SaveConnectorCatalogAsync(catalog);

        var actorId = "connector-catalog-scope-1";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<ConnectorCatalogSavedEvent>();
        evt.Connectors.Should().HaveCount(1);
        evt.Connectors[0].Name.Should().Be("my-conn");
        evt.Connectors[0].Type.Should().Be("http");
    }

    [Fact]
    public async Task ConnectorCatalogStore_GetCatalog_NoActor_ReturnsEmpty()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedConnectorCatalogStore>.Instance;
        var store = new ActorBackedConnectorCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var catalog = await store.GetConnectorCatalogAsync();

        catalog.FileExists.Should().BeFalse();
        catalog.Connectors.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════
    // RoleCatalogStore: command dispatch + workspace sync
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task RoleCatalogStore_SaveCatalog_SendsCatalogSavedEvent()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedRoleCatalogStore>.Instance;
        var store = new ActorBackedRoleCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var catalog = new StoredRoleCatalog(
            HomeDirectory: "test",
            FilePath: "test",
            FileExists: true,
            Roles:
            [
                new StoredRoleDefinition("role-1", "Assistant", "You are helpful",
                    "anthropic", "claude-opus", []),
            ]);

        await store.SaveRoleCatalogAsync(catalog);

        var actorId = "role-catalog-scope-1";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        var evt = actor.ReceivedEnvelopes[0].Payload.Unpack<RoleCatalogSavedEvent>();
        evt.Roles.Should().HaveCount(1);
        evt.Roles[0].Name.Should().Be("Assistant");
    }

    [Fact]
    public async Task RoleCatalogStore_DeleteDraft_SyncsToWorkspace()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedRoleCatalogStore>.Instance;
        var store = new ActorBackedRoleCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        await store.DeleteRoleDraftAsync();

        workspaceStore.RoleDraftDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task RoleCatalogStore_GetCatalog_NoActor_ReturnsEmpty()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedRoleCatalogStore>.Instance;
        var store = new ActorBackedRoleCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var catalog = await store.GetRoleCatalogAsync();

        catalog.FileExists.Should().BeFalse();
        catalog.Roles.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════
    // ChatHistoryStore: GetMessagesAsync read mapping
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChatHistoryStore_GetMessages_MapsStateCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var state = new ChatConversationState();
        state.Messages.Add(new StoredChatMessageProto
        {
            Id = "msg-1",
            Role = "user",
            Content = "Hello",
            Timestamp = 1700000000000,
            Status = "sent",
            Error = "",
            Thinking = "reasoning...",
        });
        state.Messages.Add(new StoredChatMessageProto
        {
            Id = "msg-2",
            Role = "assistant",
            Content = "Hi there!",
            Timestamp = 1700000001000,
            Status = "sent",
        });
        runtime.RegisterActor("chat-scope-1-conv-1",
            new FakeAgent<ChatConversationState>("chat-scope-1-conv-1", state));
        var logger = NullLogger<ActorBackedChatHistoryStore>.Instance;
        var store = new ActorBackedChatHistoryStore(runtime, logger);

        var messages = await store.GetMessagesAsync("scope-1", "conv-1");

        messages.Should().HaveCount(2);
        messages[0].Id.Should().Be("msg-1");
        messages[0].Role.Should().Be("user");
        messages[0].Content.Should().Be("Hello");
        messages[0].Thinking.Should().Be("reasoning...");
        messages[1].Id.Should().Be("msg-2");
        messages[1].Role.Should().Be("assistant");
        messages[1].Error.Should().BeNull("empty proto string maps to null");
    }

    [Fact]
    public async Task ChatHistoryStore_GetMessages_NoActor_ReturnsEmpty()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedChatHistoryStore>.Instance;
        var store = new ActorBackedChatHistoryStore(runtime, logger);

        var messages = await store.GetMessagesAsync("scope-1", "conv-1");

        messages.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════
    // RoleCatalogStore: SaveDraft workspace sync + read mapping
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task RoleCatalogStore_SaveDraft_SyncsToWorkspace()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedRoleCatalogStore>.Instance;
        var store = new ActorBackedRoleCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var draft = new StoredRoleDraft(
            HomeDirectory: "test",
            FilePath: "test/draft",
            FileExists: true,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            Draft: new StoredRoleDefinition("r1", "My Role", "prompt",
                "anthropic", "claude-opus", []));

        await store.SaveRoleDraftAsync(draft);

        workspaceStore.LastSavedRoleDraft.Should().NotBeNull();
        workspaceStore.LastSavedRoleDraft!.Draft!.Name.Should().Be("My Role");
    }

    [Fact]
    public async Task RoleCatalogStore_DeleteDraft_SendsEventAndSyncsWorkspace()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedRoleCatalogStore>.Instance;
        var store = new ActorBackedRoleCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        await store.DeleteRoleDraftAsync();

        var actorId = "role-catalog-scope-1";
        runtime.Actors.Should().ContainKey(actorId);
        var actor = runtime.Actors[actorId];
        actor.ReceivedEnvelopes.Should().HaveCount(1);
        actor.ReceivedEnvelopes[0].Payload.Is(RoleDraftDeletedEvent.Descriptor).Should().BeTrue();
        workspaceStore.RoleDraftDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task RoleCatalogStore_GetCatalog_MapsStateCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var state = new RoleCatalogState();
        state.Roles.Add(new RoleDefinitionEntry
        {
            Id = "role-1",
            Name = "Assistant",
            SystemPrompt = "You are helpful",
            Provider = "anthropic",
            Model = "claude-opus",
        });
        runtime.RegisterActor("role-catalog-scope-1",
            new FakeAgent<RoleCatalogState>("role-catalog-scope-1", state));
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedRoleCatalogStore>.Instance;
        var store = new ActorBackedRoleCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var catalog = await store.GetRoleCatalogAsync();

        catalog.FileExists.Should().BeTrue();
        catalog.Roles.Should().HaveCount(1);
        catalog.Roles[0].Id.Should().Be("role-1");
        catalog.Roles[0].Name.Should().Be("Assistant");
        catalog.Roles[0].Provider.Should().Be("anthropic");
    }

    // ════════════════════════════════════════════════════════════
    // ConnectorCatalogStore: read mapping
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectorCatalogStore_GetCatalog_MapsStateCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var state = new ConnectorCatalogState();
        state.Connectors.Add(new ConnectorDefinitionEntry
        {
            Name = "web-search",
            Type = "http",
            Enabled = true,
            TimeoutMs = 30000,
            Retry = 3,
            Http = new HttpConnectorConfigEntry
            {
                BaseUrl = "https://api.search.example.com",
            },
        });
        runtime.RegisterActor("connector-catalog-scope-1",
            new FakeAgent<ConnectorCatalogState>("connector-catalog-scope-1", state));
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "scope-1" };
        var workspaceStore = new StubWorkspaceStore();
        var logger = NullLogger<ActorBackedConnectorCatalogStore>.Instance;
        var store = new ActorBackedConnectorCatalogStore(
            runtime, scopeResolver, workspaceStore, logger);

        var catalog = await store.GetConnectorCatalogAsync();

        catalog.FileExists.Should().BeTrue();
        catalog.Connectors.Should().HaveCount(1);
        catalog.Connectors[0].Name.Should().Be("web-search");
        catalog.Connectors[0].Type.Should().Be("http");
        catalog.Connectors[0].Http.BaseUrl.Should().Be("https://api.search.example.com");
        catalog.Connectors[0].TimeoutMs.Should().Be(30000);
    }

    // ════════════════════════════════════════════════════════════
    // Scope isolation: two scopes don't share actors
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task GAgentActorStore_DifferentScopes_UseDifferentActors()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var scopeA = new FakeScopeResolver { ScopeIdToReturn = "scope-a" };
        var storeA = new ActorBackedGAgentActorStore(runtime, scopeA, logger);
        await storeA.AddActorAsync("MyAgent", "actor-1");

        var scopeB = new FakeScopeResolver { ScopeIdToReturn = "scope-b" };
        var storeB = new ActorBackedGAgentActorStore(runtime, scopeB, logger);
        await storeB.AddActorAsync("MyAgent", "actor-2");

        runtime.Actors.Should().ContainKey("gagent-registry-scope-a");
        runtime.Actors.Should().ContainKey("gagent-registry-scope-b");
        runtime.Actors["gagent-registry-scope-a"].ReceivedEnvelopes.Should().HaveCount(1);
        runtime.Actors["gagent-registry-scope-b"].ReceivedEnvelopes.Should().HaveCount(1);
    }

    // ════════════════════════════════════════════════════════════
    // Helper: stub IStudioWorkspaceStore for catalog tests
    // ════════════════════════════════════════════════════════════

    private sealed class StubWorkspaceStore : IStudioWorkspaceStore
    {
        public bool RoleDraftDeleted { get; private set; }
        public bool ConnectorDraftDeleted { get; private set; }
        public StoredRoleDraft? LastSavedRoleDraft { get; private set; }
        public StoredConnectorDraft? LastSavedConnectorDraft { get; private set; }

        public Task<StudioWorkspaceSettings> GetSettingsAsync(CancellationToken ct = default) =>
            Task.FromResult(new StudioWorkspaceSettings("", [], "", ""));
        public Task SaveSettingsAsync(StudioWorkspaceSettings settings, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<StoredWorkflowFile>> ListWorkflowFilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredWorkflowFile>>([]);
        public Task<StoredWorkflowFile?> GetWorkflowFileAsync(string workflowId, CancellationToken ct = default) =>
            Task.FromResult<StoredWorkflowFile?>(null);
        public Task<StoredWorkflowFile> SaveWorkflowFileAsync(StoredWorkflowFile f, CancellationToken ct = default) =>
            Task.FromResult(f);
        public Task<IReadOnlyList<StoredExecutionRecord>> ListExecutionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredExecutionRecord>>([]);
        public Task<StoredExecutionRecord?> GetExecutionAsync(string executionId, CancellationToken ct = default) =>
            Task.FromResult<StoredExecutionRecord?>(null);
        public Task<StoredExecutionRecord> SaveExecutionAsync(StoredExecutionRecord r, CancellationToken ct = default) =>
            Task.FromResult(r);
        public Task<StoredConnectorCatalog> GetConnectorCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(new StoredConnectorCatalog("", "", false, []));
        public Task<StoredConnectorCatalog> SaveConnectorCatalogAsync(StoredConnectorCatalog c, CancellationToken ct = default) =>
            Task.FromResult(c);
        public Task<StoredConnectorDraft> GetConnectorDraftAsync(CancellationToken ct = default) =>
            Task.FromResult(new StoredConnectorDraft("", "", false, null, null));
        public Task<StoredConnectorDraft> SaveConnectorDraftAsync(StoredConnectorDraft d, CancellationToken ct = default)
        {
            LastSavedConnectorDraft = d;
            return Task.FromResult(d);
        }
        public Task DeleteConnectorDraftAsync(CancellationToken ct = default)
        {
            ConnectorDraftDeleted = true;
            return Task.CompletedTask;
        }
        public Task<StoredRoleCatalog> GetRoleCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(new StoredRoleCatalog("", "", false, []));
        public Task<StoredRoleCatalog> SaveRoleCatalogAsync(StoredRoleCatalog c, CancellationToken ct = default) =>
            Task.FromResult(c);
        public Task<StoredRoleDraft> GetRoleDraftAsync(CancellationToken ct = default) =>
            Task.FromResult(new StoredRoleDraft("", "", false, null, null));
        public Task<StoredRoleDraft> SaveRoleDraftAsync(StoredRoleDraft d, CancellationToken ct = default)
        {
            LastSavedRoleDraft = d;
            return Task.FromResult(d);
        }
        public Task DeleteRoleDraftAsync(CancellationToken ct = default)
        {
            RoleDraftDeleted = true;
            return Task.CompletedTask;
        }
    }
}
