using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using System.Threading.Channels;

namespace Aevatar.Workflow.Host.Api.Tests;

public class WorkflowRunEventSinkChannelTests
{
    [Fact]
    public async Task PushAsync_DefaultWaitMode_WhenBufferFull_ShouldWaitUntilConsumerReads()
    {
        await using var channel = new EventChannel<WorkflowRunEventEnvelope>(capacity: 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        channel.Push(new WorkflowRunEventEnvelope
        {
            StepStarted = new WorkflowStepStartedEventPayload { StepName = "s1" },
        });

        var terminalPush = channel.PushAsync(new WorkflowRunEventEnvelope
        {
            RunFinished = new WorkflowRunFinishedEventPayload { ThreadId = "actor-1" },
        }, cts.Token).AsTask();

        terminalPush.IsCompleted.Should().BeFalse();

        await using var enumerator = channel.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.StepStarted);

        await terminalPush;
        channel.Complete();

        (await enumerator.MoveNextAsync()).Should().BeTrue();
        enumerator.Current.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunFinished);
    }

    [Fact]
    public async Task PushAsync_WhenCompleted_ShouldThrowCompletedException()
    {
        await using var channel = new EventChannel<WorkflowRunEventEnvelope>();
        channel.Complete();

        var act = async () => await channel.PushAsync(new WorkflowRunEventEnvelope
        {
            RunError = new WorkflowRunErrorEventPayload
            {
                Message = "boom",
                Code = "E",
            },
        });

        await act.Should().ThrowAsync<EventSinkCompletedException>();
    }

    [Fact]
    public async Task Push_WhenWaitModeAndBufferFull_ShouldThrowBackpressureException()
    {
        await using var channel = new EventChannel<WorkflowRunEventEnvelope>(
            capacity: 1,
            fullMode: BoundedChannelFullMode.Wait);

        channel.Push(new WorkflowRunEventEnvelope
        {
            StepStarted = new WorkflowStepStartedEventPayload { StepName = "s1" },
        });

        var act = () => channel.Push(new WorkflowRunEventEnvelope
        {
            StepFinished = new WorkflowStepFinishedEventPayload { StepName = "s1" },
        });
        act.Should().Throw<EventSinkBackpressureException>();
    }
}
