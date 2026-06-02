using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class MappedCurrentStateProjectionMaterializerTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMapCommittedStateRoot_AndUpsertReadModel()
    {
        var dispatcher = new RecordingWriteDispatcher<TestStoreReadModel>();
        var projector = new TestMappedCurrentStateProjectionMaterializer(
            dispatcher,
            new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new TestContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "test-current-state",
        };

        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                version: 7,
                eventId: "evt-7",
                state: new StringValue { Value = "ready" },
                timestamp: DateTimeOffset.Parse("2026-03-17T11:07:00+00:00")));

        dispatcher.Upserts.Should().ContainSingle();
        var readModel = dispatcher.Upserts[0];
        readModel.Id.Should().Be("actor-1");
        readModel.ActorId.Should().Be("actor-1");
        readModel.StateVersion.Should().Be(7);
        readModel.LastEventId.Should().Be("evt-7");
        readModel.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-03-17T11:07:00+00:00"));
        readModel.Value.Should().Be("test-current-state:ready");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreInvalidEnvelope_AndMapperCanSkip()
    {
        var dispatcher = new RecordingWriteDispatcher<TestStoreReadModel>();
        var projector = new TestMappedCurrentStateProjectionMaterializer(
            dispatcher,
            new FixedClock(DateTimeOffset.Parse("2026-03-17T10:00:00+00:00")));
        var context = new TestContext
        {
            RootActorId = "actor-1",
            ProjectionKind = "test-current-state",
        };

        await projector.ProjectAsync(context, new EventEnvelope());
        await projector.ProjectAsync(
            context,
            BuildCommittedEnvelope(
                version: 8,
                eventId: "evt-8",
                state: new StringValue { Value = "skip" },
                timestamp: DateTimeOffset.Parse("2026-03-17T11:08:00+00:00")));

        dispatcher.Upserts.Should().BeEmpty();
    }

    private static EventEnvelope BuildCommittedEnvelope(
        long version,
        string eventId,
        StringValue state,
        DateTimeOffset timestamp)
    {
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(new StringValue { Value = "event-payload" }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class TestMappedCurrentStateProjectionMaterializer
        : MappedCurrentStateProjectionMaterializer<TestContext, StringValue, TestStoreReadModel>
    {
        public TestMappedCurrentStateProjectionMaterializer(
            IProjectionWriteDispatcher<TestStoreReadModel> writeDispatcher,
            IProjectionClock clock)
            : base(writeDispatcher, clock)
        {
        }

        protected override TestStoreReadModel? Map(
            MappedCurrentStateProjectionInput<TestContext, StringValue> input)
        {
            if (input.State.Value == "skip")
                return null;

            return new TestStoreReadModel
            {
                Id = input.Context.RootActorId,
                ActorId = input.Context.RootActorId,
                StateVersion = input.StateEvent.Version,
                LastEventId = input.StateEvent.EventId ?? string.Empty,
                UpdatedAt = input.ObservedAt,
                Value = $"{input.Context.ProjectionKind}:{input.State.Value}",
            };
        }
    }

    private sealed class TestContext : IProjectionMaterializationContext
    {
        public required string RootActorId { get; init; }

        public required string ProjectionKind { get; init; }
    }

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(ProjectionWriteResult.Duplicate());
    }
}
