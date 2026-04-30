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

        var bound = _agent.Apply(withImpl, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-legacy-test",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-7",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        bound.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
        bound.LastBinding.Should().NotBeNull();
        bound.LastBinding.PublishedServiceId.Should().Be("member-m-1");
        bound.LastBinding.RevisionId.Should().Be("rev-7");
        bound.PublishedServiceId.Should().Be("member-m-1");
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
    public void BindingCompleted_ShouldCaptureLastBindingAndAuthorityState()
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
        var pendingAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1));
        var pending = _agent.Apply(created, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-1",
            PlatformBindingCommandId = "platform-1",
            PendingAtUtc = pendingAt,
        });

        var completedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2));
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
            CompletedAtUtc = completedAt,
        });

        completed.LifecycleStage.Should().Be(StudioMemberLifecycleStage.BindReady);
        completed.LastBinding.Should().NotBeNull();
        completed.LastBinding.RevisionId.Should().Be("rev-8");
        completed.Binding.CurrentBindingRunId.Should().Be("bind-1");
        completed.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.Succeeded);
        completed.Binding.LastTerminalBindingRunId.Should().Be("bind-1");
        completed.Binding.LastFailure.Should().BeNull();
        completed.Binding.UpdatedAtUtc.Should().Be(completedAt);
    }

    [Fact]
    public void BindingFailed_ShouldKeepLastBindingAndRecordFailure()
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
        var completed = _agent.Apply(created, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-success",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-good",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });
        var pending = _agent.Apply(completed, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-fail",
            PlatformBindingCommandId = "platform-2",
            PendingAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });
        var failedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(3));

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
        var created = _agent.Apply(new StudioMemberState(), new StudioMemberCreatedEvent
        {
            MemberId = "m-1",
            ScopeId = "scope-1",
            DisplayName = "Original",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            PublishedServiceId = "member-m-1",
            CreatedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var current = _agent.Apply(created, new StudioMemberBindingPlatformPendingEvent
        {
            BindingRunId = "bind-current",
            PlatformBindingCommandId = "platform-current",
            PendingAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
        });

        var stale = _agent.Apply(current, new StudioMemberBindingCompletedEvent
        {
            BindingRunId = "bind-old",
            PublishedServiceId = "member-m-1",
            RevisionId = "rev-old",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            CompletedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(2)),
        });

        stale.LastBinding.Should().BeNull();
        stale.Binding.CurrentBindingRunId.Should().Be("bind-current");
        stale.Binding.CurrentStatus.Should().Be(StudioMemberBindingRunStatus.PlatformBindingPending);
    }

    private static StudioMemberBindAdmissionRequested NewAdmissionRequested() =>
        new()
        {
            BindingRunId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            RequestHash = "hash-1",
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1)),
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
        };

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
