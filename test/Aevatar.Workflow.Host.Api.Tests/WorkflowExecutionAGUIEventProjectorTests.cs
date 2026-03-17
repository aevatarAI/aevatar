using System.Runtime.CompilerServices;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
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
    public async Task ProjectAsync_ShouldPublishToContextCommandStream_WhenEnvelopeCorrelationDiffers()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionRunEventProjector(
            new StaticMapper([BuildRunFinished("actor-1", "done")]),
            streamHub);

        var context = BuildContext();
        var cmd1Sink = new RecordingSink();
        var cmd2Sink = new RecordingSink();

        await using var cmd1Subscription = await streamHub.SubscribeAsync("actor-1", "cmd-1", evt => cmd1Sink.PushAsync(evt));
        await using var cmd2Subscription = await streamHub.SubscribeAsync("actor-1", "cmd-2", evt => cmd2Sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "cmd-2",
            },
        });

        await cmd1Sink.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        cmd1Sink.SnapshotEvents().Should().ContainSingle(x => x.EventCase == WorkflowRunEventEnvelope.EventOneofCase.RunFinished);
        cmd2Sink.SnapshotEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WhenMapperReturnsEmpty_ShouldNotPublish()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionRunEventProjector(new StaticMapper([]), streamHub);

        var sink = new RecordingSink();
        await using var subscription = await streamHub.SubscribeAsync("actor-1", "cmd-1", evt => sink.PushAsync(evt));

        await projector.ProjectAsync(BuildContext(), new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "cmd-1",
            },
        });

        sink.SnapshotEvents().Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldPublishAllMappedRunEventsInOrder()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionRunEventProjector(
            new StaticMapper(
            [
                new WorkflowRunEventEnvelope
                {
                    Timestamp = 1,
                    RunStarted = new WorkflowRunStartedEventPayload { ThreadId = "t1" },
                },
                new WorkflowRunEventEnvelope
                {
                    Timestamp = 2,
                    TextMessageContent = new WorkflowTextMessageContentEventPayload { MessageId = "m1", Delta = "hi" },
                },
                new WorkflowRunEventEnvelope
                {
                    Timestamp = 3,
                    Custom = new WorkflowCustomEventPayload { Name = "custom" },
                },
            ]),
            streamHub);

        var sink = new RecordingSink();
        await using var subscription = await streamHub.SubscribeAsync("actor-1", "cmd-1", evt => sink.PushAsync(evt));

        await projector.ProjectAsync(BuildContext(), new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "cmd-1",
            },
        });

        await sink.WaitForCountAsync(3, TimeSpan.FromSeconds(2));
        sink.SnapshotEvents().Select(x => x.EventCase).Should().ContainInOrder(
            WorkflowRunEventEnvelope.EventOneofCase.RunStarted,
            WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent,
            WorkflowRunEventEnvelope.EventOneofCase.Custom);
    }

    [Fact]
    public async Task ProjectAsync_WhenContextCommandIdMissing_ShouldFallbackToEnvelopeCorrelationId()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionRunEventProjector(
            new StaticMapper([BuildRunFinished("actor-1", "done")]),
            streamHub);

        var context = BuildContext(string.Empty);
        var sink = new RecordingSink();

        await using var subscription = await streamHub.SubscribeAsync("actor-1", "cmd-fallback", evt => sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = "cmd-fallback",
            },
        });

        await sink.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        sink.SnapshotEvents().Should().ContainSingle();
    }

    [Fact]
    public async Task ProjectAsync_WhenNoCommandIdAvailable_ShouldNotPublish()
    {
        var streams = new InMemoryStreamProvider();
        var streamHub = new ProjectionSessionEventHub<WorkflowRunEventEnvelope>(
            streams,
            new WorkflowRunEventSessionCodec());
        var projector = new WorkflowExecutionRunEventProjector(
            new StaticMapper([BuildRunFinished("actor-1", "done")]),
            streamHub);

        var context = BuildContext(string.Empty);
        var sink = new RecordingSink();

        await using var subscription = await streamHub.SubscribeAsync("actor-1", "cmd-1", evt => sink.PushAsync(evt));

        await projector.ProjectAsync(context, new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        sink.SnapshotEvents().Should().BeEmpty();
    }

    private static WorkflowExecutionProjectionContext BuildContext(string sessionId = "cmd-1") => new()
    {
        SessionId = sessionId,
        RootActorId = "actor-1",
        ProjectionKind = "workflow-execution",
    };

    private static WorkflowRunEventEnvelope BuildRunFinished(string threadId, string output) =>
        new()
        {
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                ThreadId = threadId,
                Result = Any.Pack(new WorkflowRunResultPayload { Output = output }),
            },
        };

    private sealed class StaticMapper : IEventEnvelopeToWorkflowRunEventMapper
    {
        private readonly IReadOnlyList<WorkflowRunEventEnvelope> _events;

        public StaticMapper(IReadOnlyList<WorkflowRunEventEnvelope> events)
        {
            _events = events;
        }

        public IReadOnlyList<WorkflowRunEventEnvelope> Map(EventEnvelope envelope)
        {
            _ = envelope;
            return _events;
        }
    }

    private sealed class RecordingSink : IEventSink<WorkflowRunEventEnvelope>
    {
        private readonly object _gate = new();
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _countWaiters = [];
        private readonly List<WorkflowRunEventEnvelope> _events = [];

        public IReadOnlyList<WorkflowRunEventEnvelope> SnapshotEvents()
        {
            lock (_gate)
                return _events.ToList();
        }

        public Task WaitForCountAsync(int expectedCount, TimeSpan timeout)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_events.Count >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _countWaiters.Add((expectedCount, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
        }

        public void Push(WorkflowRunEventEnvelope evt) => Append(evt);

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Append(evt);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void Append(WorkflowRunEventEnvelope evt)
        {
            lock (_gate)
            {
                _events.Add(evt);
                for (var i = _countWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _countWaiters[i];
                    if (_events.Count < waiter.Count)
                        continue;

                    _countWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }
            }
        }
    }
}
