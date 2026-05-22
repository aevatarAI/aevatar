using Aevatar.Foundation.Abstractions;
using Aevatar.Interop.A2A.Abstractions;
using Aevatar.Interop.A2A.Application;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Interop.A2A.Tests;

// Refactor (iter30/cluster-031-a2a-actor-owned):
//   Old pattern: readmodel behavior was implied by process-local task store state.
//   New principle: current-state projector tests assert materialization from committed task actor facts.
public class A2ATaskCurrentStateProjectorTests
{
    [Fact]
    public void TryProject_WithCommittedA2ATaskState_MapsAuthorityVersionAndEventFacts()
    {
        var observedAt = Timestamp.FromDateTime(DateTime.UtcNow);
        var state = new A2ATaskState
        {
            TaskId = "task-1",
            Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Submitted, observedAt),
            StateVersion = 3,
            LastEventId = "state-last",
            UpdatedAt = observedAt,
        };
        var envelope = BuildEnvelope(
            new CommittedStateEventPublished
            {
                StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(state),
                StateEvent = new StateEvent
                {
                    AgentId = "a2a-task:task-1",
                    EventId = "evt-7",
                    Version = 7,
                    Timestamp = observedAt,
                    EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new A2ATaskSubmittedEvent
                    {
                        EventId = "evt-7",
                        State = state,
                    }),
                },
            });

        var document = A2ATaskCurrentStateProjector.TryProject(envelope);

        document.Should().NotBeNull();
        document!.Id.Should().Be("a2a-task:task-1");
        document.ActorId.Should().Be("a2a-task:task-1");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");
        document.UpdatedAtUtcValue.Should().Be(observedAt);
        document.State.TaskId.Should().Be("task-1");
    }

    [Fact]
    public void TryProject_WithNonCommittedOrNonA2AState_ReturnsNull()
    {
        A2ATaskCurrentStateProjector.TryProject(BuildEnvelope(new StringValue { Value = "not committed" }))
            .Should().BeNull();

        A2ATaskCurrentStateProjector.TryProject(BuildEnvelope(new CommittedStateEventPublished
            {
                StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(new StringValue { Value = "wrong state" }),
                StateEvent = new StateEvent
                {
                    AgentId = "actor-1",
                    EventId = "evt-1",
                    Version = 1,
                },
            }))
            .Should().BeNull();
    }

    [Fact]
    public void TryProject_WithMissingActorOrTaskIdentity_ReturnsNull()
    {
        var timestamp = Timestamp.FromDateTime(DateTime.UtcNow);

        A2ATaskCurrentStateProjector.TryProject(BuildEnvelope(new CommittedStateEventPublished
            {
                StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(new A2ATaskState
                {
                    Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Submitted, timestamp),
                }),
                StateEvent = new StateEvent
                {
                    AgentId = "a2a-task:missing-task",
                    EventId = "evt-1",
                    Version = 1,
                },
            }))
            .Should().BeNull();

        A2ATaskCurrentStateProjector.TryProject(BuildEnvelope(new CommittedStateEventPublished
            {
                StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(new A2ATaskState
                {
                    TaskId = "task-1",
                    Status = A2ATaskModelMapper.BuildStatus(A2ATaskLifecycleState.Submitted, timestamp),
                }),
                StateEvent = new StateEvent
                {
                    EventId = "evt-1",
                    Version = 1,
                },
            }))
            .Should().BeNull();
    }

    private static EventEnvelope BuildEnvelope(Google.Protobuf.IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(payload),
        };
}
