using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkspaceCurrentStateProjectorTests
{
    private const string RootActorId = "studio-workspace-scope-1";

    [Fact]
    public async Task ProjectAsync_ShouldUpsertDocument_WhenCommittedWorkspaceStateArrives()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-05-19T08:00:00Z"));
        var projector = new StudioWorkspaceCurrentStateProjector(dispatcher, clock);
        var state = new StudioWorkspaceState
        {
            WorkspaceId = RootActorId,
            ScopeId = "scope-1",
            Settings = new StudioWorkspaceSettings
            {
                RuntimeBaseUrl = "http://127.0.0.1:5100",
            },
        };
        state.Directories.Add(new StudioWorkspaceDirectory
        {
            DirectoryId = "dir-1",
            Label = "Drafts",
            Path = "/tmp/drafts",
        });

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new StudioWorkspaceSettingsUpdated { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                state,
                version: 7,
                eventId: "evt-7"));

        dispatcher.Upserts.Should().ContainSingle();
        var written = dispatcher.Upserts[0];
        written.Id.Should().Be(RootActorId);
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(7);
        written.LastEventId.Should().Be("evt-7");
        written.UpdatedAt.Should().NotBeNull();
        written.StateRoot.Is(StudioWorkspaceState.Descriptor).Should().BeTrue();
        written.StateRoot.Unpack<StudioWorkspaceState>().Settings.RuntimeBaseUrl.Should().Be("http://127.0.0.1:5100");
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenEnvelopeIsUnrelatedOrInvalid()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new StudioWorkspaceCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            NewContext(),
            new EventEnvelope
            {
                Id = "raw",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Payload = Any.Pack(new StudioWorkspaceSettingsUpdated()),
            });

        await projector.ProjectAsync(
            NewContext(),
            new EventEnvelope
            {
                Id = "wrong-state",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-wrong",
                        Version = 1,
                        EventData = Any.Pack(new StudioWorkspaceSettingsUpdated()),
                    },
                    StateRoot = Any.Pack(new StringValue { Value = "not-workspace-state" }),
                }),
            });

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_WhenStateEventTimestampMissing_ShouldUseEnvelopeTimestamp()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-05-19T12:00:00Z"));
        var projector = new StudioWorkspaceCurrentStateProjector(dispatcher, clock);
        var envelopeTimestamp = DateTimeOffset.Parse("2026-05-19T11:00:00Z");

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new StudioWorkspaceDirectoryRemoved { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                new StudioWorkspaceState { WorkspaceId = RootActorId, ScopeId = "scope-1" },
                version: 8,
                eventId: "evt-8",
                envelopeTimestamp: envelopeTimestamp,
                stateEventTimestamp: null));

        dispatcher.Upserts.Should().ContainSingle();
        dispatcher.Upserts[0].UpdatedAt.ToDateTimeOffset().Should().Be(envelopeTimestamp);
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = StudioWorkspaceConventions.ProjectionKindValue,
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        StudioWorkspaceState state,
        long version,
        string eventId,
        DateTimeOffset? envelopeTimestamp = null,
        DateTimeOffset? stateEventTimestamp = null)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(envelopeTimestamp ?? DateTimeOffset.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = stateEventTimestamp.HasValue
                        ? Timestamp.FromDateTimeOffset(stateEventTimestamp.Value)
                        : null,
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<StudioWorkspaceCurrentStateDocument>
    {
        public List<StudioWorkspaceCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            StudioWorkspaceCurrentStateDocument readModel,
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
