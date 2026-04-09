using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.ScriptStorage;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.WorkflowStorage;
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

    private sealed class FakeActor : IActor
    {
        private readonly List<EventEnvelope> _received = [];

        public FakeActor(string id) => Id = id;

        public string Id { get; }
        public IAgent Agent => throw new NotSupportedException();
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
    /// Fake runtime that supports an optional callback when an actor is created.
    /// This lets tests wire up auto-delivery of readmodel snapshots.
    /// </summary>
    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, FakeActor> _actors = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, FakeActor> Actors => _actors;

        /// <summary>
        /// Optional callback invoked after an actor is created.
        /// Used to simulate the readmodel actor's OnActivateAsync publishing a snapshot.
        /// </summary>
        public Func<string, Task>? OnActorCreated { get; set; }

        public async Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new FakeActor(actorId);
            _actors[actorId] = actor;
            if (OnActorCreated is not null)
                await OnActorCreated(actorId);
            return actor;
        }

        public async Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? Guid.NewGuid().ToString("N");
            var actor = new FakeActor(actorId);
            _actors[actorId] = actor;
            if (OnActorCreated is not null)
                await OnActorCreated(actorId);
            return actor;
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

    private sealed class FakeSubscriptionHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Fake subscription provider that supports both manual delivery and
    /// auto-delivery via queued messages per actor ID.
    /// When a handler subscribes to an actorId that has queued messages,
    /// those messages are delivered immediately (simulating OnActivateAsync publish).
    /// </summary>
    private sealed class FakeSubscriptionProvider : IActorEventSubscriptionProvider
    {
        private readonly Dictionary<string, Delegate> _handlers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<object>> _queued = new(StringComparer.Ordinal);

        /// <summary>
        /// Queue a message to be auto-delivered when a handler subscribes to the given actor ID.
        /// </summary>
        public void EnqueueForDelivery<TMessage>(string actorId, TMessage message)
            where TMessage : class, IMessage, new()
        {
            if (!_queued.TryGetValue(actorId, out var list))
            {
                list = [];
                _queued[actorId] = list;
            }
            list.Add(message);
        }

        public async Task<IAsyncDisposable> SubscribeAsync<TMessage>(
            string actorId,
            Func<TMessage, Task> handler,
            CancellationToken ct = default)
            where TMessage : class, IMessage, new()
        {
            _handlers[actorId] = handler;

            // Auto-deliver queued messages
            if (_queued.TryGetValue(actorId, out var queued))
            {
                foreach (var msg in queued)
                {
                    if (msg is TMessage typed)
                        await handler(typed);
                }
            }

            return new FakeSubscriptionHandle();
        }

        /// <summary>Deliver a message to the handler registered for the given actor ID.</summary>
        public async Task DeliverAsync<TMessage>(string actorId, TMessage message)
            where TMessage : class, IMessage, new()
        {
            if (_handlers.TryGetValue(actorId, out var handler) && handler is Func<TMessage, Task> typedHandler)
                await typedHandler(message);
        }
    }

    private sealed class FakeScopeResolver : IAppScopeResolver
    {
        public string? ScopeIdToReturn { get; set; }

        public AppScopeContext? Resolve(Microsoft.AspNetCore.Http.HttpContext? httpContext = null) =>
            ScopeIdToReturn is not null
                ? new AppScopeContext(ScopeIdToReturn, "test")
                : null;
    }

    // ── Helpers: wire up readmodel snapshot auto-delivery ──

    /// <summary>
    /// Configures fakes so that the readmodel actor auto-delivers the
    /// given snapshot when the store subscribes.
    /// </summary>
    private static void WireUpUserConfigReadModel(
        FakeSubscriptionProvider subscriptions,
        string readModelActorId,
        UserConfigGAgentState? snapshot)
    {
        if (snapshot is not null)
        {
            var snapshotEvent = new UserConfigStateSnapshotEvent { Snapshot = snapshot };
            var envelope = new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(snapshotEvent),
            };
            subscriptions.EnqueueForDelivery(readModelActorId, envelope);
        }
    }

    // ════════════════════════════════════════════════════════════
    // UserConfigStore: defaults when snapshot is null (timeout)
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_GetAsync_NullSnapshot_ReturnsDefaults()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "test-user" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, subscriptions, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        // No snapshot queued, readmodel timeout returns defaults
        var config = await store.GetAsync();

        config.DefaultModel.Should().BeEmpty();
        config.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
        config.RuntimeMode.Should().Be(UserConfigRuntimeDefaults.LocalMode);
        config.LocalRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.LocalRuntimeBaseUrl);
        config.RemoteRuntimeBaseUrl.Should().Be(UserConfigRuntimeDefaults.RemoteRuntimeBaseUrl);
        config.MaxToolRounds.Should().Be(0);
    }

    // ════════════════════════════════════════════════════════════
    // UserConfigStore: snapshot mapping
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_GetAsync_WithSnapshot_MapsFieldsCorrectly()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-42" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        // Queue snapshot for readmodel actor
        WireUpUserConfigReadModel(subscriptions, "user-config-user-42-readmodel",
            new UserConfigGAgentState
            {
                DefaultModel = "gpt-4",
                PreferredLlmRoute = "/api/v1/proxy/s/custom",
                RuntimeMode = "remote",
                LocalRuntimeBaseUrl = "http://localhost:9090",
                RemoteRuntimeBaseUrl = "https://remote.example.com",
                MaxToolRounds = 5,
            });

        var store = new ActorBackedUserConfigStore(
            runtime, subscriptions, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

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
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "user-empty" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        // Queue snapshot with empty strings (proto default)
        WireUpUserConfigReadModel(subscriptions, "user-config-user-empty-readmodel",
            new UserConfigGAgentState
            {
                DefaultModel = "claude-3",
                // Intentionally leave others as empty string (proto default)
            });

        var store = new ActorBackedUserConfigStore(
            runtime, subscriptions, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

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
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "save-scope" };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, subscriptions, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

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

        // Verify envelope routing targets the correct actor
        envelope.Route.Should().NotBeNull();
        envelope.Route.Direct.Should().NotBeNull();
        envelope.Route.Direct.TargetActorId.Should().Be(actorId);
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
    // Scope isolation: different scopes get different actor IDs
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task UserConfigStore_DifferentScopes_ProduceDifferentActorIds()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver();
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, subscriptions, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        // First scope
        scopeResolver.ScopeIdToReturn = "scope-alpha";
        _ = await store.GetAsync();

        // Second scope
        scopeResolver.ScopeIdToReturn = "scope-beta";
        _ = await store.GetAsync();

        // Both readmodel actors should be created
        runtime.Actors.Should().ContainKey("user-config-scope-alpha-readmodel");
        runtime.Actors.Should().ContainKey("user-config-scope-beta-readmodel");
    }

    [Fact]
    public async Task UserConfigStore_NullScope_FallsBackToDefault()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = null };
        var logger = NullLogger<ActorBackedUserConfigStore>.Instance;

        var store = new ActorBackedUserConfigStore(
            runtime, subscriptions, scopeResolver, Options.Create(new StudioStorageOptions()), logger);

        _ = await store.GetAsync();

        runtime.Actors.Should().ContainKey("user-config-default-readmodel",
            "null scope should resolve to 'default' suffix");
    }

    // ════════════════════════════════════════════════════════════
    // GAgentActorStore: scope isolation
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task GAgentActorStore_ScopeIsolation_DifferentScopesGetDifferentActors()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver();
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, subscriptions, scopeResolver, logger);

        scopeResolver.ScopeIdToReturn = "tenant-a";
        _ = await store.GetAsync();

        scopeResolver.ScopeIdToReturn = "tenant-b";
        _ = await store.GetAsync();

        runtime.Actors.Should().ContainKey("gagent-registry-tenant-a-readmodel");
        runtime.Actors.Should().ContainKey("gagent-registry-tenant-b-readmodel");
    }

    [Fact]
    public async Task GAgentActorStore_GetAsync_NullSnapshot_ReturnsEmptyList()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "empty-scope" };
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, subscriptions, scopeResolver, logger);

        var groups = await store.GetAsync();

        groups.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════
    // WorkflowStoragePort: command construction
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task WorkflowStoragePort_UploadAsync_SendsWorkflowYamlUploadedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedWorkflowStoragePort>.Instance;

        var port = new ActorBackedWorkflowStoragePort(runtime, logger);

        await port.UploadWorkflowYamlAsync("wf-001", "My Workflow", "name: test\nsteps: []", CancellationToken.None);

        const string expectedActorId = "workflow-storage";
        runtime.Actors.Should().ContainKey(expectedActorId);

        var actor = runtime.Actors[expectedActorId];
        actor.ReceivedEnvelopes.Should().HaveCount(1);

        var envelope = actor.ReceivedEnvelopes[0];
        envelope.Payload.Is(WorkflowYamlUploadedEvent.Descriptor).Should().BeTrue();

        var evt = envelope.Payload.Unpack<WorkflowYamlUploadedEvent>();
        evt.WorkflowId.Should().Be("wf-001");
        evt.WorkflowName.Should().Be("My Workflow");
        evt.Yaml.Should().Be("name: test\nsteps: []");

        // Verify direct routing
        envelope.Route.Direct.TargetActorId.Should().Be(expectedActorId);
    }

    [Fact]
    public async Task WorkflowStoragePort_MultipleUploads_ReusesSameActor()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedWorkflowStoragePort>.Instance;

        var port = new ActorBackedWorkflowStoragePort(runtime, logger);

        await port.UploadWorkflowYamlAsync("wf-1", "First", "yaml1", CancellationToken.None);
        await port.UploadWorkflowYamlAsync("wf-2", "Second", "yaml2", CancellationToken.None);

        runtime.Actors.Should().HaveCount(1, "actor should be reused across uploads");

        var actor = runtime.Actors["workflow-storage"];
        actor.ReceivedEnvelopes.Should().HaveCount(2);
    }

    // ════════════════════════════════════════════════════════════
    // ScriptStoragePort: command construction
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ScriptStoragePort_UploadAsync_SendsScriptUploadedEvent()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedScriptStoragePort>.Instance;

        var port = new ActorBackedScriptStoragePort(runtime, logger);

        await port.UploadScriptAsync("script-42", "console.log('hello');", CancellationToken.None);

        const string expectedActorId = "script-storage";
        runtime.Actors.Should().ContainKey(expectedActorId);

        var actor = runtime.Actors[expectedActorId];
        actor.ReceivedEnvelopes.Should().HaveCount(1);

        var envelope = actor.ReceivedEnvelopes[0];
        envelope.Payload.Is(ScriptUploadedEvent.Descriptor).Should().BeTrue();

        var evt = envelope.Payload.Unpack<ScriptUploadedEvent>();
        evt.ScriptId.Should().Be("script-42");
        evt.SourceText.Should().Be("console.log('hello');");

        // Verify direct routing
        envelope.Route.Direct.TargetActorId.Should().Be(expectedActorId);
    }

    [Fact]
    public async Task ScriptStoragePort_MultipleUploads_ReusesSameActor()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedScriptStoragePort>.Instance;

        var port = new ActorBackedScriptStoragePort(runtime, logger);

        await port.UploadScriptAsync("s1", "code1", CancellationToken.None);
        await port.UploadScriptAsync("s2", "code2", CancellationToken.None);

        runtime.Actors.Should().HaveCount(1, "actor should be reused");
        runtime.Actors["script-storage"].ReceivedEnvelopes.Should().HaveCount(2);
    }

    // ════════════════════════════════════════════════════════════
    // Envelope structure verification
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllStores_EnvelopeContainsIdAndTimestamp()
    {
        var runtime = new FakeActorRuntime();
        var logger = NullLogger<ActorBackedScriptStoragePort>.Instance;

        var port = new ActorBackedScriptStoragePort(runtime, logger);
        var beforeUtc = DateTimeOffset.UtcNow;

        await port.UploadScriptAsync("ts-check", "body", CancellationToken.None);

        var afterUtc = DateTimeOffset.UtcNow;
        var envelope = runtime.Actors["script-storage"].ReceivedEnvelopes[0];

        envelope.Id.Should().NotBeNullOrWhiteSpace("envelope must have a unique ID");
        envelope.Id.Length.Should().Be(32, "ID should be a Guid without dashes");

        var ts = envelope.Timestamp.ToDateTimeOffset();
        ts.Should().BeOnOrAfter(beforeUtc).And.BeOnOrBefore(afterUtc);
    }

    // ════════════════════════════════════════════════════════════
    // GAgentActorStore: AddActorAsync command construction
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task GAgentActorStore_AddActorAsync_SendsActorRegisteredEvent()
    {
        var runtime = new FakeActorRuntime();
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "cmd-scope" };
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, subscriptions, scopeResolver, logger);

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
        var subscriptions = new FakeSubscriptionProvider();
        var scopeResolver = new FakeScopeResolver { ScopeIdToReturn = "cmd-scope" };
        var logger = NullLogger<ActorBackedGAgentActorStore>.Instance;

        var store = new ActorBackedGAgentActorStore(
            runtime, subscriptions, scopeResolver, logger);

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
