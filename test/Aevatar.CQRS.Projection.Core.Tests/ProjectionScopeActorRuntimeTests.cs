using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeActorRuntimeTests
{
    [Fact]
    public async Task EnsureExistsAsync_ShouldCreate_WhenActorMissing()
    {
        var operationLog = new List<string>();
        var runtime = new RecordingRuntime(operationLog);
        var dispatchPort = new NoopDispatchPort();
        var verifier = new StubAgentTypeVerifier(_ => true);
        var sut = new ProjectionScopeActorRuntime<DummyAgent>(
            runtime,
            dispatchPort,
            verifier,
            streamPubSubMaintenance: null,
            logger: NullLogger<ProjectionScopeActorRuntime<DummyAgent>>.Instance);

        var scopeKey = new ProjectionRuntimeScopeKey(
            "agent-registry-store",
            "user-agent-catalog-read-model",
            ProjectionRuntimeMode.DurableMaterialization);

        await sut.EnsureExistsAsync(scopeKey, CancellationToken.None);

        var actorId = ProjectionScopeActorId.Build(scopeKey);
        runtime.CreatedActorIds.Should().Equal(actorId);
        runtime.DestroyedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureExistsAsync_ShouldNoOp_WhenActorExistsWithExpectedType()
    {
        var actorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            "agent-registry-store",
            "user-agent-catalog-read-model",
            ProjectionRuntimeMode.DurableMaterialization));
        var operationLog = new List<string>();
        var runtime = new RecordingRuntime(operationLog);
        runtime.SeedExisting(actorId);
        var verifier = new StubAgentTypeVerifier(_ => true);
        var sut = new ProjectionScopeActorRuntime<DummyAgent>(
            runtime,
            new NoopDispatchPort(),
            verifier,
            streamPubSubMaintenance: null,
            logger: NullLogger<ProjectionScopeActorRuntime<DummyAgent>>.Instance);

        await sut.EnsureExistsAsync(new ProjectionRuntimeScopeKey(
            "agent-registry-store",
            "user-agent-catalog-read-model",
            ProjectionRuntimeMode.DurableMaterialization),
            CancellationToken.None);

        runtime.CreatedActorIds.Should().BeEmpty();
        runtime.DestroyedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureExistsAsync_ShouldDestroyResetAndRecreate_WhenActorTypeIsStale()
    {
        // Mid-migration scope actors hold an older materialization context type
        // (e.g. ChannelRuntime → Scheduled rename). The original behaviour threw
        // InvalidOperationException, blocking projection startup forever. We now
        // self-heal: destroy the stale actor, reset its stream pub/sub
        // rendezvous (so RegisterAsStreamProducer doesn't hit the leaked etag),
        // then recreate as the expected type.
        var scopeKey = new ProjectionRuntimeScopeKey(
            "agent-registry-store",
            "user-agent-catalog-read-model",
            ProjectionRuntimeMode.DurableMaterialization);
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        var operationLog = new List<string>();
        var runtime = new RecordingRuntime(operationLog);
        runtime.SeedExisting(actorId);
        var verifier = new StubAgentTypeVerifier(_ => false);
        var pubSub = new RecordingPubSubMaintenance(operationLog);
        var sut = new ProjectionScopeActorRuntime<DummyAgent>(
            runtime,
            new NoopDispatchPort(),
            verifier,
            pubSub,
            NullLogger<ProjectionScopeActorRuntime<DummyAgent>>.Instance);

        await sut.EnsureExistsAsync(scopeKey, CancellationToken.None);

        // Order matters across all three operations: destroy → pub/sub reset →
        // recreate. Reordering pub/sub reset to before destroy or after create
        // leaves the recreated actor's RegisterAsStreamProducer hitting the
        // stale etag from the previous incarnation.
        operationLog.Should().Equal(
            "destroy:" + actorId,
            "pubsub-reset:" + actorId,
            "create:" + actorId);
    }

    [Fact]
    public async Task EnsureExistsAsync_ShouldStillRecreate_WhenPubSubResetThrows()
    {
        // Pub/sub reset is best-effort. Once we've destroyed the stale actor,
        // a maintenance impl that throws must not block the recreate — failing
        // here would leave the cluster strictly worse than the pre-self-heal
        // state (the type mismatch at least had an actor).
        var scopeKey = new ProjectionRuntimeScopeKey(
            "agent-registry-store",
            "user-agent-catalog-read-model",
            ProjectionRuntimeMode.DurableMaterialization);
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        var operationLog = new List<string>();
        var runtime = new RecordingRuntime(operationLog);
        runtime.SeedExisting(actorId);
        var verifier = new StubAgentTypeVerifier(_ => false);
        var pubSub = new ThrowingPubSubMaintenance();
        var sut = new ProjectionScopeActorRuntime<DummyAgent>(
            runtime,
            new NoopDispatchPort(),
            verifier,
            pubSub,
            NullLogger<ProjectionScopeActorRuntime<DummyAgent>>.Instance);

        await sut.EnsureExistsAsync(scopeKey, CancellationToken.None);

        operationLog.Should().Equal("destroy:" + actorId, "create:" + actorId);
    }

    [Fact]
    public async Task EnsureExistsAsync_ShouldStillSelfHeal_WhenPubSubMaintenanceUnavailable()
    {
        var scopeKey = new ProjectionRuntimeScopeKey(
            "agent-registry-store",
            "user-agent-catalog-read-model",
            ProjectionRuntimeMode.DurableMaterialization);
        var actorId = ProjectionScopeActorId.Build(scopeKey);
        var operationLog = new List<string>();
        var runtime = new RecordingRuntime(operationLog);
        runtime.SeedExisting(actorId);
        var verifier = new StubAgentTypeVerifier(_ => false);
        var sut = new ProjectionScopeActorRuntime<DummyAgent>(
            runtime,
            new NoopDispatchPort(),
            verifier,
            streamPubSubMaintenance: null,
            logger: NullLogger<ProjectionScopeActorRuntime<DummyAgent>>.Instance);

        await sut.EnsureExistsAsync(scopeKey, CancellationToken.None);

        operationLog.Should().Equal("destroy:" + actorId, "create:" + actorId);
    }

    private sealed class DummyAgent : IAgent
    {
        public string Id => "dummy";
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("dummy");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);
    }

    private sealed class RecordingRuntime(List<string> operationLog) : IActorRuntime
    {
        private readonly HashSet<string> _existing = new(StringComparer.Ordinal);
        private readonly List<string> _operationLog = operationLog;

        public List<string> CreatedActorIds { get; } = [];
        public List<string> DestroyedActorIds { get; } = [];

        public void SeedExisting(string actorId) => _existing.Add(actorId);

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ArgumentNullException.ThrowIfNull(id);
            CreatedActorIds.Add(id);
            _operationLog.Add("create:" + id);
            _existing.Add(id);
            return Task.FromResult<IActor>(new StubActor(id));
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<DummyAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActorIds.Add(id);
            _operationLog.Add("destroy:" + id);
            _existing.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(_existing.Contains(id) ? new StubActor(id) : null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(_existing.Contains(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;

        private sealed class StubActor(string id) : IActor
        {
            public string Id => id;
            public IAgent Agent => new DummyAgent();
            public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
            public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
            public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
                Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class StubAgentTypeVerifier(Func<string, bool> matcher) : IAgentTypeVerifier
    {
        public Task<bool> IsExpectedAsync(string actorId, Type expectedType, CancellationToken ct = default) =>
            Task.FromResult(matcher(actorId));
    }

    private sealed class RecordingPubSubMaintenance(List<string> operationLog) : IStreamPubSubMaintenance
    {
        private readonly List<string> _operationLog = operationLog;

        public Task<bool> ResetActorStreamPubSubAsync(string actorId, CancellationToken ct = default)
        {
            _operationLog.Add("pubsub-reset:" + actorId);
            return Task.FromResult(true);
        }
    }

    private sealed class ThrowingPubSubMaintenance : IStreamPubSubMaintenance
    {
        public Task<bool> ResetActorStreamPubSubAsync(string actorId, CancellationToken ct = default) =>
            throw new InvalidOperationException("pub/sub backend offline");
    }

    private sealed class NoopDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
