using System.Runtime.CompilerServices;
using Aevatar.Foundation.Abstractions;
using Aevatar.Presentation.AGUI;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Projection;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionAGUIEventProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldOnlyPushToSinksMatchingEnvelopeCommandId()
    {
        var projector = new WorkflowExecutionAGUIEventProjector(new StaticMapper(
        [
            new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "run-1",
            },
        ]));

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

        context.AttachLiveSink("cmd-1", cmd1Sink);
        context.AttachLiveSink("cmd-2", cmd2Sink);

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            CorrelationId = "cmd-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        cmd1Sink.Events.Should().ContainSingle(x => x is WorkflowRunFinishedEvent);
        cmd2Sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WithoutCorrelationId_ShouldNotPushToAnySink()
    {
        var projector = new WorkflowExecutionAGUIEventProjector(new StaticMapper(
        [
            new RunFinishedEvent
            {
                ThreadId = "actor-1",
                RunId = "run-1",
            },
        ]));

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

        context.AttachLiveSink("cmd-1", cmd1Sink);
        context.AttachLiveSink("cmd-2", cmd2Sink);

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        cmd1Sink.Events.Should().BeEmpty();
        cmd2Sink.Events.Should().BeEmpty();
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
}
