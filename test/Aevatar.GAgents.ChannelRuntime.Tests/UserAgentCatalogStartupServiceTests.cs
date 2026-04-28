using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Xunit;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class UserAgentCatalogStartupServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldRemoveLegacyRelay_AndDestroyLegacyScope_BeforeActivatingNewProjection()
    {
        var operations = new ConcurrentQueue<string>();
        var activationService = new RecordingActivationService(operations);
        var projectionPort = new UserAgentCatalogProjectionPort(activationService);
        var actorRuntime = new RecordingActorRuntime(operations, legacyScopeExists: true);
        var streamProvider = new RecordingStreamProvider(operations);
        var service = new UserAgentCatalogStartupService(
            projectionPort,
            actorRuntime,
            streamProvider,
            NullLogger<UserAgentCatalogStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var legacyScopeActorId = ProjectionScopeActorId.Build(
            new ProjectionRuntimeScopeKey(
                UserAgentCatalogGAgent.WellKnownId,
                UserAgentCatalogStorageContracts.LegacyDurableProjectionKind,
                ProjectionRuntimeMode.DurableMaterialization));

        operations.Should().ContainInOrder(
            $"stream:get:{UserAgentCatalogGAgent.WellKnownId}",
            $"stream:remove-relay:{UserAgentCatalogGAgent.WellKnownId}->{legacyScopeActorId}",
            $"runtime:exists:{legacyScopeActorId}",
            $"runtime:destroy:{legacyScopeActorId}",
            $"projection:ensure:{UserAgentCatalogGAgent.WellKnownId}:{UserAgentCatalogProjectionPort.ProjectionKind}");
        activationService.LastRequest.Should().NotBeNull();
        activationService.LastRequest!.ProjectionKind.Should().Be(UserAgentCatalogProjectionPort.ProjectionKind);
        streamProvider.Stream.RemovedRelayTargets.Should().ContainSingle().Which.Should().Be(legacyScopeActorId);
        actorRuntime.DestroyedActorIds.Should().ContainSingle().Which.Should().Be(legacyScopeActorId);
    }

    private sealed class RecordingActivationService(ConcurrentQueue<string> operations)
        : IProjectionScopeActivationService<UserAgentCatalogMaterializationRuntimeLease>
    {
        public ProjectionScopeStartRequest? LastRequest { get; private set; }

        public Task<UserAgentCatalogMaterializationRuntimeLease> EnsureAsync(
            ProjectionScopeStartRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            operations.Enqueue($"projection:ensure:{request.RootActorId}:{request.ProjectionKind}");
            return Task.FromResult(new UserAgentCatalogMaterializationRuntimeLease(
                new UserAgentCatalogMaterializationContext
                {
                    RootActorId = request.RootActorId,
                    ProjectionKind = request.ProjectionKind,
                }));
        }
    }

    private sealed class RecordingActorRuntime(ConcurrentQueue<string> operations, bool legacyScopeExists)
        : IActorRuntime
    {
        public List<string> DestroyedActorIds { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            throw new NotSupportedException();

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            DestroyedActorIds.Add(id);
            operations.Enqueue($"runtime:destroy:{id}");
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id)
        {
            operations.Enqueue($"runtime:exists:{id}");
            return Task.FromResult(legacyScopeExists);
        }

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingStreamProvider(ConcurrentQueue<string> operations) : IStreamProvider
    {
        public RecordingStream Stream { get; } = new(operations);

        public IStream GetStream(string actorId)
        {
            operations.Enqueue($"stream:get:{actorId}");
            Stream.SourceStreamId = actorId;
            return Stream;
        }
    }

    private sealed class RecordingStream(ConcurrentQueue<string> operations) : IStream
    {
        public string StreamId => SourceStreamId ?? string.Empty;

        public string? SourceStreamId { get; set; }

        public List<string> RemovedRelayTargets { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : Google.Protobuf.IMessage =>
            throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, Task> handler,
            CancellationToken ct = default) where T : Google.Protobuf.IMessage, new() =>
            throw new NotSupportedException();

        public Task UpsertRelayAsync(
            Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding binding,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            RemovedRelayTargets.Add(targetStreamId);
            operations.Enqueue($"stream:remove-relay:{SourceStreamId}->{targetStreamId}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Aevatar.Foundation.Abstractions.Streaming.StreamForwardingBinding>> ListRelaysAsync(
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
