using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LongRunningBusinessIoExecutorTests
{
    [Fact]
    public async Task SubmitAsync_RespectsConfiguredConcurrencyLimit()
    {
        await using var executor = CreateExecutor(maxConcurrency: 2, queueCapacity: 8);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTwoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maxObserved = 0;

        var tasks = Enumerable.Range(0, 4)
            .Select(index => executor.SubmitAsync(
                WorkItem($"work-{index}", async _ =>
                {
                    var current = Interlocked.Increment(ref running);
                    UpdateMax(ref maxObserved, current);
                    if (current == 2)
                        firstTwoStarted.TrySetResult();
                    await gate.Task.ConfigureAwait(false);
                    Interlocked.Decrement(ref running);
                }),
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);
        await firstTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        maxObserved.Should().Be(2);
        running.Should().Be(2);
        gate.SetResult();
    }

    [Fact]
    public async Task SubmitAsync_HonorsSubmitCancellation_WhenQueueIsFull()
    {
        await using var executor = CreateExecutor(maxConcurrency: 1, queueCapacity: 1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await executor.SubmitAsync(WorkItem("running", _ => gate.Task), CancellationToken.None);
        await executor.SubmitAsync(WorkItem("queued", _ => Task.CompletedTask), CancellationToken.None);

        using var submitCts = new CancellationTokenSource();
        await submitCts.CancelAsync();

        var act = () => executor.SubmitAsync(WorkItem("cancelled", _ => Task.CompletedTask), submitCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        gate.SetResult();
    }

    [Fact]
    public async Task ExecuteAsync_CancelsWork_WhenTimeoutExpires()
    {
        await using var executor = CreateExecutor(maxConcurrency: 1, queueCapacity: 4);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await executor.SubmitAsync(
            WorkItem(
                "timeout",
                ct =>
                {
                    var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ct.Register(static state =>
                    {
                        var (observed, blocked) = ((TaskCompletionSource Observed, TaskCompletionSource Blocked))state!;
                        observed.SetResult();
                        blocked.SetCanceled();
                    }, (cancellationObserved, pending));
                    return pending.Task;
                },
                timeout: TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExecuteAsync_PreservesTypedOwnershipAndCorrelationContract()
    {
        await using var executor = CreateExecutor(maxConcurrency: 1, queueCapacity: 4);
        var observed = new TaskCompletionSource<(string OwnerActorId, string OperationName, string CorrelationId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new LongRunningBusinessIoWorkItem(
            "work-corr-1",
            "actor-1",
            "lark-card-create",
            "corr-1",
            TimeSpan.FromSeconds(5),
            _ =>
            {
                observed.SetResult(("actor-1", "lark-card-create", "corr-1"));
                return Task.CompletedTask;
            });

        await executor.SubmitAsync(item, CancellationToken.None);

        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        result.OwnerActorId.Should().Be(item.OwnerActorId);
        result.OperationName.Should().Be(item.OperationName);
        result.CorrelationId.Should().Be(item.CorrelationId);
    }

    private static LongRunningBusinessIoExecutor CreateExecutor(int maxConcurrency, int queueCapacity) =>
        new(
            Options.Create(new LongRunningBusinessIoExecutorOptions
            {
                MaxConcurrency = maxConcurrency,
                QueueCapacity = queueCapacity,
                DefaultTimeout = TimeSpan.FromSeconds(10),
            }),
            NullLogger<LongRunningBusinessIoExecutor>.Instance);

    private static LongRunningBusinessIoWorkItem WorkItem(
        string id,
        Func<CancellationToken, Task> executeAsync,
        TimeSpan? timeout = null) =>
        new(
            id,
            "owner-1",
            "operation-1",
            $"corr-{id}",
            timeout ?? TimeSpan.FromSeconds(10),
            executeAsync);

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current)
                return;
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
