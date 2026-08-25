using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberBindingRunCurrentStateProjectorTests
{
    private const string RootActorId = "studio-member-binding-run:bind-1";

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeBindingRunAuthorityState()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberBindingRunCurrentStateDocument>();
        var projector = new StudioMemberBindingRunCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-30T00:00:00Z")));
        var updatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-30T08:00:00Z"));
        var state = new StudioMemberBindingRunState
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = "hash-1",
            Status = StudioMemberBindingRunStatus.PlatformBindingPending,
            PlatformBindingCommandId = "platform-bind-1",
            AttemptCount = 1,
            PlatformExecutionStage = StudioMemberPlatformBindingExecutionStage.ReadinessInFlight,
            PlatformExecutionAttempt = 4,
            LastPlatformReadinessStatus = StudioMemberPlatformBindingReadinessStatus.ServingSetMissing,
            UpdatedAtUtc = updatedAt,
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioMemberPlatformBindingAccepted(), state, 4, "evt-4"));

        var written = dispatcher.Upserts.Should().ContainSingle().Which;
        written.Id.Should().Be(RootActorId);
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(4);
        written.LastEventId.Should().Be("evt-4");
        written.BindingRunId.Should().Be("bind-1");
        written.ScopeId.Should().Be("scope-1");
        written.MemberId.Should().Be("m-1");
        written.Status.Should().Be(StudioMemberBindingRunStatusNames.PlatformBindingPending);
        written.PlatformBindingCommandId.Should().Be("platform-bind-1");
        written.AttemptCount.Should().Be(1);
        written.PlatformExecutionStage.Should().Be("readiness_in_flight");
        written.PlatformExecutionAttempt.Should().Be(4);
        written.LastReadinessStatus.Should().Be("serving_set_missing");
        written.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeFailureAndPlatformResult()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberBindingRunCurrentStateDocument>();
        var projector = new StudioMemberBindingRunCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-30T00:00:00Z")));
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-30T08:10:00Z"));
        var state = new StudioMemberBindingRunState
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = "hash-1",
            Status = StudioMemberBindingRunStatus.Succeeded,
            PlatformBindingCommandId = "platform-bind-1",
            AttemptCount = 2,
            Failure = new StudioMemberBindingFailure
            {
                Code = "PREVIOUS_FAILURE",
                Message = "Recovered on retry",
                FailedAtUtc = failedAt,
            },
            PlatformResult = new StudioMemberPlatformBindingResult
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-2",
                ImplementationKind = StudioMemberImplementationKind.Gagent,
                ExpectedActorId = "scope-gagent:scope-1:m-1",
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioMemberBindingTerminalAcknowledged(), state, 7, "evt-7"));

        var written = dispatcher.Upserts.Should().ContainSingle().Which;
        written.Status.Should().Be(StudioMemberBindingRunStatusNames.Succeeded);
        written.FailureCode.Should().Be("PREVIOUS_FAILURE");
        written.FailureMessage.Should().Be("Recovered on retry");
        written.FailureAt.Should().Be(failedAt);
        written.ResultPublishedServiceId.Should().Be("member-m-1");
        written.ResultRevisionId.Should().Be("rev-2");
        written.ResultImplementationKind.Should().Be(MemberImplementationKindNames.GAgent);
        written.ResultExpectedActorId.Should().Be("scope-gagent:scope-1:m-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreNonCommittedStateEnvelope()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberBindingRunCurrentStateDocument>();
        var projector = new StudioMemberBindingRunCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-30T00:00:00Z")));

        await projector.ProjectAsync(
            NewContext(),
            new EventEnvelope
            {
                Id = "evt-raw",
                Payload = Any.Pack(new StudioMemberPlatformBindingAccepted()),
            });

        dispatcher.Upserts.Should().BeEmpty();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = StudioMemberBindingRunGAgent.ProjectionKind,
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        StudioMemberBindingRunState state,
        long version,
        string eventId)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-30T08:00:00Z")),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    Version = version,
                    EventId = eventId,
                    EventData = Any.Pack(payload),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, Aevatar.CQRS.Projection.Stores.Abstractions.IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            Upserts.Add(readModel);
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
