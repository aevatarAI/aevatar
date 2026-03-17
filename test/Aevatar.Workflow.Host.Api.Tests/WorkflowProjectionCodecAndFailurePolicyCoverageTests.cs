using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
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
                        TotalSteps = 2,
                    },
                    ProjectionState = new WorkflowActorProjectionStatePayload
                    {
                        ActorId = "actor-1",
                        LastCommandId = "cmd-1",
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
