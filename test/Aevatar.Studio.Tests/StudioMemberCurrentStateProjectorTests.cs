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

/// <summary>
/// Locks in the projector contract: committed StudioMember state events are
/// materialized into <see cref="StudioMemberCurrentStateDocument"/> with the
/// denormalized roster fields populated, and unrelated payloads are
/// silently skipped (no spurious upserts).
/// </summary>
public sealed class StudioMemberCurrentStateProjectorTests
{
    private const string RootActorId = "studio-member:scope-1:m-1";

    [Fact]
    public async Task ProjectAsync_ShouldUpsertDocument_WhenCommittedStateEventArrives()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.Parse("2026-04-27T00:00:00Z"));
        var projector = new StudioMemberCurrentStateProjector(dispatcher, clock);

        var state = new StudioMemberState
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Test Member",
            Description = "desc",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            LifecycleStage = StudioMemberLifecycleStage.BindReady,
            CreatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "rev-9",
                },
            },
            LastBinding = new StudioMemberBindingContract
            {
                PublishedServiceId = "member-m-1",
                RevisionId = "rev-9",
                ImplementationKind = StudioMemberImplementationKind.Workflow,
                BoundAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            },
            Binding = new StudioMemberBindingAuthorityState
            {
                CurrentBindingRunId = "bind-1",
                CurrentStatus = StudioMemberBindingRunStatus.Succeeded,
                LastTerminalBindingRunId = "bind-1",
                UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            },
        };

        var envelope = WrapCommitted(
            payload: new StudioMemberBindingCompletedEvent { RevisionId = "rev-9" },
            state: state,
            version: 5,
            eventId: "evt-9");

        await projector.ProjectAsync(NewContext(), envelope);

        dispatcher.Upserts.Should().ContainSingle();
        var written = dispatcher.Upserts[0];

        // Document identity follows the actor-id convention.
        written.Id.Should().Be(RootActorId);
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(5);
        written.LastEventId.Should().Be("evt-9");

        // Denormalized roster fields are written so the query port doesn't
        // have to unpack state_root for ListAsync.
        written.MemberId.Should().Be("m-1");
        written.ScopeId.Should().Be("scope-1");
        written.DisplayName.Should().Be("Test Member");
        written.PublishedServiceId.Should().Be("member-m-1");
        written.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        written.LifecycleStage.Should().Be(MemberLifecycleStageNames.BindReady);

        // implementation_ref denormalized — no Any-pack of internal state.
        written.ImplementationWorkflowId.Should().Be("wf-1");
        written.ImplementationWorkflowRevision.Should().Be("rev-9");
        written.ImplementationScriptId.Should().BeEmpty();
        written.ImplementationActorTypeName.Should().BeEmpty();

        // last_binding denormalized.
        written.LastBoundPublishedServiceId.Should().Be("member-m-1");
        written.LastBoundRevisionId.Should().Be("rev-9");
        written.LastBoundImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        written.LastBoundAt.Should().NotBeNull();

        // async binding status denormalized from member authority state.
        written.BindingCurrentRunId.Should().Be("bind-1");
        written.BindingCurrentStatus.Should().Be(StudioMemberBindingRunStatusNames.Succeeded);
        written.BindingLastTerminalRunId.Should().Be("bind-1");
        written.BindingUpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProjectAsync_ShouldDenormalizeScriptImplementation()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberCurrentStateDocument>();
        var projector = new StudioMemberCurrentStateProjector(
            dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));

        var state = new StudioMemberState
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Script Member",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            LifecycleStage = StudioMemberLifecycleStage.BuildReady,
            CreatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            UpdatedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            ImplementationRef = new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef { ScriptId = "s-1", ScriptRevision = "v3" },
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioMemberImplementationUpdatedEvent(), state, 1, "evt-1"));

        var written = dispatcher.Upserts[0];
        written.ImplementationKind.Should().Be(MemberImplementationKindNames.Script);
        written.ImplementationScriptId.Should().Be("s-1");
        written.ImplementationScriptRevision.Should().Be("v3");
        written.ImplementationWorkflowId.Should().BeEmpty();
        written.LastBoundPublishedServiceId.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenPayloadIsNotCommittedStateEvent()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);
        var projector = new StudioMemberCurrentStateProjector(dispatcher, clock);

        // A bare event envelope without the CommittedStateEventPublished
        // wrapper must not produce a write — the projector is downstream of
        // committed events only.
        var envelope = new EventEnvelope
        {
            Id = "raw",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new StudioMemberCreatedEvent { MemberId = "m-1" }),
        };

        await projector.ProjectAsync(NewContext(), envelope);

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldRejectNullArguments()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);
        var projector = new StudioMemberCurrentStateProjector(dispatcher, clock);

        await FluentActions
            .Awaiting(() => projector.ProjectAsync(null!, new EventEnvelope()).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions
            .Awaiting(() => projector.ProjectAsync(NewContext(), null!).AsTask())
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var dispatcher = new RecordingWriteDispatcher<StudioMemberCurrentStateDocument>();
        var clock = new FixedProjectionClock(DateTimeOffset.UtcNow);

        FluentActions
            .Invoking(() => new StudioMemberCurrentStateProjector(null!, clock))
            .Should().Throw<ArgumentNullException>();
        FluentActions
            .Invoking(() => new StudioMemberCurrentStateProjector(dispatcher, null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = "studio-current-state",
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        StudioMemberState state,
        long version,
        string eventId)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
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
