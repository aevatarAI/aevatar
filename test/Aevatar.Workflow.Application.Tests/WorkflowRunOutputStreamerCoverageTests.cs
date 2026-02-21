using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunOutputStreamerCoverageTests
{
    [Fact]
    public async Task StreamAsync_ShouldStopAfterTerminalEvent()
    {
        var channel = new WorkflowRunEventChannel();
        var streamer = new WorkflowRunOutputStreamer();
        var frames = new List<WorkflowOutputFrame>();

        channel.Push(new WorkflowRunStartedEvent { ThreadId = "actor-1", Timestamp = 1001 });
        channel.Push(new WorkflowTextMessageContentEvent { MessageId = "m1", Delta = "hello", Timestamp = 1002 });
        channel.Push(new WorkflowRunFinishedEvent { ThreadId = "actor-1", Result = "done", Timestamp = 1003 });
        channel.Push(new WorkflowCustomEvent { Name = "after_terminal", Value = "should_not_emit", Timestamp = 1004 });
        channel.Complete();

        await streamer.StreamAsync(channel, (frame, _) =>
        {
            frames.Add(frame);
            return ValueTask.CompletedTask;
        });

        frames.Should().HaveCount(3);
        frames[0].Type.Should().Be("RUN_STARTED");
        frames[1].Type.Should().Be("TEXT_MESSAGE_CONTENT");
        frames[2].Type.Should().Be("RUN_FINISHED");
        frames.Should().NotContain(f => f.Name == "after_terminal");
    }

    [Fact]
    public async Task PumpAsync_ShouldHonorCustomStopPredicate()
    {
        var streamer = new WorkflowRunOutputStreamer();
        var frames = new List<WorkflowOutputFrame>();
        var events = Stream(
            new WorkflowStepStartedEvent { StepName = "s1", Timestamp = 1 },
            new WorkflowStepFinishedEvent { StepName = "s1", Timestamp = 2 },
            new WorkflowStepStartedEvent { StepName = "s2", Timestamp = 3 });

        await streamer.PumpAsync(
            events,
            (frame, _) =>
            {
                frames.Add(frame);
                return ValueTask.CompletedTask;
            },
            shouldStop: evt => evt is WorkflowStepFinishedEvent);

        frames.Should().HaveCount(2);
        frames[0].Type.Should().Be("STEP_STARTED");
        frames[1].Type.Should().Be("STEP_FINISHED");
    }

    [Fact]
    public void Map_ShouldCoverKnownAndFallbackEventTypes()
    {
        var streamer = new WorkflowRunOutputStreamer();

        var started = streamer.Map(new WorkflowRunStartedEvent { ThreadId = "a1", Timestamp = 10 });
        started.Type.Should().Be("RUN_STARTED");
        started.ThreadId.Should().Be("a1");

        var finished = streamer.Map(new WorkflowRunFinishedEvent { ThreadId = "a1", Result = "ok", Timestamp = 11 });
        finished.Type.Should().Be("RUN_FINISHED");
        finished.Result.Should().Be("ok");

        var error = streamer.Map(new WorkflowRunErrorEvent { Message = "boom", Code = "E1", Timestamp = 12 });
        error.Type.Should().Be("RUN_ERROR");
        error.Message.Should().Be("boom");
        error.Code.Should().Be("E1");

        var stepStart = streamer.Map(new WorkflowStepStartedEvent { StepName = "step-a", Timestamp = 13 });
        stepStart.StepName.Should().Be("step-a");

        var stepFinish = streamer.Map(new WorkflowStepFinishedEvent { StepName = "step-a", Timestamp = 14 });
        stepFinish.StepName.Should().Be("step-a");

        var msgStart = streamer.Map(new WorkflowTextMessageStartEvent { MessageId = "m1", Role = "assistant", Timestamp = 15 });
        msgStart.MessageId.Should().Be("m1");
        msgStart.Role.Should().Be("assistant");

        var msgDelta = streamer.Map(new WorkflowTextMessageContentEvent { MessageId = "m1", Delta = "delta", Timestamp = 16 });
        msgDelta.Delta.Should().Be("delta");

        var msgEnd = streamer.Map(new WorkflowTextMessageEndEvent { MessageId = "m1", Timestamp = 17 });
        msgEnd.MessageId.Should().Be("m1");

        var snapshot = streamer.Map(new WorkflowStateSnapshotEvent { Snapshot = new { v = 1 }, Timestamp = 18 });
        snapshot.Type.Should().Be("STATE_SNAPSHOT");
        snapshot.Snapshot.Should().NotBeNull();

        var toolStart = streamer.Map(new WorkflowToolCallStartEvent { ToolCallId = "t1", ToolName = "search", Timestamp = 19 });
        toolStart.ToolCallId.Should().Be("t1");
        toolStart.ToolName.Should().Be("search");

        var toolEnd = streamer.Map(new WorkflowToolCallEndEvent { ToolCallId = "t1", Result = "{}", Timestamp = 20 });
        toolEnd.Result.Should().Be("{}");

        var custom = streamer.Map(new WorkflowCustomEvent { Name = "custom-name", Value = 7, Timestamp = 21 });
        custom.Name.Should().Be("custom-name");
        custom.Value.Should().Be(7);

        var unknown = streamer.Map(new UnknownWorkflowRunEvent { Timestamp = 22 });
        unknown.Type.Should().Be("UNKNOWN_EVENT");
        unknown.Timestamp.Should().Be(22);
    }

    private static async IAsyncEnumerable<WorkflowRunEvent> Stream(params WorkflowRunEvent[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed record UnknownWorkflowRunEvent : WorkflowRunEvent
    {
        public override string Type => "UNKNOWN_EVENT";
    }
}
