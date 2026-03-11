using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Transport;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunEventSessionCodecCoverageTests
{
    [Fact]
    public void Channel_ShouldBeWorkflowRun()
    {
        new WorkflowRunEventSessionCodec().Channel.Should().Be("workflow-run");
    }

    [Fact]
    public void GetEventTypeAndSerialize_WhenEventIsNull_ShouldThrowArgumentNullException()
    {
        var codec = new WorkflowRunEventSessionCodec();

        Action getType = () => codec.GetEventType(null!);
        Action serialize = () => codec.Serialize(null!);

        getType.Should().Throw<ArgumentNullException>();
        serialize.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Deserialize_WhenEventTypeOrPayloadInvalid_ShouldReturnNull()
    {
        var codec = new WorkflowRunEventSessionCodec();
        var valid = new WorkflowRunEventEnvelope
        {
            RunStarted = new WorkflowRunStartedEventPayload { ThreadId = "thread-1" },
        };

        codec.Deserialize(null!, Any.Pack(new StringValue { Value = "payload" }).ToByteString()).Should().BeNull();
        codec.Deserialize(string.Empty, Any.Pack(new StringValue { Value = "payload" }).ToByteString()).Should().BeNull();
        codec.Deserialize(WorkflowRunEventTypes.RunStarted, null!).Should().BeNull();
        codec.Deserialize(WorkflowRunEventTypes.RunStarted, Any.Pack(new StringValue { Value = "payload" }).ToByteString()).Should().BeNull();
        codec.Deserialize("UNKNOWN", codec.Serialize(valid)).Should().BeNull();
    }

    [Fact]
    public void SerializeAndDeserialize_ShouldRoundTripRunFinishedEnvelope()
    {
        var codec = new WorkflowRunEventSessionCodec();
        var evt = new WorkflowRunEventEnvelope
        {
            Timestamp = 123,
            RunFinished = new WorkflowRunFinishedEventPayload
            {
                ThreadId = "thread-1",
                Result = Any.Pack(new WorkflowRunResultPayload { Output = "ok" }),
            },
        };

        var payload = codec.Serialize(evt);
        var deserialized = codec.Deserialize(WorkflowRunEventTypes.RunFinished, payload);

        deserialized.Should().NotBeNull();
        deserialized!.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunFinished);
        deserialized.Timestamp.Should().Be(123);
        deserialized.RunFinished.ThreadId.Should().Be("thread-1");
        deserialized.RunFinished.Result.Unpack<WorkflowRunResultPayload>().Output.Should().Be("ok");
    }

    [Fact]
    public void SerializeAndDeserialize_ShouldRoundTripStructuredStateSnapshot()
    {
        var codec = new WorkflowRunEventSessionCodec();
        var evt = new WorkflowRunEventEnvelope
        {
            Timestamp = 456,
            StateSnapshot = new WorkflowStateSnapshotEventPayload
            {
                Snapshot = Any.Pack(new WorkflowProjectionStateSnapshotPayload
                {
                    ActorId = "actor-1",
                    WorkflowName = "direct",
                    CommandId = "cmd-1",
                    ProjectionCompleted = true,
                    ProjectionCompletionStatus = WorkflowProjectionCompletionStatusPayload.Completed,
                    SnapshotAvailable = true,
                    Snapshot = new WorkflowActorSnapshotPayload
                    {
                        ActorId = "actor-1",
                        WorkflowName = "direct",
                        LastCommandId = "cmd-1",
                        TotalSteps = 2,
                    },
                }),
            },
        };

        var payload = codec.Serialize(evt);
        var deserialized = codec.Deserialize(WorkflowRunEventTypes.StateSnapshot, payload);

        deserialized.Should().NotBeNull();
        deserialized!.Timestamp.Should().Be(456);
        var snapshot = deserialized.StateSnapshot.Snapshot.Unpack<WorkflowProjectionStateSnapshotPayload>();
        snapshot.ActorId.Should().Be("actor-1");
        snapshot.Snapshot.TotalSteps.Should().Be(2);
    }

    [Fact]
    public void SerializeAndDeserialize_ShouldRoundTripCustomAnyPayload()
    {
        var codec = new WorkflowRunEventSessionCodec();
        var evt = new WorkflowRunEventEnvelope
        {
            Timestamp = 789,
            Custom = new WorkflowCustomEventPayload
            {
                Name = "custom",
                Payload = Any.Pack(new Int32Value { Value = 9 }),
            },
        };

        var payload = codec.Serialize(evt);
        var deserialized = codec.Deserialize(WorkflowRunEventTypes.Custom, payload);

        deserialized.Should().NotBeNull();
        deserialized!.Custom.Name.Should().Be("custom");
        deserialized.Custom.Payload.Unpack<Int32Value>().Value.Should().Be(9);
        deserialized.Timestamp.Should().Be(789);
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
            BuildStepStarted("step-1"),
            new InvalidOperationException("sink write failed"));

        handled.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(1);
        runEventHub.PublishedEvents.Should().ContainSingle();
        runEventHub.PublishedEvents.Single().evt.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunError);
        var runError = runEventHub.PublishedEvents.Single().evt.RunError;
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
            BuildRunStarted("thread-1"),
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
            BuildRunStarted("thread-1"),
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
            BuildRunStarted("thread-1"),
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

    private static WorkflowRunEventEnvelope BuildRunStarted(string threadId) =>
        new()
        {
            RunStarted = new WorkflowRunStartedEventPayload { ThreadId = threadId },
        };

    private static WorkflowRunEventEnvelope BuildStepStarted(string stepName) =>
        new()
        {
            StepStarted = new WorkflowStepStartedEventPayload { StepName = stepName },
        };

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingSinkSubscriptionManager
        : IEventSinkProjectionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>
    {
        public int DetachCalls { get; private set; }

        public Task AttachOrReplaceAsync(
            WorkflowExecutionRuntimeLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
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
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunEventHub : IProjectionSessionEventHub<WorkflowRunEventEnvelope>
    {
        public bool ThrowOnPublish { get; init; }

        public List<(string scopeId, string sessionId, WorkflowRunEventEnvelope evt)> PublishedEvents { get; } = [];

        public Task PublishAsync(string scopeId, string sessionId, WorkflowRunEventEnvelope evt, CancellationToken ct = default)
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
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
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

    private sealed class NoopRunEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        public void Push(WorkflowRunEventEnvelope evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
