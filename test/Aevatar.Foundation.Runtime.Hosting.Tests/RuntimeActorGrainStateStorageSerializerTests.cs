using System.Text;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Persistence;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Storage;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeActorGrainStateStorageSerializerTests
{
    [Fact]
    public void Serializer_ShouldKeepBothRuntimeActorStateSlotsReadableByRollingPeers()
    {
        using var host = BuildRuntimeHost();
        var options = host.Services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>();
        var rollingPeerSerializer = options
            .Get(OrleansRuntimeConstants.GrainStateStorageName)
            .GrainStorageSerializer;
        var serializer = options
            .Get(OrleansRuntimeConstants.RuntimeActorGrainStateStorageName)
            .GrainStorageSerializer;
        var actorState = new RuntimeActorGrainState
        {
            AgentId = "actor-rolling-json",
            ParentId = "parent-rolling-json",
            Children = ["child-a", "child-b"],
            AgentStateTypeName = "tests.ActorState",
            AgentStateSnapshot = [1, 2, 3],
            AgentStateSnapshotVersion = 9,
            Identity = new RuntimeActorIdentity
            {
                Kind = "tests.rolling-json-state",
                StateSchemaVersion = 4,
            },
            CommittedStatePublicationState = [4, 5, 6],
        };
        var publicationState = new RuntimeActorCommittedStatePublicationGrainState
        {
            Checkpoint = [7, 8, 9],
        };

        var actorBytes = serializer.Serialize(actorState);
        var publicationBytes = serializer.Serialize(publicationState);

        Encoding.UTF8.GetString(actorBytes.ToArray()).Should().StartWith("{");
        serializer.Deserialize<RuntimeActorGrainState>(actorBytes).Should().BeEquivalentTo(actorState);
        serializer.Deserialize<RuntimeActorCommittedStatePublicationGrainState>(publicationBytes)
            .Should().BeEquivalentTo(publicationState);
        rollingPeerSerializer.Deserialize<RuntimeActorGrainState>(actorBytes)
            .Should().BeEquivalentTo(actorState);
        rollingPeerSerializer.Deserialize<RuntimeActorCommittedStatePublicationGrainState>(publicationBytes)
            .Should().BeEquivalentTo(publicationState);
    }

    [Fact]
    public void Serializer_ShouldReadAndRewriteExistingValidOrleansJsonRow()
    {
        using var host = BuildRuntimeHost();
        var options = host.Services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>();
        var legacySerializer = options
            .Get(OrleansRuntimeConstants.GrainStateStorageName)
            .GrainStorageSerializer;
        var runtimeSerializer = options
            .Get(OrleansRuntimeConstants.RuntimeActorGrainStateStorageName)
            .GrainStorageSerializer;
        var legacyState = new RuntimeActorGrainState
        {
            AgentId = "actor-legacy-json",
            ParentId = "parent-legacy-json",
            Children = ["child-legacy-json"],
            AgentStateTypeName = "tests.LegacyState",
            AgentStateSnapshot = [10, 11],
            AgentStateSnapshotVersion = 17,
            Identity = new RuntimeActorIdentity
            {
                Kind = "tests.legacy-json",
                StateSchemaVersion = 2,
            },
        };

        var legacyBytes = legacySerializer.Serialize(legacyState);
        var migrated = runtimeSerializer.Deserialize<RuntimeActorGrainState>(legacyBytes);
        var rewrittenJsonBytes = runtimeSerializer.Serialize(migrated);

        migrated.Should().BeEquivalentTo(legacyState);
        Encoding.UTF8.GetString(rewrittenJsonBytes.ToArray()).Should().StartWith("{");
        runtimeSerializer.Deserialize<RuntimeActorGrainState>(rewrittenJsonBytes)
            .Should().BeEquivalentTo(legacyState);
    }

    [Fact]
    public void Serializer_WhenLegacyRowIsReferenceToken_ShouldReturnTypedRecoveryState()
    {
        var serializer = new RuntimeActorGrainStateStorageSerializer(new RejectingLegacySerializer());
        var source = Encoding.UTF8.GetBytes("\"$id\"");

        var state = serializer.Deserialize<RuntimeActorGrainState>(BinaryData.FromBytes(source));

        state.Identity.Should().BeNull();
        state.StorageRecovery.Should().NotBeNull();
        state.StorageRecovery!.Reason.Should().Be(
            RuntimeActorStateStorageRecoveryReason.LegacyJsonReferenceToken);
        state.StorageRecovery.SourcePayload.ToByteArray().Should().Equal(source);
        serializer.Serialize(state).ToArray().Should().Equal(source);
    }

    [Fact]
    public void Serializer_WhenLegacyRowHasAnyOtherInvalidShape_ShouldFailClosed()
    {
        var expected = new InvalidOperationException("legacy parser rejected payload");
        var serializer = new RuntimeActorGrainStateStorageSerializer(
            new RejectingLegacySerializer(expected));

        var act = () => serializer.Deserialize<RuntimeActorGrainState>(
            BinaryData.FromBytes(Encoding.UTF8.GetBytes("\"unexpected\"")));

        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
    }

    [Fact]
    public void Serializer_WhenRollingJsonWriterProducesNonObjectRoot_ShouldRejectDurableWrite()
    {
        var serializer = new RuntimeActorGrainStateStorageSerializer(
            new FixedSerializationLegacySerializer(
                BinaryData.FromBytes(Encoding.UTF8.GetBytes("\"$id\""))));

        var act = () => serializer.Serialize(new RuntimeActorGrainState
        {
            AgentId = "actor-invalid-write",
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*durable write was rejected*");
    }

    private static IHost BuildRuntimeHost() => new HostBuilder()
        .UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering(
                siloPort: 11126,
                gatewayPort: 30026,
                serviceId: $"runtime-state-serializer-{Guid.NewGuid():N}",
                clusterId: $"runtime-state-serializer-{Guid.NewGuid():N}");
            siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
            {
                options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
            });
        })
        .Build();

    private sealed class RejectingLegacySerializer(Exception? failure = null) : IGrainStorageSerializer
    {
        private readonly Exception _failure = failure ?? new InvalidOperationException("legacy parser must not be used");

        public BinaryData Serialize<T>(T input) => throw _failure;

        public T Deserialize<T>(BinaryData input) => throw _failure;
    }

    private sealed class FixedSerializationLegacySerializer(BinaryData serialized) : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T input) => serialized;

        public T Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }
}
