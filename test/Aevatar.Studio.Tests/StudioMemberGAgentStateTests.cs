using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StudioMember;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Tests the StudioMember state machine in isolation by feeding events
/// directly into the GAgent's <c>TransitionState</c>. Reflection bridges to
/// the protected method so we can lock in the rename-safe publishedServiceId
/// invariant from the issue without standing up the full actor runtime.
/// </summary>
public sealed class StudioMemberGAgentStateTests
{
    private readonly StudioMemberStateApplier _agent = new();

    // Refactor (iter1345/cluster-519-draft-member-authority):
    //   Old pattern: tests covered only direct create semantics, leaving the
    //   draft projection ensure command contract implicit.
    //   New principle: behavior tests lock the actor-owned idempotency and
    //   conflict rules for the typed ensure-member command path.
    [Fact]
    public async Task HandleEnsureStudioMember_ShouldPersistCreatedEvent_WhenMissing()
    {
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var eventSourcing = new RecordingEventSourcing(new StudioMemberState());
        var agent = NewHandlerAgent(new StudioMemberState(), eventSourcing, new RecordingEventPublisher());

        await agent.HandleEnsureStudioMember(new EnsureStudioMember
        {
            MemberId = "workflow-1",
            ScopeId = "scope-1",
            DisplayName = "Workflow 1",
            Description = "draft member",
            RequestedAtUtc = requestedAt,
        });

        var created = eventSourcing.RaisedEvents.Should().ContainSingle().Subject
            .Should().BeOfType<StudioMemberCreatedEvent>().Subject;
        created.MemberId.Should().Be("workflow-1");
        created.ScopeId.Should().Be("scope-1");
        created.DisplayName.Should().Be("Workflow 1");
        created.Description.Should().Be("draft member");
        created.ImplementationKind.Should().Be(StudioMemberImplementationKind.Workflow);
        created.PublishedServiceId.Should().Be(StudioMemberConventions.BuildPublishedServiceId("workflow-1"));
        created.CreatedAtUtc.Should().Be(requestedAt);
        eventSourcing.ConfirmCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleEnsureStudioMember_ShouldNoOp_WhenSameMemberAlreadyExists()
    {
        var existing = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "workflow-1",
            ScopeId = "scope-1",
            DisplayName = "Existing",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-workflow-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var eventSourcing = new RecordingEventSourcing(existing);
        var agent = NewHandlerAgent(existing, eventSourcing, new RecordingEventPublisher());

        await agent.HandleEnsureStudioMember(new EnsureStudioMember
        {
            MemberId = "workflow-1",
            ScopeId = "scope-1",
            DisplayName = "Ignored",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        eventSourcing.RaisedEvents.Should().BeEmpty();
        eventSourcing.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleEnsureStudioMember_ShouldReject_WhenExistingAuthorityDoesNotMatchCommandTarget()
    {
        var existing = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "workflow-1",
            ScopeId = "scope-1",
            DisplayName = "Existing",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-workflow-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var eventSourcing = new RecordingEventSourcing(existing);
        var agent = NewHandlerAgent(existing, eventSourcing, new RecordingEventPublisher());

        Func<Task> act = () => agent.HandleEnsureStudioMember(new EnsureStudioMember
        {
            MemberId = "workflow-2",
            ScopeId = "scope-2",
            DisplayName = "Conflicting workflow",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*member already initialized as 'scope-1/workflow-1'*");
        eventSourcing.RaisedEvents.Should().BeEmpty();
        eventSourcing.ConfirmCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleCreated_ShouldRejectDuplicate_WhenCoreCreateFieldsDiffer()
    {
        var existing = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var eventSourcing = new RecordingEventSourcing(existing);
        var agent = NewHandlerAgent(existing, eventSourcing, new RecordingEventPublisher());

        var act = () => agent.HandleCreated(new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Changed",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName / description / implementationKind*");
        eventSourcing.RaisedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleCreated_ShouldNoOp_WhenDuplicateCreateWithoutRefArrivesAfterImplementationUpdate()
    {
        var createdAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var originalCreate = new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = createdAt,
        };
        var created = _agent.Apply(new StudioMemberState(), originalCreate);
        var updated = _agent.Apply(created, new StudioMemberImplementationUpdatedEvent
        {
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "rev-1",
                },
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(updated);
        var agent = NewHandlerAgent(updated, eventSourcing, new RecordingEventPublisher());

        var act = () => agent.HandleCreated(originalCreate.Clone());

        await act.Should().NotThrowAsync();
        eventSourcing.RaisedEvents.Should().BeEmpty();
        updated.ImplementationRef.Should().NotBeNull();
        updated.ImplementationRef.Workflow.WorkflowId.Should().Be("wf-1");
        updated.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task HandleCreated_ShouldNoOp_WhenDuplicateCreateWithOriginalRefArrivesAfterImplementationRevisionUpdate()
    {
        var createdAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var originalCreate = new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = createdAt,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "rev-a",
                },
            },
        };
        var created = _agent.Apply(new StudioMemberState(), originalCreate);
        var updated = _agent.Apply(created, new StudioMemberImplementationUpdatedEvent
        {
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "rev-b",
                },
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(updated);
        var agent = NewHandlerAgent(updated, eventSourcing, new RecordingEventPublisher());

        var act = () => agent.HandleCreated(originalCreate.Clone());

        await act.Should().NotThrowAsync();
        eventSourcing.RaisedEvents.Should().BeEmpty();
        updated.ImplementationRef.Should().NotBeNull();
        updated.ImplementationRef.Workflow.WorkflowId.Should().Be("wf-1");
        updated.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-b");
    }

    [Fact]
    public void Created_ShouldPersistPublishedServiceId()
    {
        var initial = new StudioMemberState();
        var createdAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var afterCreate = _agent.Apply(initial, new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = createdAt,
        });

        afterCreate.MemberId.Should().Be("m-1");
        afterCreate.PublishedServiceId.Should().Be("member-m-1");
        afterCreate.LifecycleStage.Should().Be(StudioMemberLifecycleStage.Created);
    }

    [Fact]
    public void Created_WithHistoricalImplementationRef_ShouldReplayActorOwnedState()
    {
        var createdAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var afterCreate = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = createdAt,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-1",
                },
            },
        });

        afterCreate.ImplementationRef.Should().NotBeNull();
        afterCreate.ImplementationRef.Script.ScriptId.Should().Be("script-1");
        afterCreate.ImplementationRef.Script.ScriptRevision.Should().Be("rev-1");
        afterCreate.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BuildReady);
    }

    [Fact]
    public void Renamed_ShouldNotChangePublishedServiceId()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var renamed = _agent.Apply(created, new StudioMemberRenamedEvent
        {
            DisplayName = "Renamed Member",
            Description = "Now with different name",
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        // Acceptance criterion from issue #325:
        //   "publishedServiceId is backend-generated, stable, and rename-safe"
        renamed.PublishedServiceId.Should().Be(created.PublishedServiceId);
        renamed.MemberId.Should().Be(created.MemberId);
        renamed.DisplayName.Should().Be("Renamed Member");
        renamed.Description.Should().Be("Now with different name");
    }

    [Fact]
    public async Task HandleRenamed_ShouldPreserveDescription_WhenOnlyDisplayNameChanges()
    {
        var current = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            Description = "Existing description",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var updatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));
        var eventSourcing = new RecordingEventSourcing(current);
        var agent = NewHandlerAgent(current, eventSourcing, new RecordingEventPublisher());

        await agent.HandleRenamed(new StudioMemberRenamedEvent
        {
            DisplayName = "Renamed Workflow",
            UpdatedAtUtc = updatedAt,
        });

        var renamed = eventSourcing.RaisedEvents.Should().ContainSingle().Subject
            .Should().BeOfType<StudioMemberRenamedEvent>().Subject;
        renamed.DisplayName.Should().Be("Renamed Workflow");
        renamed.Description.Should().Be("Existing description");
        renamed.UpdatedAtUtc.Should().Be(updatedAt);
    }

    [Fact]
    public void ImplementationUpdated_ShouldAdvanceLifecycleToBuildReady()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        created.LifecycleStage.Should().Be(StudioMemberLifecycleStage.Created);

        var withImpl = _agent.Apply(created, new StudioMemberImplementationUpdatedEvent
        {
            ImplementationKind = StudioMemberImplementationKind.Script,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef
                {
                    ScriptId = "s-1",
                    ScriptRevision = "v1",
                },
            },
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        withImpl.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BuildReady);
        withImpl.ImplementationRef.Should().NotBeNull();
        withImpl.ImplementationRef.Script.ScriptId.Should().Be("s-1");
    }

    [Fact]
    public void Reassigned_PureAssign_ShouldSetTeamId()
    {
        // Pure assign: from_team_id absent, to_team_id = "T".
        // Member starts unassigned (no team_id field set).
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        created.HasTeamId.Should().BeFalse();

        var reassignedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));
        var assigned = _agent.Apply(created, new StudioMemberReassignedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            ToTeamId = "team-1",
            ReassignedAtUtc = reassignedAt,
        });

        assigned.HasTeamId.Should().BeTrue();
        assigned.TeamId.Should().Be("team-1");
        assigned.UpdatedAtUtc.Should().Be(reassignedAt);
    }

    [Fact]
    public void Reassigned_PureUnassign_ShouldClearTeamId()
    {
        // Pure unassign: from_team_id = "T1", to_team_id absent.
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var assigned = _agent.Apply(created, new StudioMemberReassignedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            ToTeamId = "team-1",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        var unassigned = _agent.Apply(assigned, new StudioMemberReassignedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            FromTeamId = "team-1",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        unassigned.HasTeamId.Should().BeFalse();
        unassigned.TeamId.Should().BeEmpty();
    }

    [Fact]
    public void Reassigned_Move_ShouldSetTeamIdToDestination()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var inTeam1 = _agent.Apply(created, new StudioMemberReassignedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            ToTeamId = "team-1",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        var moved = _agent.Apply(inTeam1, new StudioMemberReassignedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            FromTeamId = "team-1",
            ToTeamId = "team-2",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        moved.TeamId.Should().Be("team-2");
    }

    [Fact]
    public void Reassigned_ShouldNotTouchPublishedServiceId()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var assigned = _agent.Apply(created, new StudioMemberReassignedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            ToTeamId = "team-1",
            ReassignedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        // Composing on top of ADR-0016: team membership must never disturb
        // the rename-safe published_service_id contract.
        assigned.PublishedServiceId.Should().Be(created.PublishedServiceId);
    }

    [Fact]
    public async Task HandleTeamAssignmentPatchRequested_ShouldCommitMoveFromAuthoritativeState()
    {
        var now = DateTimeOffset.UtcNow;
        var current = _agent.Apply(
            NewCreatedScriptMember(now),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-1",
                ScopeId = "scope-1",
                ToTeamId = "team-old",
                ReassignedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
            });
        var eventSourcing = new RecordingEventSourcing(current);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(current, eventSourcing, publisher);

        await agent.HandleTeamAssignmentPatchRequested(new StudioMemberTeamAssignmentPatchRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            TargetTeamId = "team-new",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().ContainSingle();
        var reassigned = eventSourcing.RaisedEvents.Single()
            .Should().BeOfType<StudioMemberReassignedEvent>().Subject;
        reassigned.FromTeamId.Should().Be("team-old");
        reassigned.ToTeamId.Should().Be("team-new");

        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTeamAssignmentPatchRequested_ShouldCommitPureAssignFromAuthoritativeState()
    {
        var now = DateTimeOffset.UtcNow;
        var current = NewCreatedScriptMember(now);
        var eventSourcing = new RecordingEventSourcing(current);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(current, eventSourcing, publisher);

        await agent.HandleTeamAssignmentPatchRequested(new StudioMemberTeamAssignmentPatchRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            TargetTeamId = "team-X",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
        });

        var reassigned = eventSourcing.RaisedEvents.Should().ContainSingle().Subject
            .Should().BeOfType<StudioMemberReassignedEvent>().Subject;
        reassigned.HasFromTeamId.Should().BeFalse();
        reassigned.ToTeamId.Should().Be("team-X");
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTeamAssignmentPatchRequested_ShouldNoOp_WhenTargetMatchesAuthoritativeState()
    {
        var now = DateTimeOffset.UtcNow;
        var current = _agent.Apply(
            NewCreatedScriptMember(now),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-1",
                ScopeId = "scope-1",
                ToTeamId = "team-1",
                ReassignedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
            });
        var eventSourcing = new RecordingEventSourcing(current);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(current, eventSourcing, publisher);

        await agent.HandleTeamAssignmentPatchRequested(new StudioMemberTeamAssignmentPatchRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            TargetTeamId = "team-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().BeEmpty();
        eventSourcing.ConfirmCallCount.Should().Be(0);
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleTeamAssignmentPatchRequested_ShouldCommitUnassignFromAuthoritativeState()
    {
        var now = DateTimeOffset.UtcNow;
        var current = _agent.Apply(
            NewCreatedScriptMember(now),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-1",
                ScopeId = "scope-1",
                ToTeamId = "team-1",
                ReassignedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
            });
        var eventSourcing = new RecordingEventSourcing(current);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(current, eventSourcing, publisher);

        await agent.HandleTeamAssignmentPatchRequested(new StudioMemberTeamAssignmentPatchRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
        });

        var reassigned = eventSourcing.RaisedEvents.Should().ContainSingle().Subject
            .Should().BeOfType<StudioMemberReassignedEvent>().Subject;
        reassigned.FromTeamId.Should().Be("team-1");
        reassigned.HasToTeamId.Should().BeFalse();
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteRequested_ShouldCommitOnlyDeletedEvent_WhenAssigned()
    {
        var now = DateTimeOffset.UtcNow;
        var current = _agent.Apply(
            NewCreatedScriptMember(now),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-1",
                ScopeId = "scope-1",
                ToTeamId = "team-1",
                ReassignedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
            });
        var eventSourcing = new RecordingEventSourcing(current);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(current, eventSourcing, publisher);
        var deletedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(2));

        await agent.HandleDeleteRequested(new StudioMemberDeleteRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            RequestedAtUtc = deletedAt,
        });

        var deleted = eventSourcing.RaisedEvents.Should().ContainSingle().Subject
            .Should().BeOfType<StudioMemberDeletedEvent>().Subject;
        deleted.MemberId.Should().Be("m-1");
        deleted.ScopeId.Should().Be("scope-1");
        deleted.PreviousTeamId.Should().Be("team-1");
        deleted.PublishedServiceId.Should().Be("member-m-1");
        deleted.DeletedAtUtc.Should().Be(deletedAt);
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleDeleteRequested_ShouldFailActiveBindingRunBeforeDeletedEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = StartScriptBindingRun(NewCreatedScriptMember(now), "bind-delete", now.AddSeconds(1));
        var eventSourcing = new RecordingEventSourcing(pending);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(pending, eventSourcing, publisher);
        var deletedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(4));

        await agent.HandleDeleteRequested(new StudioMemberDeleteRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            RequestedAtUtc = deletedAt,
        });

        eventSourcing.RaisedEvents.Should().HaveCount(2);
        var failed = eventSourcing.RaisedEvents[0]
            .Should().BeOfType<StudioMemberBindingFailedEvent>().Subject;
        failed.BindingRunId.Should().Be("bind-delete");
        failed.Failure.Code.Should().Be("STUDIO_MEMBER_DELETED");
        failed.Failure.FailedAtUtc.Should().Be(deletedAt);

        var deleted = eventSourcing.RaisedEvents[1]
            .Should().BeOfType<StudioMemberDeletedEvent>().Subject;
        deleted.MemberId.Should().Be("m-1");
        deleted.ScopeId.Should().Be("scope-1");
        deleted.DeletedAtUtc.Should().Be(deletedAt);

        var sent = publisher.SentMessages.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be(StudioMemberConventions.BuildBindingRunActorId("bind-delete"));
        var ack = sent.Event.Should().BeOfType<StudioMemberBindingTerminalAcknowledged>().Subject;
        ack.BindingRunId.Should().Be("bind-delete");
        ack.Status.Should().Be(StudioMemberBindingRunStatus.Failed);
    }

    [Fact]
    public void DeletedEvent_ShouldTombstoneStateAndClearTeamId()
    {
        var now = DateTimeOffset.UtcNow;
        var assigned = _agent.Apply(
            NewCreatedScriptMember(now),
            new StudioMemberReassignedEvent
            {
                MemberId = "m-1",
                ScopeId = "scope-1",
                ToTeamId = "team-1",
                ReassignedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
            });
        var deletedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(2));

        var deleted = _agent.Apply(assigned, new StudioMemberDeletedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            PreviousTeamId = "team-1",
            PublishedServiceId = "member-m-1",
            DeletedAtUtc = deletedAt,
        });

        deleted.Deleted.Should().BeTrue();
        deleted.DeletedAtUtc.Should().Be(deletedAt);
        deleted.HasTeamId.Should().BeFalse();
        deleted.PublishedServiceId.Should().Be("member-m-1");
        deleted.UpdatedAtUtc.Should().Be(deletedAt);
    }

    [Fact]
    public async Task HandleDeleteRequested_ShouldNoOp_WhenAlreadyDeleted()
    {
        var now = DateTimeOffset.UtcNow;
        var deleted = _agent.Apply(NewCreatedScriptMember(now), new StudioMemberDeletedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            PublishedServiceId = "member-m-1",
            DeletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(1)),
        });
        var eventSourcing = new RecordingEventSourcing(deleted);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(deleted, eventSourcing, publisher);

        await agent.HandleDeleteRequested(new StudioMemberDeleteRequested
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
        });

        eventSourcing.RaisedEvents.Should().BeEmpty();
        eventSourcing.ConfirmCallCount.Should().Be(0);
        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public void Bound_ShouldCaptureLastBindingAndAdvanceLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var withImpl = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });

        var pending = StartWorkflowBindingRun(withImpl, "bind-legacy-test", now.AddSeconds(1));
        var bound = _agent.Apply(pending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-legacy-test",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-7",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        bound.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
        bound.LastBinding.Should().NotBeNull();
        bound.LastBinding.PublishedServiceId.Should().Be("member-m-1");
        bound.LastBinding.RevisionId.Should().Be("rev-7");
        bound.PublishedServiceId.Should().Be("member-m-1");
    }

    [Fact]
    public void PublishedBindingRecorded_ShouldRefreshLastBindingAndClearStaleCurrentRun()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var pending = StartWorkflowBindingRun(created, "bind-first", now.AddSeconds(1));
        var completed = _agent.Apply(pending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-first",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-first",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        var recorded = _agent.Apply(completed, new StudioMemberPublishedBindingRecordedEvent
        {
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-updated",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "workflow-1",
                    WorkflowRevision = "rev-updated",
                },
            },
            RecordedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(10)),
            ExpectedActorId = "workflow-definition:workflow-1",
        });

        recorded.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
        recorded.LastBinding.Should().NotBeNull();
        recorded.LastBinding.PublishedServiceId.Should().Be("member-m-1");
        recorded.LastBinding.RevisionId.Should().Be("rev-updated");
        recorded.LastBinding.ExpectedActorId.Should().Be("workflow-definition:workflow-1");
        recorded.ImplementationRef.Workflow.WorkflowId.Should().Be("workflow-1");
        recorded.ImplementationRef.Workflow.WorkflowRevision.Should().Be("rev-updated");
        recorded.Binding.CurrentBindingRunId.Should().BeEmpty();
        recorded.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Unspecified);
        recorded.Binding.LastTerminalBindingRunId.Should().Be("bind-first");
        recorded.Binding.LastFailure.Should().BeNull();
    }

    [Fact]
    public async Task HandlePublishedBindingRecorded_ShouldRejectMismatchedPublishedServiceId()
    {
        var current = NewCreatedWorkflowMember(DateTimeOffset.UtcNow);
        var eventSourcing = new RecordingEventSourcing(current);
        var agent = NewHandlerAgent(current, eventSourcing, new RecordingEventPublisher());

        var act = () => agent.HandlePublishedBindingRecorded(new StudioMemberPublishedBindingRecordedEvent
        {
            PublishedServiceId = "different-service",
            RevisionId = "rev-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "workflow-1",
                    WorkflowRevision = "rev-1",
                },
            },
            RecordedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match member 'm-1' publishedServiceId 'member-m-1'*");
        eventSourcing.RaisedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlePublishedBindingRecorded_ShouldRejectMismatchedImplementationKind()
    {
        var current = NewCreatedWorkflowMember(DateTimeOffset.UtcNow);
        var eventSourcing = new RecordingEventSourcing(current);
        var agent = NewHandlerAgent(current, eventSourcing, new RecordingEventPublisher());

        var act = () => agent.HandlePublishedBindingRecorded(new StudioMemberPublishedBindingRecordedEvent
        {
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-1",
                },
            },
            RecordedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match member kind 'Workflow'*");
        eventSourcing.RaisedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandlePublishedBindingRecorded_ShouldRejectImplementationRefForDifferentKind()
    {
        var current = NewCreatedWorkflowMember(DateTimeOffset.UtcNow);
        var eventSourcing = new RecordingEventSourcing(current);
        var agent = NewHandlerAgent(current, eventSourcing, new RecordingEventPublisher());

        var act = () => agent.HandlePublishedBindingRecorded(new StudioMemberPublishedBindingRecordedEvent
        {
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Script = new StudioMemberScriptRef
                {
                    ScriptId = "script-1",
                    ScriptRevision = "rev-1",
                },
            },
            RecordedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("published binding record must include a resolved implementation reference for its implementation kind.");
        eventSourcing.RaisedEvents.Should().BeEmpty();
    }

    [Fact]
    public void BindingAdmissionRequested_ShouldRecordPendingRun()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));

        var pending = _agent.Apply(created, new StudioMemberBindAdmissionRequested
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = "hash-1",
            RequestedAtUtc = requestedAt,
            Request = new StudioMemberBindingRequest
            {
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                RequestHash = "hash-1",
                Script = new StudioMemberScriptBindingRequest
                {
                    ScriptId = "script-1",
                },
            },
        });

        pending.Binding.CurrentBindingRunId.Should().Be("bind-1");
        pending.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.AdmissionPending);
        pending.Binding.UpdatedAtUtc.Should().Be(requestedAt);
    }

    [Fact]
    public void BindingAdmitted_ShouldRecordAdmittedRun()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var pending = _agent.Apply(created, NewAdmissionRequested());
        var admittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var admitted = _agent.Apply(pending, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Original",
            AdmittedAtUtc = admittedAt,
        });

        admitted.Binding.CurrentBindingRunId.Should().Be("bind-1");
        admitted.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Admitted);
        admitted.Binding.UpdatedAtUtc.Should().Be(admittedAt);
    }

    [Fact]
    public void BindingRejected_ShouldRecordTerminalFailure()
    {
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var pending = _agent.Apply(created, NewAdmissionRequested());
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));

        var rejected = _agent.Apply(pending, new StudioMemberBindingRejectedEvent
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "STUDIO_MEMBER_IMPLEMENTATION_KIND_MISMATCH",
                Message = "kind mismatch",
                FailedAtUtc = failedAt,
            },
        });

        rejected.Binding.CurrentBindingRunId.Should().Be("bind-1");
        rejected.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Rejected);
        rejected.Binding.LastTerminalBindingRunId.Should().Be("bind-1");
        rejected.Binding.LastFailure.Code.Should().Be("STUDIO_MEMBER_IMPLEMENTATION_KIND_MISMATCH");
        rejected.Binding.UpdatedAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public async Task HandleBindingAdmissionRequested_ShouldResendRejectedEvent_WhenSameRunTerminalReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var pending = _agent.Apply(created, NewAdmissionRequested(
            bindingRunId: "bind-rejected",
            requestHash: "hash-bind-rejected",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(1))));
        var rejected = _agent.Apply(pending, new StudioMemberBindingRejectedEvent
        {
            BindingRunId = "bind-rejected",
            ScopeId = "scope-1",
            MemberId = "m-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "STUDIO_MEMBER_IMPLEMENTATION_KIND_MISMATCH",
                Message = "kind mismatch",
                FailedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
            },
        });
        var eventSourcing = new RecordingEventSourcing(rejected);
        var publisher = new RecordingEventPublisher();
        var agent = new StudioMemberGAgent
        {
            EventSourcing = eventSourcing,
            EventPublisher = publisher,
        };
        StudioMemberStateSetter.Set(agent, rejected);

        await agent.HandleBindingAdmissionRequested(NewAdmissionRequested(
            bindingRunId: "bind-rejected",
            requestHash: "hash-bind-rejected",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(3))));

        eventSourcing.RaisedEvents.Should().BeEmpty();
        eventSourcing.ConfirmCallCount.Should().Be(0);
        publisher.SentMessages.Should().ContainSingle();
        var sent = publisher.SentMessages.Single();
        sent.TargetActorId.Should().Be(StudioMemberConventions.BuildBindingRunActorId("bind-rejected"));
        var response = sent.Event.Should().BeOfType<StudioMemberBindingRejectedEvent>().Subject;
        response.BindingRunId.Should().Be("bind-rejected");
        response.ScopeId.Should().Be("scope-1");
        response.MemberId.Should().Be("m-1");
        response.Failure.Code.Should().Be("STUDIO_MEMBER_IMPLEMENTATION_KIND_MISMATCH");
        response.Failure.Message.Should().Be("kind mismatch");
    }

    [Fact]
    public async Task HandleBindingPlatformPending_ShouldNotSendTerminalAcknowledgement()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = StartScriptBindingRun(NewCreatedScriptMember(now), "bind-1", now.AddSeconds(1));
        var eventSourcing = new RecordingEventSourcing(pending);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(pending, eventSourcing, publisher);

        await agent.HandleBindingPlatformPending(new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-bind-1",
            PendingAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        publisher.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleBindingCompleted_ShouldSendSucceededTerminalAcknowledgement()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = StartScriptBindingRun(NewCreatedScriptMember(now), "bind-1", now.AddSeconds(1));
        var eventSourcing = new RecordingEventSourcing(pending);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(pending, eventSourcing, publisher);

        await agent.HandleBindingCompleted(new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-1",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        var ack = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberBindingTerminalAcknowledged>().Subject;
        ack.BindingRunId.Should().Be("bind-1");
        ack.Status.Should().Be(StudioMemberBindingRunStatus.Succeeded);
    }

    [Fact]
    public async Task HandleBindingFailed_ShouldSendFailedTerminalAcknowledgement()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = StartScriptBindingRun(NewCreatedScriptMember(now), "bind-1", now.AddSeconds(1));
        var eventSourcing = new RecordingEventSourcing(pending);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(pending, eventSourcing, publisher);

        await agent.HandleBindingFailed(new StudioMemberBindingFailedEvent
        {
            BindingRunId = "bind-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "SCOPE_BINDING_FAILED",
                Message = "platform failed",
                FailedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
            },
        });

        var ack = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberBindingTerminalAcknowledged>().Subject;
        ack.BindingRunId.Should().Be("bind-1");
        ack.Status.Should().Be(StudioMemberBindingRunStatus.Failed);
    }

    [Fact]
    public async Task HandleBindingCompleted_ShouldResendSucceededAcknowledgement_WhenTerminalReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = StartScriptBindingRun(NewCreatedScriptMember(now), "bind-1", now.AddSeconds(1));
        var completed = _agent.Apply(pending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-1",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });
        var eventSourcing = new RecordingEventSourcing(completed);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(completed, eventSourcing, publisher);

        await agent.HandleBindingCompleted(new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-1",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(5)),
        });

        eventSourcing.RaisedEvents.Should().BeEmpty();
        var ack = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberBindingTerminalAcknowledged>().Subject;
        ack.Status.Should().Be(StudioMemberBindingRunStatus.Succeeded);
    }

    [Fact]
    public async Task HandleBindingFailed_ShouldResendFailedAcknowledgement_WhenTerminalReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var pending = StartScriptBindingRun(NewCreatedScriptMember(now), "bind-1", now.AddSeconds(1));
        var failed = _agent.Apply(pending, new StudioMemberBindingFailedEvent
        {
            BindingRunId = "bind-1",
            Failure = new StudioMemberBindingFailure
            {
                Code = "SCOPE_BINDING_FAILED",
                Message = "platform failed",
                FailedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
            },
        });
        var eventSourcing = new RecordingEventSourcing(failed);
        var publisher = new RecordingEventPublisher();
        var agent = NewHandlerAgent(failed, eventSourcing, publisher);

        await agent.HandleBindingFailed(new StudioMemberBindingFailedEvent
        {
            BindingRunId = "bind-1",
            Failure = failed.Binding.LastFailure.Clone(),
        });

        eventSourcing.RaisedEvents.Should().BeEmpty();
        var ack = publisher.SentMessages.Should().ContainSingle().Subject.Event
            .Should().BeOfType<StudioMemberBindingTerminalAcknowledged>().Subject;
        ack.Status.Should().Be(StudioMemberBindingRunStatus.Failed);
    }

    [Fact]
    public void BindingCompleted_ShouldCaptureLastBindingAndAuthorityState()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var pending = StartWorkflowBindingRun(created, "bind-1", now.AddSeconds(1));

        var completedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(4));
        var completed = _agent.Apply(pending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-1",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-8",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            ImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "rev-8",
                },
            },
            ExpectedActorId = "scope-workflow:scope-1:m-1",
            CompletedAtUtc = completedAt,
        });

        completed.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
        completed.LastBinding.Should().NotBeNull();
        completed.LastBinding.RevisionId.Should().Be("rev-8");
        completed.Binding.CurrentBindingRunId.Should().Be("bind-1");
        completed.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Succeeded);
        completed.Binding.LastTerminalBindingRunId.Should().Be("bind-1");
        completed.Binding.LastFailure.Should().BeNull();
        completed.LastBinding.Should().NotBeNull();
        completed.LastBinding.ExpectedActorId.Should().Be("scope-workflow:scope-1:m-1");
        completed.Binding.UpdatedAtUtc.Should().Be(completedAt);
    }

    [Fact]
    public void BindingCompletedEvent_ShouldCarryMemberContractFieldsWithoutPlatformResultBag()
    {
        StudioMemberBindingCompletedEvent.Descriptor.Fields.InFieldNumberOrder()
            .Select(field => field.Name)
            .Should()
            .Contain("expected_actor_id")
            .And.NotContain("result");
    }

    [Fact]
    public void BindingFailed_ShouldKeepLastBindingAndRecordFailure()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var successPending = StartWorkflowBindingRun(created, "bind-success", now.AddSeconds(1));
        var completed = _agent.Apply(successPending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-success",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-good",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });
        var nextAdmission = _agent.Apply(completed, new StudioMemberBindAdmissionRequested
        {
            BindingRunId = "bind-fail",
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = "hash-fail",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(5)),
            Request = new StudioMemberBindingRequest
            {
                BindingRunId = "bind-fail",
                ScopeId = "scope-1",
                MemberId = "m-1",
                RequestHash = "hash-fail",
                Workflow = new StudioMemberWorkflowBindingRequest(),
            },
        });
        var pending = _agent.Apply(nextAdmission, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-fail",
            PlatformBindingCommandId = "platform-2",
            PendingAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(6)),
        });
        var failedAt = Timestamp.FromDateTimeOffset(now.AddSeconds(7));

        var failed = _agent.Apply(pending, new StudioMemberBindingFailedEvent
        {
            BindingRunId = "bind-fail",
            Failure = new StudioMemberBindingFailure
            {
                Code = "SCOPE_BINDING_FAILED",
                Message = "platform failed",
                FailedAtUtc = failedAt,
            },
        });

        failed.LastBinding.RevisionId.Should().Be("rev-good");
        failed.Binding.CurrentBindingRunId.Should().Be("bind-fail");
        failed.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Failed);
        failed.Binding.LastTerminalBindingRunId.Should().Be("bind-fail");
        failed.Binding.LastFailure.Code.Should().Be("SCOPE_BINDING_FAILED");
        failed.Binding.UpdatedAtUtc.Should().Be(failedAt);
    }

    [Fact]
    public void BindingCompleted_ShouldIgnoreStaleRun()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var current = StartWorkflowBindingRun(created, "bind-current", now.AddSeconds(1));

        var stale = _agent.Apply(current, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-old",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-old",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        stale.LastBinding.Should().BeNull();
        stale.Binding.CurrentBindingRunId.Should().Be("bind-current");
        stale.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
    }

    [Fact]
    public void BindingAdmissionRequested_ShouldIgnoreOlderRun()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var current = _agent.Apply(created, NewAdmissionRequested(
            bindingRunId: "bind-current",
            requestHash: "hash-current",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(10))));

        var stale = _agent.Apply(current, NewAdmissionRequested(
            bindingRunId: "bind-old",
            requestHash: "hash-old",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(5))));

        stale.Binding.CurrentBindingRunId.Should().Be("bind-current");
        stale.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.AdmissionPending);
        stale.Binding.UpdatedAtUtc.Should().Be(Timestamp.FromDateTimeOffset(now.AddSeconds(10)));
    }

    [Fact]
    public void BindingPlatformPending_ShouldIgnoreStaleRun()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var current = _agent.Apply(created, NewAdmissionRequested(
            bindingRunId: "bind-current",
            requestHash: "hash-current",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(1))));

        var stale = _agent.Apply(current, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-old",
            PlatformBindingCommandId = "platform-old",
            PendingAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
        });

        stale.Binding.CurrentBindingRunId.Should().Be("bind-current");
        stale.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.AdmissionPending);
    }

    [Fact]
    public void BindingAdmissionRequested_ShouldNotRegressSameRunAfterPlatformPending()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var admitted = _agent.Apply(
            _agent.Apply(created, NewAdmissionRequested(requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(1)))),
            new StudioMemberBindingAdmittedEvent
            {
                BindingRunId = "bind-1",
                ScopeId = "scope-1",
                MemberId = "m-1",
                PublishedServiceId = "member-m-1",
                ImplementationKind = StudioMemberImplementationKind.Script,
                DisplayName = "Original",
                AdmittedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(2)),
            });
        var pending = _agent.Apply(admitted, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            PendingAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(3)),
        });

        var duplicateAdmission = _agent.Apply(pending, NewAdmissionRequested(
            bindingRunId: "bind-1",
            requestHash: "hash-1",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(4))));

        duplicateAdmission.Binding.CurrentBindingRunId.Should().Be("bind-1");
        duplicateAdmission.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        duplicateAdmission.Binding.UpdatedAtUtc.Should().Be(Timestamp.FromDateTimeOffset(now.AddSeconds(3)));
    }

    [Fact]
    public void BindingAdmissionRequested_ShouldKeepActiveRunWhenNewerRunArrives()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var active = StartScriptBindingRun(created, "bind-active", now.AddSeconds(1));

        var afterNewerAdmission = _agent.Apply(active, NewAdmissionRequested(
            bindingRunId: "bind-newer",
            requestHash: "hash-newer",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(10))));

        afterNewerAdmission.Binding.CurrentBindingRunId.Should().Be("bind-active");
        afterNewerAdmission.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
        afterNewerAdmission.Binding.UpdatedAtUtc.Should().Be(active.Binding.UpdatedAtUtc);
    }

    [Fact]
    public void BindingAdmissionRequested_ShouldStartNewRunAfterTerminalWhenNewer()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var successPending = StartScriptBindingRun(created, "bind-success", now.AddSeconds(1));
        var completed = _agent.Apply(successPending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-success",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-good",
            ImplementationKind = StudioMemberImplementationKind.Script,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        var next = _agent.Apply(completed, NewAdmissionRequested(
            bindingRunId: "bind-next",
            requestHash: "hash-next",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(5))));

        next.Binding.CurrentBindingRunId.Should().Be("bind-next");
        next.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.AdmissionPending);
        next.Binding.LastTerminalBindingRunId.Should().Be("bind-success");
        next.LastBinding.RevisionId.Should().Be("rev-good");
    }

    [Fact]
    public void BindingAdmissionRequested_ShouldIgnoreSameRunAfterTerminal()
    {
        var now = DateTimeOffset.UtcNow;
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        var successPending = StartScriptBindingRun(created, "bind-success", now.AddSeconds(1));
        var completed = _agent.Apply(successPending, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-success",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-good",
            ImplementationKind = StudioMemberImplementationKind.Script,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(now.AddSeconds(4)),
        });

        var duplicate = _agent.Apply(completed, NewAdmissionRequested(
            bindingRunId: "bind-success",
            requestHash: "hash-bind-success",
            requestedAt: Timestamp.FromDateTimeOffset(now.AddSeconds(5))));

        duplicate.Binding.CurrentBindingRunId.Should().Be("bind-success");
        duplicate.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Succeeded);
        duplicate.Binding.LastTerminalBindingRunId.Should().Be("bind-success");
        duplicate.LastBinding.RevisionId.Should().Be("rev-good");
    }

    private static StudioMemberBindAdmissionRequested NewAdmissionRequested(
        string bindingRunId = "bind-1",
        string requestHash = "hash-1",
        Timestamp? requestedAt = null) =>
        new()
        {
            BindingRunId = bindingRunId,
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = requestHash,
            RequestedAtUtc = requestedAt ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
            Request = new StudioMemberBindingRequest
            {
                BindingRunId = bindingRunId,
                ScopeId = "scope-1",
                MemberId = "m-1",
                RequestHash = requestHash,
                Script = new StudioMemberScriptBindingRequest
                {
                    ScriptId = "script-1",
                },
            },
        };

    private StudioMemberState NewCreatedScriptMember(DateTimeOffset createdAt) =>
        _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Script,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(createdAt),
        });

    private StudioMemberState NewCreatedWorkflowMember(DateTimeOffset createdAt) =>
        _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(createdAt),
        });

    private static StudioMemberGAgent NewHandlerAgent(
        StudioMemberState state,
        RecordingEventSourcing eventSourcing,
        RecordingEventPublisher publisher)
    {
        var agent = new StudioMemberGAgent
        {
            EventSourcing = eventSourcing,
            EventPublisher = publisher,
        };
        StudioMemberStateSetter.Set(agent, state);
        return agent;
    }

    private StudioMemberState StartWorkflowBindingRun(
        StudioMemberState state,
        string bindingRunId,
        DateTimeOffset requestedAt)
    {
        var requested = _agent.Apply(state, new StudioMemberBindAdmissionRequested
        {
            BindingRunId = bindingRunId,
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = $"hash-{bindingRunId}",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(requestedAt),
            Request = new StudioMemberBindingRequest
            {
                BindingRunId = bindingRunId,
                ScopeId = "scope-1",
                MemberId = "m-1",
                RequestHash = $"hash-{bindingRunId}",
                Workflow = new StudioMemberWorkflowBindingRequest(),
            },
        });
        var admitted = _agent.Apply(requested, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = bindingRunId,
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Original",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddSeconds(1)),
        });
        return _agent.Apply(admitted, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = bindingRunId,
            PlatformBindingCommandId = $"platform-{bindingRunId}",
            PendingAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddSeconds(2)),
        });
    }

    private StudioMemberState StartScriptBindingRun(
        StudioMemberState state,
        string bindingRunId,
        DateTimeOffset requestedAt)
    {
        var requested = _agent.Apply(state, NewAdmissionRequested(
            bindingRunId: bindingRunId,
            requestHash: $"hash-{bindingRunId}",
            requestedAt: Timestamp.FromDateTimeOffset(requestedAt)));
        var admitted = _agent.Apply(requested, new StudioMemberBindingAdmittedEvent
        {
            BindingRunId = bindingRunId,
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Original",
            AdmittedAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddSeconds(1)),
        });
        return _agent.Apply(admitted, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = bindingRunId,
            PlatformBindingCommandId = $"platform-{bindingRunId}",
            PendingAtUtc = Timestamp.FromDateTimeOffset(requestedAt.AddSeconds(2)),
        });
    }

    private sealed class StudioMemberStateApplier
    {
        private static readonly MethodInfo TransitionStateMethod =
            typeof(StudioMemberGAgent).GetMethod(
                "TransitionState",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransitionState method not found.");

        private readonly StudioMemberGAgent _agent = new();

        public StudioMemberState Apply(StudioMemberState current, IMessage evt)
        {
            var result = TransitionStateMethod.Invoke(_agent, [current, evt])
                ?? throw new InvalidOperationException("TransitionState returned null.");
            return (StudioMemberState)result;
        }
    }

    private static class StudioMemberStateSetter
    {
        private static readonly FieldInfo StateField =
            typeof(StudioMemberGAgent).BaseType!
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GAgent state field not found.");

        public static void Set(StudioMemberGAgent agent, StudioMemberState state) =>
            StateField.SetValue(agent, state.Clone());
    }

    private sealed class RecordingEventSourcing(StudioMemberState replayState) : IEventSourcingBehavior<StudioMemberState>
    {
        private readonly List<IMessage> _pending = [];
        public List<IMessage> RaisedEvents { get; } = [];
        public int ConfirmCallCount { get; private set; }
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage
        {
            RaisedEvents.Add(evt);
            _pending.Add(evt);
        }

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            ConfirmCallCount++;
            var result = EventSourcingTestCommit.From(_pending, CurrentVersion);
            CurrentVersion = result.LatestVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(StudioMemberState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<StudioMemberState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<StudioMemberState?>(replayState.Clone());

        public void DiscardPendingEvents()
        {
            RaisedEvents.Clear();
            _pending.Clear();
        }

        public StudioMemberState TransitionState(StudioMemberState current, IMessage evt) =>
            current.Clone();
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<SentMessage> SentMessages { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            SentMessages.Add(new SentMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    private sealed record SentMessage(string TargetActorId, IMessage Event);
}
