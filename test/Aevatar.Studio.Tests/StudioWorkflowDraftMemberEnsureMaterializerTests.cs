using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Workspace;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StudioWorkflowDraftMemberEnsureMaterializerTests
{
    private const string RootActorId = "studio-workspace:scope-1";

    // Refactor (iter1345/cluster-519-draft-member-authority):
    //   Old pattern: workflow draft saves and member authority creation could
    //   be tested as separate API-side effects.
    //   New principle: tests pin the projection fanout contract from committed
    //   draft event to typed EnsureStudioMember dispatch.
    [Fact]
    public async Task ProjectAsync_ShouldDispatchEnsureMember_ForCommittedDraftSaved()
    {
        var bootstrap = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap();
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var materializer = new StudioWorkflowDraftMemberEnsureMaterializer(
            bootstrap,
            StudioWorkflowDraftMemberCommandDispatchTestHarness.CreateCommandDispatch(dispatch),
            new StudioWorkflowDraftMemberEnsureCommandFactory());

        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(NewDraftSaved("workflow-1", "Workflow One"), version: 4, eventId: "evt-4"));

        bootstrap.EnsuredActorIds.Should().Equal("studio-member:scope-1:workflow-1");
        var dispatched = dispatch.Dispatches.Should().ContainSingle().Subject;
        dispatched.ActorId.Should().Be("studio-member:scope-1:workflow-1");
        dispatched.Envelope.Payload.Is(EnsureStudioMember.Descriptor).Should().BeTrue();
        var command = dispatched.Envelope.Payload.Unpack<EnsureStudioMember>();
        command.MemberId.Should().Be("workflow-1");
        command.ScopeId.Should().Be("scope-1");
        command.DisplayName.Should().Be("Workflow One");
        dispatched.Envelope.Runtime?.Deduplication?.OperationId.Should().Be(
            "aevatar.studio.projection.workflow-draft-member-ensure:scope-1:workflow-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldUseMemberIdAsDisplayName_WhenDraftNameIsBlank()
    {
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var materializer = new StudioWorkflowDraftMemberEnsureMaterializer(
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap(),
            StudioWorkflowDraftMemberCommandDispatchTestHarness.CreateCommandDispatch(dispatch),
            new StudioWorkflowDraftMemberEnsureCommandFactory());

        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(NewDraftSaved("workflow-1", "   "), version: 4, eventId: "evt-4"));

        var command = dispatch.Dispatches.Should().ContainSingle().Subject
            .Envelope.Payload.Unpack<EnsureStudioMember>();
        command.MemberId.Should().Be("workflow-1");
        command.DisplayName.Should().Be("workflow-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldUseStableCommandId_ForCommittedDraftReplay()
    {
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var materializer = new StudioWorkflowDraftMemberEnsureMaterializer(
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap(),
            StudioWorkflowDraftMemberCommandDispatchTestHarness.CreateCommandDispatch(dispatch),
            new StudioWorkflowDraftMemberEnsureCommandFactory());
        var envelope = WrapCommitted(NewDraftSaved("workflow-1", "Workflow One"), version: 4, eventId: "evt-4");

        await materializer.ProjectAsync(NewContext(), envelope);
        await materializer.ProjectAsync(NewContext(), envelope.Clone());

        dispatch.Dispatches.Should().HaveCount(2);
        dispatch.Dispatches[1].Envelope.Id.Should().Be(dispatch.Dispatches[0].Envelope.Id);
        dispatch.Dispatches[1].Envelope.Runtime?.Deduplication?.OperationId.Should().Be(
            dispatch.Dispatches[0].Envelope.Runtime?.Deduplication?.OperationId);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenCommittedEventIsNotDraftSaved()
    {
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var materializer = new StudioWorkflowDraftMemberEnsureMaterializer(
            new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap(),
            StudioWorkflowDraftMemberCommandDispatchTestHarness.CreateCommandDispatch(dispatch),
            new StudioWorkflowDraftMemberEnsureCommandFactory());

        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioWorkflowDraftDeleted
            {
                ScopeId = "scope-1",
                WorkflowId = "workflow-1",
                DeletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, version: 5, eventId: "evt-5"));

        dispatch.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldNoOp_WhenDraftIsMissingOrWorkflowIdIsBlank()
    {
        var bootstrap = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingBootstrap();
        var dispatch = new StudioWorkflowDraftMemberCommandDispatchTestHarness.RecordingDispatchPort();
        var materializer = new StudioWorkflowDraftMemberEnsureMaterializer(
            bootstrap,
            StudioWorkflowDraftMemberCommandDispatchTestHarness.CreateCommandDispatch(dispatch),
            new StudioWorkflowDraftMemberEnsureCommandFactory());

        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(new StudioWorkflowDraftSaved
            {
                WorkspaceId = RootActorId,
                ScopeId = "scope-1",
                SavedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
            }, version: 6, eventId: "evt-6"));
        await materializer.ProjectAsync(
            NewContext(),
            WrapCommitted(NewDraftSaved("   ", "Workflow Without Id"), version: 7, eventId: "evt-7"));

        bootstrap.EnsuredActorIds.Should().BeEmpty();
        dispatch.Dispatches.Should().BeEmpty();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = "studio-workspace",
    };

    private static StudioWorkflowDraftSaved NewDraftSaved(string workflowId, string name) => new()
    {
        WorkspaceId = RootActorId,
        ScopeId = "scope-1",
        Draft = new StudioWorkflowDraft
        {
            WorkflowId = workflowId,
            Name = name,
            FileName = $"{workflowId}.yaml",
            Yaml = "workflow: {}",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
        },
        SavedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T00:00:00Z")),
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        long version,
        string eventId) =>
        new()
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
                    AgentId = RootActorId,
                },
                StateRoot = Any.Pack(new StudioWorkspaceState
                {
                    WorkspaceId = RootActorId,
                    ScopeId = "scope-1",
                }),
            }),
        };

}
