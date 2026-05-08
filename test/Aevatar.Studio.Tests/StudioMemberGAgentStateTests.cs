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
}
