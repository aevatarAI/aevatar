using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Registry;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class GAgentRegistryCurrentStateProjectorTests
{
    private const string RootActorId = "gagent-registry-scope-a";

    [Fact]
    public async Task ProjectAsync_ShouldStoreRenamedRegistryStateRoot()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new GAgentRegistryCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-06-04T08:00:00Z")));
        var state = new GAgentRegistryState
        {
            Groups =
            {
                new GAgentRegistryEntry
                {
                    AgentKind = "tests.registry-agent",
                    ActorIds = { "actor-1" },
                },
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ActorRegistrationKeyCanonicalizedEvent
                {
                    PreviousRegistryKey = "Legacy.Registry.Agent, Tests",
                    AgentKind = "tests.registry-agent",
                    ActorId = "actor-1",
                },
                state,
                version: 12,
                eventId: "evt-12"));

        dispatcher.Upserts.Should().ContainSingle();
        var written = dispatcher.Upserts[0];
        written.Id.Should().Be(RootActorId);
        written.StateVersion.Should().Be(12);
        written.LastEventId.Should().Be("evt-12");
        written.StateRoot.Is(GAgentRegistryState.Descriptor).Should().BeTrue();
        var unpacked = written.StateRoot.Unpack<GAgentRegistryState>();
        unpacked.Groups.Should().ContainSingle();
        unpacked.Groups[0].AgentKind.Should().Be("tests.registry-agent");
        unpacked.Groups[0].ActorIds.Should().ContainSingle("actor-1");
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = GAgentRegistryGAgent.ProjectionKind,
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        GAgentRegistryState state,
        long version,
        string eventId) =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-04T08:00:00Z")),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-04T08:00:00Z")),
                },
                StateRoot = Any.Pack(state),
            }),
        };

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<GAgentRegistryCurrentStateDocument>
    {
        public List<GAgentRegistryCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            GAgentRegistryCurrentStateDocument readModel,
            CancellationToken ct = default)
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
