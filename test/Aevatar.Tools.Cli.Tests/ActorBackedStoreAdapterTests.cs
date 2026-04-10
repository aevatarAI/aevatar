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
}
