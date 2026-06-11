using System.Reflection;
using System.Runtime.CompilerServices;
using Aevatar.Foundation.Runtime.Implementations.Orleans;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Persistence;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

/// <summary>
/// Pins our pub/sub redis-key format to Orleans's actual
/// <see cref="RedisGrainStorage"/> key derivation. If Orleans ever changes the
/// format the test breaks immediately rather than silently leaking stale state
/// after a deploy.
/// </summary>
public sealed class OrleansRedisStreamPubSubMaintenanceTests
{
    private const string ServiceId = "aevatar-mainnet-host-api";
    private const string StreamProvider = "AevatarOrleansStreamProvider";
    private const string StreamNamespace = "aevatar.actor.events";
    private const string ActorId = "projection.durable.scope:channel-bot-registration:channel-bot-registration-store";

    [Fact]
    public async Task ResetActorStreamPubSubAsync_ShouldDeleteRedisKey_MatchingOrleansDefaultStorageKey()
    {
        var capturedKeys = new List<RedisKey>();
        var database = Substitute.For<IDatabase>();
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                capturedKeys.Add(call.Arg<RedisKey>());
                return Task.FromResult(true);
            });
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var sut = new OrleansRedisStreamPubSubMaintenance(
            connection,
            new AevatarOrleansRuntimeOptions
            {
                StreamProviderName = StreamProvider,
                ActorEventNamespace = StreamNamespace,
            },
            Options.Create(new ClusterOptions { ServiceId = ServiceId }),
            NullLogger<OrleansRedisStreamPubSubMaintenance>.Instance);

        var deleted = await sut.ResetActorStreamPubSubAsync(ActorId);

        deleted.Should().BeTrue();
        capturedKeys.Should().HaveCount(1);

        var actualKey = capturedKeys[0].ToString()!;
        var expectedKey = ComputeOrleansDefaultStorageKey(ServiceId, StreamProvider, StreamNamespace, ActorId);
        actualKey.Should().Be(expectedKey);
    }

    [Fact]
    public async Task ResetActorStreamPubSubAsync_ShouldNotThrow_WhenRedisDeleteFails()
    {
        var database = Substitute.For<IDatabase>();
        database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ =>
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        var sut = new OrleansRedisStreamPubSubMaintenance(
            connection,
            new AevatarOrleansRuntimeOptions
            {
                StreamProviderName = StreamProvider,
                ActorEventNamespace = StreamNamespace,
            },
            Options.Create(new ClusterOptions { ServiceId = ServiceId }),
            NullLogger<OrleansRedisStreamPubSubMaintenance>.Instance);

        var deleted = await sut.ResetActorStreamPubSubAsync(ActorId);
        deleted.Should().BeFalse();
    }

    [Fact]
    public void Construction_ShouldThrow_WhenServiceIdMissing()
    {
        var act = () => new OrleansRedisStreamPubSubMaintenance(
            Substitute.For<IConnectionMultiplexer>(),
            new AevatarOrleansRuntimeOptions(),
            Options.Create(new ClusterOptions { ServiceId = null! }),
            NullLogger<OrleansRedisStreamPubSubMaintenance>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ServiceId is required*");
    }

    private static string ComputeOrleansDefaultStorageKey(
        string serviceId, string streamProvider, string streamNamespace, string actorId)
    {
        var grainId = GrainId.Create("pubsubrendezvous", $"{streamProvider}/{streamNamespace}/{actorId}");
        var method = typeof(RedisGrainStorage)
            .GetMethod(
                "DefaultGetStorageKey",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (method == null)
            throw new InvalidOperationException("RedisGrainStorage.DefaultGetStorageKey not found.");

        // The method has an instance-method signature but does not actually use
        // any instance state (verified against Microsoft.Orleans.Persistence.Redis 10.0.x).
        // We invoke it on an uninitialized instance to derive the canonical key
        // without spinning up a redis backend.
        var instance = RuntimeHelpers.GetUninitializedObject(typeof(RedisGrainStorage));
        var redisKey = (RedisKey)method.Invoke(instance, [serviceId, grainId])!;
        return redisKey.ToString()!;
    }
}
