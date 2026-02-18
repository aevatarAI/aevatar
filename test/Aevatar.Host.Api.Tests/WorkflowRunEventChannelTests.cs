using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using System.Threading.Channels;

namespace Aevatar.Host.Api.Tests;

public class WorkflowRunEventChannelTests
{
    [Fact]
    public async Task PushAsync_DefaultWaitMode_WhenBufferFull_ShouldWaitUntilConsumerReads()
    {
        await using var channel = new WorkflowRunEventChannel(capacity: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        channel.Push(new WorkflowStepStartedEvent { StepName = "s1" });

        var terminalPush = channel.PushAsync(new WorkflowRunFinishedEvent
        {
            ThreadId = "actor-1",
        }, cts.Token).AsTask();

        await Task.Delay(50, cts.Token);
        terminalPush.IsCompleted.Should().BeFalse();

        await using var enumerator = channel.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().BeOfType<WorkflowStepStartedEvent>();

        await terminalPush;
        channel.Complete();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.Should().BeOfType<WorkflowRunFinishedEvent>();
    }

    [Fact]
    public async Task PushAsync_WhenCompleted_ShouldThrowCompletedException()
    {
        await using var channel = new WorkflowRunEventChannel();
        channel.Complete();

        var act = async () => await channel.PushAsync(new WorkflowRunErrorEvent
        {
            Message = "boom",
            Code = "E",
        });

        await act.Should().ThrowAsync<WorkflowRunEventSinkCompletedException>();
    }

    [Fact]
    public async Task Push_WhenWaitModeAndBufferFull_ShouldThrowBackpressureException()
    {
        await using var channel = new WorkflowRunEventChannel(
            capacity: 1,
            fullMode: BoundedChannelFullMode.Wait);

        channel.Push(new WorkflowStepStartedEvent { StepName = "s1" });

        var act = () => channel.Push(new WorkflowStepFinishedEvent { StepName = "s1" });
        act.Should().Throw<WorkflowRunEventSinkBackpressureException>();
    }
}
