using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Projection.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests.Runtime;

public sealed class RuntimeFleetCapabilityAuthorityCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_WhenAuthorityOriginAndScopeMatch_ShouldWriteCurrentState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new RuntimeFleetCapabilityAuthorityCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-18T00:00:00Z")));

        await projector.ProjectAsync(
            CreateContext(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                RuntimeFleetCapabilityProjectionKinds.AuthorityCurrentState),
            CreateCommittedEnvelope(RuntimeFleetCapabilityAuthorityIdentity.ActorId));

        dispatcher.Upserts.Should().ContainSingle();
        dispatcher.Upserts[0].AuthorityActorId.Should()
            .Be(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        dispatcher.Upserts[0].StateVersion.Should().Be(7);
        dispatcher.Upserts[0].LastEventId.Should().Be("fleet-event-7");
    }

    [Theory]
    [InlineData(
        RuntimeFleetCapabilityAuthorityIdentity.ActorId,
        RuntimeFleetCapabilityProjectionKinds.AuthorityCurrentState,
        "forged-child")]
    [InlineData(
        "forged-root",
        RuntimeFleetCapabilityProjectionKinds.AuthorityCurrentState,
        RuntimeFleetCapabilityAuthorityIdentity.ActorId)]
    [InlineData(
        RuntimeFleetCapabilityAuthorityIdentity.ActorId,
        "forged-projection-kind",
        RuntimeFleetCapabilityAuthorityIdentity.ActorId)]
    public async Task ProjectAsync_WhenOriginOrScopeIsNotAuthority_ShouldNotWrite(
        string rootActorId,
        string projectionKind,
        string originActorId)
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new RuntimeFleetCapabilityAuthorityCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            CreateContext(rootActorId, projectionKind),
            CreateCommittedEnvelope(originActorId));

        dispatcher.Upserts.Should().BeEmpty();
    }

    private static RuntimeFleetCapabilityProjectionContext CreateContext(
        string rootActorId,
        string projectionKind) => new()
    {
        RootActorId = rootActorId,
        ProjectionKind = projectionKind,
    };

    private static EventEnvelope CreateCommittedEnvelope(string originActorId) => new()
    {
        Id = "fleet-envelope-7",
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-18T00:00:00Z")),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                AgentId = originActorId,
                EventId = "fleet-event-7",
                Version = 7,
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-08-18T00:00:00Z")),
                EventData = Any.Pack(new RuntimeFleetReconciliationRecordedEvent
                {
                    TransitionId = "transition-7",
                }),
            },
            StateRoot = Any.Pack(new RuntimeFleetCapabilityAuthorityState
            {
                LastReconcileTransitionId = "transition-7",
            }),
        }),
    };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<RuntimeFleetCapabilityAuthorityCurrentStateDocument>
    {
        public List<RuntimeFleetCapabilityAuthorityCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            RuntimeFleetCapabilityAuthorityCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(
            string id,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
