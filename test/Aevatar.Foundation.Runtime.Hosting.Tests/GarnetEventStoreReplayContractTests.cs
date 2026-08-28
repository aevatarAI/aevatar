using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using FluentAssertions;
using Google.Protobuf;
using NSubstitute;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class GarnetEventStoreReplayContractTests
{
    [Fact]
    public async Task GetEventsAsync_ShouldBoundSingleFieldPayloadReads()
    {
        const int firstBatchSize = 64;
        const int eventCount = firstBatchSize + 1;
        var (store, database) = CreateStore();
        var versions = Enumerable.Range(1, eventCount)
            .Select(static version => (RedisValue)version)
            .ToArray();
        var payload = CreateEvent("actor-1", version: 1).ToByteArray();
        var firstBatchRelease = new TaskCompletionSource<RedisValue>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBatchScheduled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var singleReadCount = 0;

        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)eventCount);
        database.SortedSetRangeByScoreAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<Exclude>(),
                Arg.Any<Order>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<CommandFlags>())
            .Returns(versions);
        database.HashGetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                var callNumber = Interlocked.Increment(ref singleReadCount);
                if (callNumber == firstBatchSize)
                    firstBatchScheduled.TrySetResult();

                return callNumber <= firstBatchSize
                    ? firstBatchRelease.Task
                    : Task.FromResult((RedisValue)payload);
            });

        var replay = store.GetEventsAsync("actor-1");
        await firstBatchScheduled.Task;

        singleReadCount.Should().Be(firstBatchSize);
        replay.IsCompleted.Should().BeFalse();
        await database.DidNotReceive().HashGetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());

        firstBatchRelease.TrySetResult(payload);
        var events = await replay;

        events.Should().HaveCount(eventCount);
        singleReadCount.Should().Be(eventCount);
    }

    private static (GarnetEventStore Store, IDatabase Database) CreateStore()
    {
        var database = Substitute.For<IDatabase>();
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var store = new GarnetEventStore(
            connection,
            new GarnetEventStoreOptions
            {
                ConnectionString = "unused.test:6379",
                KeyPrefix = $"aevatar:test:eventstore:{Guid.NewGuid():N}",
            });
        return (store, database);
    }

    private static StateEvent CreateEvent(string agentId, long version) => new()
    {
        AgentId = agentId,
        EventId = Guid.NewGuid().ToString("N"),
        EventType = "test.event",
        Version = version,
    };
}
