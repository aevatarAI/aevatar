using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunEventSessionCodecCoverageTests
{
    [Fact]
    public void Channel_ShouldBeWorkflowRun()
    {
        var codec = new WorkflowRunEventSessionCodec();

        codec.Channel.Should().Be("workflow-run");
    }

    [Fact]
    public void GetEventType_WhenEventIsNull_ShouldThrowArgumentNullException()
    {
        var codec = new WorkflowRunEventSessionCodec();

        Action act = () => codec.GetEventType(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Serialize_WhenEventIsNull_ShouldThrowArgumentNullException()
    {
        var codec = new WorkflowRunEventSessionCodec();

        Action act = () => codec.Serialize(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("RUN_STARTED", null)]
    [InlineData("RUN_STARTED", "")]
    [InlineData("UNKNOWN", "{}")]
    public void Deserialize_WhenEventTypeOrPayloadInvalid_ShouldReturnNull(string? eventType, string? payload)
    {
        var codec = new WorkflowRunEventSessionCodec();

        var deserialized = codec.Deserialize(eventType!, payload!);

        deserialized.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WhenPayloadIsMalformed_ShouldReturnNull()
    {
        var codec = new WorkflowRunEventSessionCodec();

        var deserialized = codec.Deserialize(WorkflowRunEventTypes.RunStarted, "{");

        deserialized.Should().BeNull();
    }

    [Fact]
    public void SerializeAndDeserialize_ShouldRoundTripKnownWorkflowEvent()
    {
        var codec = new WorkflowRunEventSessionCodec();
        var evt = new WorkflowRunFinishedEvent
        {
            ThreadId = "thread-1",
            Result = "ok",
            Timestamp = 123,
        };

        var payload = codec.Serialize(evt);
        var deserialized = codec.Deserialize(evt.Type, payload);

        deserialized.Should().BeOfType<WorkflowRunFinishedEvent>();
        var finished = (WorkflowRunFinishedEvent)deserialized!;
        finished.ThreadId.Should().Be("thread-1");
        finished.Result.Should().BeOfType<System.Text.Json.JsonElement>();
        ((System.Text.Json.JsonElement)finished.Result!).GetString().Should().Be("ok");
        finished.Timestamp.Should().Be(123);
    }
}

public sealed class WorkflowProjectionSinkFailurePolicyCoverageTests
{
    [Fact]
    public async Task TryHandleAsync_WhenInvalidOperation_ShouldDetachAndPublishRunError()
    {
        var sinkManager = new RecordingSinkSubscriptionManager();
        var runEventHub = new RecordingRunEventHub();
        var policy = new WorkflowProjectionSinkFailurePolicy(
            sinkManager,
            runEventHub,
            new FixedClock(new DateTimeOffset(2026, 2, 27, 5, 0, 0, TimeSpan.Zero)));

        var handled = await policy.TryHandleAsync(
            CreateLease("actor-1", "cmd-1"),
            new NoopRunEventSink(),
            new WorkflowStepStartedEvent { StepName = "step-1" },
            new InvalidOperationException("sink write failed"));

        handled.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(1);
        runEventHub.PublishedEvents.Should().ContainSingle();
        var runError = runEventHub.PublishedEvents.Single().evt.Should().BeOfType<WorkflowRunErrorEvent>().Subject;
        runError.Code.Should().Be(WorkflowProjectionSinkFailurePolicy.SinkWriteErrorCode);
        runError.Message.Should().Contain("eventType=STEP_STARTED");
    }

    [Fact]
    public async Task TryHandleAsync_WhenLeaseIdsMissing_ShouldDetachWithoutPublishing()
    {
        var sinkManager = new RecordingSinkSubscriptionManager();
        var runEventHub = new RecordingRunEventHub();
        var policy = new WorkflowProjectionSinkFailurePolicy(
            sinkManager,
            runEventHub,
            new FixedClock(new DateTimeOffset(2026, 2, 27, 5, 0, 0, TimeSpan.Zero)));

        var handled = await policy.TryHandleAsync(
            CreateLease(string.Empty, "   "),
            new NoopRunEventSink(),
            new WorkflowRunStartedEvent { ThreadId = "thread-1" },
            new EventSinkBackpressureException());

        handled.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(1);
        runEventHub.PublishedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleAsync_WhenRunErrorPublishFails_ShouldSwallowAndReturnTrue()
    {
        var sinkManager = new RecordingSinkSubscriptionManager();
        var runEventHub = new RecordingRunEventHub
        {
            ThrowOnPublish = true,
        };
        var policy = new WorkflowProjectionSinkFailurePolicy(
            sinkManager,
            runEventHub,
            new FixedClock(new DateTimeOffset(2026, 2, 27, 5, 0, 0, TimeSpan.Zero)));

        var handled = await policy.TryHandleAsync(
            CreateLease("actor-1", "cmd-1"),
            new NoopRunEventSink(),
            new WorkflowRunStartedEvent { ThreadId = "thread-1" },
            new EventSinkBackpressureException());

        handled.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(1);
    }

    [Fact]
    public async Task TryHandleAsync_WhenTokenCanceled_ShouldThrowOperationCanceledException()
    {
        var policy = new WorkflowProjectionSinkFailurePolicy(
            new RecordingSinkSubscriptionManager(),
            new RecordingRunEventHub(),
            new FixedClock(new DateTimeOffset(2026, 2, 27, 5, 0, 0, TimeSpan.Zero)));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => policy.TryHandleAsync(
            CreateLease("actor-1", "cmd-1"),
            new NoopRunEventSink(),
            new WorkflowRunStartedEvent { ThreadId = "thread-1" },
            new InvalidOperationException("write failed"),
            cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static WorkflowExecutionRuntimeLease CreateLease(string actorId, string commandId) =>
        new(new WorkflowExecutionProjectionContext
        {
            ProjectionId = actorId,
            RootActorId = actorId,
            CommandId = commandId,
            WorkflowName = "workflow",
            StartedAt = new DateTimeOffset(2026, 2, 27, 5, 0, 0, TimeSpan.Zero),
            Input = "input",
        });

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingSinkSubscriptionManager
        : IWorkflowProjectionSinkSubscriptionManager
    {
        public int DetachCalls { get; private set; }

        public Task AttachOrReplaceAsync(
            WorkflowExecutionRuntimeLease lease,
            IEventSink<WorkflowRunEvent> sink,
            Func<WorkflowRunEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DetachAsync(
            WorkflowExecutionRuntimeLease lease,
            IEventSink<WorkflowRunEvent> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunEventHub : IProjectionSessionEventHub<WorkflowRunEvent>
    {
        public bool ThrowOnPublish { get; init; }

        public List<(string scopeId, string sessionId, WorkflowRunEvent evt)> PublishedEvents { get; } = [];

        public Task PublishAsync(string scopeId, string sessionId, WorkflowRunEvent evt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOnPublish)
                throw new InvalidOperationException("publish failed");

            PublishedEvents.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowRunEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoopSubscription());
        }
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopRunEventSink : IEventSink<WorkflowRunEvent>
    {
        public void Push(WorkflowRunEvent evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(WorkflowRunEvent evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEvent> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
