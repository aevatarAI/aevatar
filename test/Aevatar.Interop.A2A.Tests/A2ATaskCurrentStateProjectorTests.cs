using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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
    public async Task ProjectAsync_WithCommittedA2ATaskState_WritesAuthorityVersionAndEventFacts()
    {
        var store = new RecordingWriteDispatcher();
        var projector = new A2ATaskCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-22T00:00:00+00:00")));
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

        await projector.ProjectAsync(BuildContext(), envelope);

        var document = store.Document;
        document.Should().NotBeNull();
        document!.Id.Should().Be("a2a-task:task-1");
        document.ActorId.Should().Be("a2a-task:task-1");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");
        document.UpdatedAtUtcValue.Should().Be(observedAt);
        document.State.TaskId.Should().Be("task-1");
    }

    [Fact]
    public async Task ProjectAsync_WithNonCommittedOrNonA2AState_SkipsWrite()
    {
        var store = new RecordingWriteDispatcher();
        var projector = new A2ATaskCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(BuildContext(), BuildEnvelope(new StringValue { Value = "not committed" }));
        await projector.ProjectAsync(BuildContext(), BuildEnvelope(new CommittedStateEventPublished
            {
                StateRoot = Google.Protobuf.WellKnownTypes.Any.Pack(new StringValue { Value = "wrong state" }),
                StateEvent = new StateEvent
                {
                    AgentId = "actor-1",
                    EventId = "evt-1",
                    Version = 1,
                },
            }));

        store.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ProjectAsync_WithMissingActorOrTaskIdentity_SkipsWrite()
    {
        var store = new RecordingWriteDispatcher();
        var projector = new A2ATaskCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));
        var timestamp = Timestamp.FromDateTime(DateTime.UtcNow);

        await projector.ProjectAsync(BuildContext(), BuildEnvelope(new CommittedStateEventPublished
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
            }));

        await projector.ProjectAsync(BuildContext(), BuildEnvelope(new CommittedStateEventPublished
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
            }));

        store.WriteCount.Should().Be(0);
    }

    private static A2ATaskProjectionContext BuildContext() =>
        new()
        {
            RootActorId = "a2a-task:task-1",
            ProjectionKind = "a2a-tasks",
        };

    private static EventEnvelope BuildEnvelope(Google.Protobuf.IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(payload),
        };

    private sealed class RecordingWriteDispatcher : IProjectionWriteDispatcher<A2ATaskCurrentStateReadModel>
    {
        public A2ATaskCurrentStateReadModel? Document { get; private set; }

        public int WriteCount { get; private set; }

        public Task<ProjectionWriteResult> UpsertAsync(
            A2ATaskCurrentStateReadModel readModel,
            CancellationToken ct = default)
        {
            Document = readModel;
            WriteCount++;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Applied());
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
