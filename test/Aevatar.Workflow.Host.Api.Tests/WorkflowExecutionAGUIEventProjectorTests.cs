using System.Runtime.CompilerServices;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionAGUIEventProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldPublishToMatchingCommandStreamOnly()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEvent>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionAGUIEventProjector(new StaticMapper(
        [
            new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "run-1",
            },
        ]), streamHub);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-1",
            CommandId = "cmd-1",
            RootActorId = "actor-1",
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        };

        var cmd1Sink = new RecordingSink();
        var cmd2Sink = new RecordingSink();

        await using var cmd1Subscription = await streamHub.SubscribeAsync(
            "actor-1",
            "cmd-1",
            evt => cmd1Sink.PushAsync(evt));
        await using var cmd2Subscription = await streamHub.SubscribeAsync(
            "actor-1",
            "cmd-2",
            evt => cmd2Sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = "cmd-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await WaitUntilAsync(() => cmd1Sink.Events.Count == 1);

        cmd1Sink.Events.Should().ContainSingle(x => x is WorkflowRunFinishedEvent);
        cmd2Sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithoutCorrelationId_ShouldNotPublishToAnyCommandStream()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEvent>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionAGUIEventProjector(new StaticMapper(
        [
            new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "run-1",
            },
        ]), streamHub);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-1",
            CommandId = "cmd-1",
            RootActorId = "actor-1",
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        };

        var cmd1Sink = new RecordingSink();
        var cmd2Sink = new RecordingSink();

        await using var cmd1Subscription = await streamHub.SubscribeAsync(
            "actor-1",
            "cmd-1",
            evt => cmd1Sink.PushAsync(evt));
        await using var cmd2Subscription = await streamHub.SubscribeAsync(
            "actor-1",
            "cmd-2",
            evt => cmd2Sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await Task.Delay(30);

        cmd1Sink.Events.Should().BeEmpty();
        cmd2Sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WhenMapperReturnsEmpty_ShouldNotPublish()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEvent>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionAGUIEventProjector(new StaticMapper([]), streamHub);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-1",
            CommandId = "cmd-1",
            RootActorId = "actor-1",
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        };

        var sink = new RecordingSink();
        await using var subscription = await streamHub.SubscribeAsync(
            "actor-1",
            "cmd-1",
            evt => sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = "cmd-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await Task.Delay(30);

        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldMapAllAGUIEventsToWorkflowRunEvents()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEvent>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionAGUIEventProjector(new StaticMapper(
        [
            new RunStartedEvent { ThreadId = "t1", RunId = "r1", Timestamp = 1 },
            new RunFinishedEvent { ThreadId = "t1", RunId = "r1", Result = "ok", Timestamp = 2 },
            new RunErrorEvent { Message = "boom", RunId = "r1", Code = "E", Timestamp = 3 },
            new StepStartedEvent { StepName = "s1", Timestamp = 4 },
            new StepFinishedEvent { StepName = "s1", Timestamp = 5 },
            new Aevatar.Presentation.AGUI.TextMessageStartEvent { MessageId = "m1", Role = "assistant", Timestamp = 6 },
            new Aevatar.Presentation.AGUI.TextMessageContentEvent { MessageId = "m1", Delta = "hi", Timestamp = 7 },
            new Aevatar.Presentation.AGUI.TextMessageEndEvent { MessageId = "m1", Timestamp = 8 },
            new StateSnapshotEvent { Snapshot = new { a = 1 }, Timestamp = 9 },
            new ToolCallStartEvent { ToolCallId = "tc1", ToolName = "search", Timestamp = 10 },
            new ToolCallEndEvent { ToolCallId = "tc1", Result = "{}", Timestamp = 11 },
            new CustomEvent { Name = "x", Value = 9, Timestamp = 12 },
            new UnknownAGUIEvent { Timestamp = 13 },
        ]), streamHub);

        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "actor-1",
            CommandId = "cmd-1",
            RootActorId = "actor-1",
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        };

        var sink = new RecordingSink();
        await using var subscription = await streamHub.SubscribeAsync(
            "actor-1",
            "cmd-1",
            evt => sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = "cmd-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        await WaitUntilAsync(() => sink.Events.Count == 13);

        sink.Events.Should().HaveCount(13);
        sink.Events.Should().SatisfyRespectively(
            e => e.Should().BeOfType<WorkflowRunStartedEvent>(),
            e => e.Should().BeOfType<WorkflowRunFinishedEvent>(),
            e => e.Should().BeOfType<WorkflowRunErrorEvent>(),
            e => e.Should().BeOfType<WorkflowStepStartedEvent>(),
            e => e.Should().BeOfType<WorkflowStepFinishedEvent>(),
            e => e.Should().BeOfType<WorkflowTextMessageStartEvent>(),
            e => e.Should().BeOfType<WorkflowTextMessageContentEvent>(),
            e => e.Should().BeOfType<WorkflowTextMessageEndEvent>(),
            e => e.Should().BeOfType<WorkflowStateSnapshotEvent>(),
            e => e.Should().BeOfType<WorkflowToolCallStartEvent>(),
            e => e.Should().BeOfType<WorkflowToolCallEndEvent>(),
            e => e.Should().BeOfType<WorkflowCustomEvent>(),
            e => e.Should().BeOfType<WorkflowCustomEvent>());

        var mappedCustomFromUnknown = sink.Events[^1].Should().BeOfType<WorkflowCustomEvent>().Subject;
        mappedCustomFromUnknown.Name.Should().Be("UNKNOWN_KIND");
        mappedCustomFromUnknown.Value.Should().BeNull();
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        int timeoutMs = 1000,
        int pollMs = 10)
    {
        var started = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - started).TotalMilliseconds < timeoutMs)
        {
            if (predicate())
                return;

            await Task.Delay(pollMs);
        }

        throw new TimeoutException("Condition not met before timeout.");
    }

    private sealed class StaticMapper : IEventEnvelopeToAGUIEventMapper
    {
        private readonly IReadOnlyList<AGUIEvent> _events;

        public StaticMapper(IReadOnlyList<AGUIEvent> events)
        {
            _events = events;
        }

        public IReadOnlyList<AGUIEvent> Map(EventEnvelope envelope)
        {
            _ = envelope;
            return _events;
        }
    }

    private sealed class RecordingSink : IWorkflowRunEventSink
    {
        public List<WorkflowRunEvent> Events { get; } = [];

        public void Push(WorkflowRunEvent evt)
        {
            Events.Add(evt);
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed record UnknownAGUIEvent : AGUIEvent
    {
        public override string Type => "UNKNOWN_KIND";
    }
}
