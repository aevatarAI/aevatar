using Aevatar.CQRS.Core.Streaming;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunEventOutputStreamCoverageTests
{
    [Fact]
    public async Task PumpAsync_ShouldStopAfterTerminalEvent()
    {
        var channel = new EventChannel<WorkflowRunEventEnvelope>();
        var stream = new DefaultEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>(
            new IdentityEventFrameMapper<WorkflowRunEventEnvelope>());
        var frames = new List<WorkflowRunEventEnvelope>();

        channel.Push(new WorkflowRunEventEnvelope
        {
            Timestamp = 1001,
            RunStarted = new WorkflowRunStartedEventPayload { ThreadId = "actor-1" },
        });
        channel.Push(new WorkflowRunEventEnvelope
        {
            Timestamp = 1002,
            TextMessageContent = new WorkflowTextMessageContentEventPayload { MessageId = "m1", Delta = "hello" },
        });
        channel.Push(new WorkflowRunEventEnvelope
        {
            Timestamp = 1003,
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                ThreadId = "actor-1",
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "done" }),
            },
        });
        channel.Push(new WorkflowRunEventEnvelope
        {
            Timestamp = 1004,
            Custom = new WorkflowCustomEventPayload { Name = "after_terminal" },
        });
        channel.Complete();

        await stream.PumpAsync(
            channel.ReadAllAsync(CancellationToken.None),
            (frame, _) =>
            {
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            evt => evt.EventCase is WorkflowRunEventEnvelope.EventOneofCase.RunFinished
                or WorkflowRunEventEnvelope.EventOneofCase.RunError);

        frames.Should().HaveCount(3);
        frames[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunStarted);
        frames[1].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent);
        frames[2].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunFinished);
        frames.Any(f => f.EventCase == WorkflowRunEventEnvelope.EventOneofCase.Custom && f.Custom.Name == "after_terminal")
            .Should().BeFalse();
    }

    [Fact]
    public async Task PumpAsync_ShouldHonorCustomStopPredicate()
    {
        var stream = new DefaultEventOutputStream<WorkflowRunEventEnvelope, WorkflowRunEventEnvelope>(
            new IdentityEventFrameMapper<WorkflowRunEventEnvelope>());
        var frames = new List<WorkflowRunEventEnvelope>();
        var events = Stream(
            new WorkflowRunEventEnvelope
            {
                Timestamp = 1,
                StepStarted = new WorkflowStepStartedEventPayload { StepName = "s1" },
            },
            new WorkflowRunEventEnvelope
            {
                Timestamp = 2,
                StepFinished = new WorkflowStepFinishedEventPayload { StepName = "s1" },
            },
            new WorkflowRunEventEnvelope
            {
                Timestamp = 3,
                StepStarted = new WorkflowStepStartedEventPayload { StepName = "s2" },
            });

        await stream.PumpAsync(
            events,
            (frame, _) =>
            {
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            shouldStop: evt => evt.EventCase == WorkflowRunEventEnvelope.EventOneofCase.StepFinished);

        frames.Should().HaveCount(2);
        frames[0].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.StepStarted);
        frames[1].EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.StepFinished);
    }

    [Fact]
    public void IdentityMapper_ShouldReturnTheOriginalEnvelope()
    {
        var mapper = new IdentityEventFrameMapper<WorkflowRunEventEnvelope>();

        var evt = new WorkflowRunEventEnvelope
        {
            Timestamp = 18,
            StateSnapshot = new WorkflowStateSnapshotEventPayload
            {
                Snapshot = Any.Pack(new WorkflowProjectionStateSnapshotPayload
                {
                    ActorId = "actor-1",
                    WorkflowName = "direct",
                    CommandId = "cmd-1",
                    ProjectionCompleted = true,
                    ProjectionCompletionStatus = WorkflowProjectionCompletionStatusPayload.Completed,
                }),
            },
        };

        var mapped = mapper.Map(evt);

        mapped.Should().BeSameAs(evt);
        mapped.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.StateSnapshot);
        mapped.StateSnapshot.Snapshot.Is(WorkflowProjectionStateSnapshotPayload.Descriptor).Should().BeTrue();
    }

    private static async IAsyncEnumerable<WorkflowRunEventEnvelope> Stream(params WorkflowRunEventEnvelope[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}
