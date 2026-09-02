using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet;
using FluentAssertions;
using NSubstitute;
using StackExchange.Redis;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class GarnetEventStoreCancellationContractTests
{
    [Fact]
    public async Task AppendAsync_WhenDeadlineCancelsDuringAtomicScript_ShouldReturnCommitResult()
    {
        using var deadline = new CancellationTokenSource();
        var (store, database) = CreateStore();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                deadline.Cancel();
                return Task.FromResult(CommittedScriptResult(latestVersion: 1));
            });

        var result = await store.AppendAsync(
            "agent-commit-during-deadline",
            [CreateEvent("agent-commit-during-deadline", version: 1)],
            expectedVersion: 0,
            deadline.Token);

        deadline.IsCancellationRequested.Should().BeTrue();
        result.LatestVersion.Should().Be(1);
        result.CommittedEvents.Should().ContainSingle().Which.Version.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_WhenCommitResultReturnsAfterDeadline_ShouldReturnAuthoritativeResult()
    {
        using var deadline = new CancellationTokenSource();
        var scriptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scriptResult = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var (store, database) = CreateStore();
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                scriptStarted.TrySetResult();
                return scriptResult.Task;
            });

        var append = store.AppendAsync(
            "agent-late-commit-result",
            [CreateEvent("agent-late-commit-result", version: 1)],
            expectedVersion: 0,
            deadline.Token);
        await scriptStarted.Task;

        deadline.Cancel();
        scriptResult.TrySetResult(CommittedScriptResult(latestVersion: 1));

        var result = await append;
        result.LatestVersion.Should().Be(1);
        result.CommittedEvents.Should().ContainSingle().Which.Version.Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_WhenCanceledBeforeAdmission_ShouldCommitNothing()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        var (store, database) = CreateStore();

        var append = () => store.AppendAsync(
            "agent-canceled-before-admission",
            [CreateEvent("agent-canceled-before-admission", version: 1)],
            expectedVersion: 0,
            deadline.Token);

        await append.Should().ThrowAsync<OperationCanceledException>();
        await database.DidNotReceive().ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());
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

    private static RedisResult CommittedScriptResult(long latestVersion) =>
        RedisResult.Create([(RedisValue)1, (RedisValue)latestVersion]);

    private static StateEvent CreateEvent(string agentId, long version) => new()
    {
        AgentId = agentId,
        EventId = Guid.NewGuid().ToString("N"),
        EventType = "test.event",
        Version = version,
    };
}
