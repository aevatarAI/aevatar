// ─── AgUiEventChannel 测试 ───
// 验证 Channel 驱动的事件收集器

using Aevatar.AGUI;
using FluentAssertions;

namespace Aevatar.Api.Tests;

public class AgUiEventChannelTests
{
    [Fact]
    public async Task PushAndRead_RoundTrip()
    {
        await using var channel = new AgUiEventChannel();

        channel.Push(new RunStartedEvent { ThreadId = "t1", RunId = "r1" });
        channel.Push(new StepStartedEvent { StepName = "step1" });
        channel.Complete();

        var events = new List<AgUiEvent>();
        await foreach (var evt in channel.ReadAllAsync())
            events.Add(evt);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<RunStartedEvent>();
        events[1].Should().BeOfType<StepStartedEvent>();
    }

    [Fact]
    public async Task Complete_TerminatesReader()
    {
        await using var channel = new AgUiEventChannel();

        var readTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in channel.ReadAllAsync())
                count++;
            return count;
        });

        channel.Push(new RunStartedEvent { ThreadId = "t", RunId = "r" });
        channel.Complete();

        var result = await readTask;
        result.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_StopsReader()
    {
        await using var channel = new AgUiEventChannel();
        using var cts = new CancellationTokenSource();

        channel.Push(new RunStartedEvent { ThreadId = "t", RunId = "r" });

        var events = new List<AgUiEvent>();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var evt in channel.ReadAllAsync(cts.Token))
                events.Add(evt);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
