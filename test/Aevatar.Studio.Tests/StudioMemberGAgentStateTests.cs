using System.Reflection;
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
    public void Bound_ShouldCaptureLastBindingAndAdvanceLifecycle()
    {
        var withImpl = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var bound = _agent.Apply(withImpl, new StudioMemberBoundEvent
        {
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-7",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            BoundAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        bound.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
        bound.LastBinding.Should().NotBeNull();
        bound.LastBinding.PublishedServiceId.Should().Be("member-m-1");
        bound.LastBinding.RevisionId.Should().Be("rev-7");
        bound.PublishedServiceId.Should().Be("member-m-1");
    }

    [Fact]
    public void BindingRequested_ShouldRecordPendingRun()
    {
        var created = NewCreatedState();
        var requestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        var pending = _agent.Apply(created, new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Original",
            Request = new StudioMemberBindingSpec
            {
                Workflow = new StudioMemberWorkflowBindingSpec
                {
                    WorkflowYamls = { "workflow: test" },
                },
            },
            RequestedAtUtc = requestedAt,
        });

        pending.BindingRuns.Should().ContainSingle();
        var run = pending.BindingRuns[0];
        run.BindingId.Should().Be("bind-1");
        run.Status.Should().Be(StudioMemberBindingStatus.Pending);
        run.PublishedServiceId.Should().Be("member-m-1");
        run.Request.Workflow.WorkflowYamls.Should().ContainSingle();
    }

    [Fact]
    public void BindingCompleted_ShouldUpdateRunAndLastBinding()
    {
        var pending = _agent.Apply(NewCreatedState(), new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Original",
            Request = new StudioMemberBindingSpec
            {
                Workflow = new StudioMemberWorkflowBindingSpec
                {
                    WorkflowYamls = { "workflow: test" },
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var completed = _agent.Apply(pending, new StudioMemberBindingCompletedEvent
        {
            BindingId = "bind-1",
            RevisionId = "rev-1",
            ExpectedActorId = "actor-1",
            ResolvedImplementationRef = new StudioMemberImplementationRef
            {
                Workflow = new StudioMemberWorkflowRef
                {
                    WorkflowId = "wf-1",
                    WorkflowRevision = "rev-1",
                },
            },
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        completed.BindingRuns[0].Status.Should().Be(StudioMemberBindingStatus.Completed);
        completed.LastBinding.Should().NotBeNull();
        completed.LastBinding.RevisionId.Should().Be("rev-1");
        completed.ImplementationRef.Workflow.WorkflowId.Should().Be("wf-1");
        completed.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
    }

    [Fact]
    public void BindingFailed_ShouldUpdateRunWithoutChangingLastBinding()
    {
        var pending = _agent.Apply(NewCreatedState(), new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Original",
            Request = new StudioMemberBindingSpec
            {
                Workflow = new StudioMemberWorkflowBindingSpec
                {
                    WorkflowYamls = { "workflow: test" },
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var failed = _agent.Apply(pending, new StudioMemberBindingFailedEvent
        {
            BindingId = "bind-1",
            FailureCode = "scope_binding_failed",
            FailureSummary = "scope binding failed",
            Retryable = true,
            FailedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        failed.BindingRuns[0].Status.Should().Be(StudioMemberBindingStatus.Failed);
        failed.BindingRuns[0].FailureCode.Should().Be("scope_binding_failed");
        failed.BindingRuns[0].Retryable.Should().BeTrue();
        failed.LastBinding.Should().BeNull();
    }

    [Fact]
    public void BindingRequested_ShouldReplacePreviousTerminalRun()
    {
        var pending = _agent.Apply(NewCreatedState(), new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Original",
            Request = new StudioMemberBindingSpec
            {
                Workflow = new StudioMemberWorkflowBindingSpec
                {
                    WorkflowYamls = { "workflow: first" },
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var completed = _agent.Apply(pending, new StudioMemberBindingCompletedEvent
        {
            BindingId = "bind-1",
            RevisionId = "rev-1",
            ExpectedActorId = "actor-1",
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        var nextPending = _agent.Apply(completed, new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-2",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Original",
            Request = new StudioMemberBindingSpec
            {
                Workflow = new StudioMemberWorkflowBindingSpec
                {
                    WorkflowYamls = { "workflow: second" },
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        nextPending.BindingRuns.Should().ContainSingle();
        nextPending.BindingRuns[0].BindingId.Should().Be("bind-2");
        nextPending.BindingRuns[0].Request.Workflow.WorkflowYamls.Should().ContainSingle("workflow: second");
    }

    private StudioMemberState NewCreatedState() =>
        _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

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
}
