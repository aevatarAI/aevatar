// ─── AGUIEventChannel 测试 ───
// 验证 Channel 驱动的事件收集器

using Aevatar.Presentation.AGUI;
using FluentAssertions;
using System.Threading.Channels;

namespace Aevatar.Workflow.Host.Api.Tests;

public class AGUIEventChannelTests
{
    [Fact]
    public async Task PushAndRead_RoundTrip()
    {
        await using var channel = new AGUIEventChannel();

        channel.Push(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
        });
        channel.Push(new AGUIEvent
        {
            StepStarted = new StepStartedEvent { StepName = "step1" },
        });
        channel.Complete();

        var events = new List<AGUIEvent>();
        await foreach (var evt in channel.ReadAllAsync())
            events.Add(evt);

        events.Should().HaveCount(2);
        events[0].EventCase.Should().Be(AGUIEvent.EventOneofCase.RunStarted);
        events[0].RunStarted.ThreadId.Should().Be("t1");
        events[1].EventCase.Should().Be(AGUIEvent.EventOneofCase.StepStarted);
        events[1].StepStarted.StepName.Should().Be("step1");
    }

    [Fact]
    public async Task Complete_TerminatesReader()
    {
        await using var channel = new AGUIEventChannel();

        var readTask = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in channel.ReadAllAsync())
                count++;
            return count;
        });

        channel.Push(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t", RunId = "r" },
        });
        channel.Complete();

        var result = await readTask;
        result.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_StopsReader()
    {
        await using var channel = new AGUIEventChannel();
        using var cts = new CancellationTokenSource();

        channel.Push(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t", RunId = "r" },
        });

        var events = new List<AGUIEvent>();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var evt in channel.ReadAllAsync(cts.Token))
                events.Add(evt);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Push_WhenChannelFull_ShouldThrow()
    {
        await using var channel = new AGUIEventChannel(new AGUIEventChannelOptions
        {
            Capacity = 1,
            FullMode = BoundedChannelFullMode.Wait,
        });

        channel.Push(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
        });
        var act = () => channel.Push(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t2", RunId = "r2" },
        });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task PushAsync_WhenChannelFullWithWaitMode_ShouldResumeAfterRead()
    {
        await using var channel = new AGUIEventChannel(new AGUIEventChannelOptions
        {
            Capacity = 1,
            FullMode = BoundedChannelFullMode.Wait,
        });

        channel.Push(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t1", RunId = "r1" },
        });

        var pushTask = channel.PushAsync(new AGUIEvent
        {
            RunStarted = new RunStartedEvent { ThreadId = "t2", RunId = "r2" },
        }).AsTask();
        pushTask.IsCompleted.Should().BeFalse();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var _ in channel.ReadAllAsync(cts.Token))
        {
            channel.Complete();
            break;
        }

        await pushTask;
    }
}
