using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.UserConfig;
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

    private sealed class FakeAgent : IAgent<UserConfigGAgentState>
    {
        public FakeAgent(string id, UserConfigGAgentState state)
        {
            Id = id;
            State = state;
        }

        public string Id { get; }
        public UserConfigGAgentState State { get; }

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
            Agent = agent ?? new FakeAgent(id, new UserConfigGAgentState());
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

    // ════════════════════════════════════════════════════════════
    // UserConfigStore: defaults when actor does not exist
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_GetAsync_NoActor_ReturnsDefaults()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "test-user" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        // No actor exists, returns defaults
        var config = await store.GetAsync();

        config.DefaultModel.Should().BeEmpty();
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        config.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.LocalMode);
        config.LocalRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
        config.RemoteRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);
        config.MaxToolRounds.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════
    // UserConfigStore: state mapping
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_GetAsync_WithState_MapsFieldsCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-42" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        // Register write actor with state
        var state = new UserConfigGAgentState
        {
            DefaultModel = "gpt-4",
            PreferredLlmRoute = "/api/v1/proxy/s/custom",
            RuntimeMode = "remote",
            LocalRuntimeBaseUrl = "http://localhost:9090",
            RemoteRuntimeBaseUrl = "https://remote.example.com",
            MaxToolRounds = 5,
        };
        runtime.RegisterActor("user-config-user-42", new FakeAgent("user-config-user-42", state));

        var store = new ActorBackedUserConfigStore(
            runtime, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        var config = await store.GetAsync();

        config.DefaultModel.Should().Be("gpt-4");
        config.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/custom");
        config.RuntimeMode.Should().Be("remote");
        config.LocalRuntimeBaseUrl.Should().Be("http://localhost:9090");
        config.RemoteRuntimeBaseUrl.Should().Be("https://remote.example.com");
        config.MaxToolRounds.Should().Be(5);
    }

    // ════════════════════════════════════════════════════════════
    // UserConfigStore: empty string fields apply defaults
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_GetAsync_EmptyStringFields_ApplyDefaults()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-empty" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        // Register write actor with mostly-empty state
        var state = new UserConfigGAgentState { DefaultModel = "claude-3" };
        runtime.RegisterActor("user-config-user-empty", new FakeAgent("user-config-user-empty", state));

        var store = new ActorBackedUserConfigStore(
            runtime, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        var config = await store.GetAsync();

        config.DefaultModel.Should().Be("claude-3");
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway,
            "empty PreferredLlmRoute should fall back to Gateway default");
        config.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.LocalMode,
            "empty RuntimeMode should fall back to local");
        config.LocalRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl,
            "empty LocalRuntimeBaseUrl should fall back to default");
        config.RemoteRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl,
            "empty RemoteRuntimeBaseUrl should fall back to default");
    }

    // ════════════════════════════════════════════════════════════
    // UserConfigStore: SaveAsync sends UserConfigUpdatedEvent
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_SaveAsync_SendsUserConfigUpdatedEvent()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "save-scope" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        var config = new UserConfig(
            DefaultModel: "gpt-4-turbo",
            PreferredLlmRoute: "/api/v1/proxy/s/openai",
            RuntimeMode: "remote",
            LocalRuntimeBaseUrl: "http://127.0.0.1:8080",
            RemoteRuntimeBaseUrl: "https://api.example.com",
            MaxToolRounds: 10);

        await store.SaveAsync(config);

        var actorId = "user-config-save-scope";
        runtime.Actors.Should().ContainKey(actorId);

        var actor = runtime.Actors[actorId];
        actor.ReceivedEnvelopes.Should().HaveCount(1);

        var envelope = actor.ReceivedEnvelopes[0];
        envelope.Payload.Should().NotBeNull();
        envelope.Payload.Is(UserConfigUpdatedEvent.Descriptor).Should().BeTrue();

        var evt = envelope.Payload.Unpack<UserConfigUpdatedEvent>();
        evt.DefaultModel.Should().Be("gpt-4-turbo");
        evt.MaxToolRounds.Should().Be(10);
    }

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
    // Envelope structure verification
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveAsync_EnvelopeContainsIdAndTimestamp()
    {
        var runtime = new FakeActorRuntime();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "ts-scope" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        var beforeUtc = DateTimeOffset.UtcNow;

        await store.SaveAsync(new UserConfig(DefaultModel: "model"));

        var afterUtc = DateTimeOffset.UtcNow;
        var envelope = runtime.Actors["user-config-ts-scope"].ReceivedEnvelopes[0];

        envelope.Id.Should().NotBeNullOrWhiteSpace("envelope must have a unique ID");
        envelope.Id.Length.Should().Be(32, "ID should be a Guid without dashes");

        var ts = envelope.Timestamp.ToDateTimeOffset();
        ts.Should().BeOnOrAfter(beforeUtc).And.BeOnOrBefore(afterUtc);
    }

    // ════════════════════════════════════════════════════════════
    // Helper: stub IUserConfigStore for NyxId delegation tests
    // ════════════════════════════════════════════════════════════

    private sealed class StubUserConfigStore : IUserConfigStore
    {
        private readonly UserConfig _config;

        public StubUserConfigStore(UserConfig config) => _config = config;

        public Task<UserConfig> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_config);

        public Task SaveAsync(UserConfig config, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
