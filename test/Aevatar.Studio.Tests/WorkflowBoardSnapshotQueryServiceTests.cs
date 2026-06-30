using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.WorkflowBoards;
using Aevatar.Studio.Application.Studio.WorkflowBoards.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class WorkflowBoardSnapshotQueryServiceTests
{
    private const string ScopeId = "scope-mainnet-01";

    [Fact]
    public void WorkflowBoardSnapshotRequestLimits_ShouldExposeApplicationContractLimits()
    {
        WorkflowBoardSnapshotRequestLimits.MaxSelectedTeams.Should().Be(4);
        WorkflowBoardSnapshotRequestLimits.MaxSelectedMembers.Should().Be(24);
        WorkflowBoardSnapshotRequestLimits.MaxPreviousWatermarkLength.Should().Be(256);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnSelectedMembersWithoutExpandingTeamRoster()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(new WorkflowBoardRosterTeam(
                TeamId: "t-protocol",
                ScopeId: ScopeId,
                DisplayName: "protocol-ops",
                IsArchived: false,
                TotalMemberCount: 8,
                UpdatedAt: DateTimeOffset.Parse("2026-06-24T10:00:00Z")))
            .AddMember(new WorkflowBoardRosterMember(
                MemberId: "m-alpha",
                ScopeId: ScopeId,
                TeamId: "t-protocol",
                DisplayName: "Alpha",
                IsArchived: false,
                PublishedServiceId: "svc-alpha",
                WorkflowId: "wf-alpha",
                WorkflowName: "Deploy workflow",
                ActorId: "actor-alpha",
                RoleSummary: "deploy coordinator",
                UpdatedAt: DateTimeOffset.Parse("2026-06-24T10:01:00Z")))
            .AddMember(new WorkflowBoardRosterMember(
                MemberId: "m-unselected",
                ScopeId: ScopeId,
                TeamId: "t-protocol",
                DisplayName: "Unselected",
                IsArchived: false,
                PublishedServiceId: "svc-unselected",
                WorkflowId: "wf-unselected",
                WorkflowName: "Unselected workflow",
                ActorId: "actor-unselected",
                RoleSummary: "not selected",
                UpdatedAt: DateTimeOffset.Parse("2026-06-24T10:02:00Z")));
        var service = new WorkflowBoardSnapshotQueryService(
            roster,
            executionQueryPort: null,
            new FixedWorkflowBoardClock(DateTimeOffset.Parse("2026-06-24T13:24:16Z")));

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-protocol", ["m-alpha"])]));

        snapshot.ScopeId.Should().Be(ScopeId);
        snapshot.GeneratedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:24:16Z"));
        snapshot.Teams.Should().ContainSingle();
        snapshot.Teams[0].TeamId.Should().Be("t-protocol");
        snapshot.Teams[0].SelectedMemberCount.Should().Be(1);
        snapshot.Teams[0].TotalMemberCount.Should().Be(8);
        snapshot.Teams[0].Members.Should().ContainSingle();
        var member = snapshot.Teams[0].Members[0];
        member.MemberId.Should().Be("m-alpha");
        member.WorkflowId.Should().Be("wf-alpha");
        member.PublishedServiceId.Should().Be("svc-alpha");
        member.ActorId.Should().Be("actor-alpha");
        member.ExecutionAvailability.Should().Be(WorkflowBoardExecutionAvailability.PendingBackendContract);
        member.CompletedNodes.Should().BeEmpty();
        member.PendingNodes.Should().BeEmpty();
        member.FailedNodes.Should().BeEmpty();
        snapshot.Totals.CompletedSteps.Should().BeNull();
        snapshot.Totals.RunningNodes.Should().BeNull();
        snapshot.Totals.WaitingOrPendingNodes.Should().BeNull();
        snapshot.Totals.FailedNodes.Should().BeNull();
        snapshot.InvalidSelections.Should().BeEmpty();
        snapshot.Watermark.Should().StartWith("workflow-board:v1:");
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnInvalidSelectionsWithoutDroppingValidRows()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha", name: "Alpha Team"))
            .AddTeam(NewTeam("t-archived", archived: true))
            .AddMember(NewMember("m-alpha", "t-alpha"))
            .AddMember(NewMember("m-other-team", "t-other"))
            .AddMember(NewMember("m-archived", "t-alpha", archived: true));
        var service = NewService(roster);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [
                new WorkflowBoardTeamSelection(
                    "t-alpha",
                    ["m-alpha", "m-missing", "m-other-team", "m-archived"]),
                new WorkflowBoardTeamSelection("t-missing", ["m-any"]),
                new WorkflowBoardTeamSelection("t-archived", ["m-any"]),
            ]));

        snapshot.Teams.Should().ContainSingle();
        snapshot.Teams[0].Members.Should().ContainSingle()
            .Which.MemberId.Should().Be("m-alpha");
        snapshot.InvalidSelections.Select(x => (x.TeamId, x.MemberId, x.Reason)).Should().Equal(
            ("t-alpha", "m-missing", WorkflowBoardInvalidSelectionReason.MemberNotFound),
            ("t-alpha", "m-other-team", WorkflowBoardInvalidSelectionReason.MemberNotInTeam),
            ("t-alpha", "m-archived", WorkflowBoardInvalidSelectionReason.Archived),
            ("t-missing", "m-any", WorkflowBoardInvalidSelectionReason.TeamNotFound),
            ("t-archived", "m-any", WorkflowBoardInvalidSelectionReason.Archived));
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldDedupeRowsAndPreserveRequestOrder()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-beta"))
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-beta-2", "t-beta"))
            .AddMember(NewMember("m-beta-1", "t-beta"))
            .AddMember(NewMember("m-alpha-1", "t-alpha"));
        var service = NewService(roster);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [
                new WorkflowBoardTeamSelection("t-beta", ["m-beta-2", "m-beta-1", "m-beta-2"]),
                new WorkflowBoardTeamSelection("t-alpha", ["m-alpha-1"]),
            ]));

        snapshot.Teams.Select(x => x.TeamId).Should().Equal("t-beta", "t-alpha");
        snapshot.Teams[0].Members.Select(x => x.MemberId).Should().Equal("m-beta-2", "m-beta-1");
        snapshot.Teams[0].SelectedMemberCount.Should().Be(2);
        snapshot.Teams[1].Members.Select(x => x.MemberId).Should().Equal("m-alpha-1");
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldRejectInvalidRequestShapeBeforeQueryingRoster()
    {
        var roster = new InMemoryBoardRosterQueryPort();
        var service = NewService(roster);
        var tooManyMembers = Enumerable.Range(0, 25)
            .Select(index => $"m-{index}")
            .ToArray();

        var cases = new[]
        {
            new WorkflowBoardSnapshotRequest(ScopeId, []),
            new WorkflowBoardSnapshotRequest(ScopeId, [new WorkflowBoardTeamSelection("", ["m-1"])]),
            new WorkflowBoardSnapshotRequest(ScopeId, [new WorkflowBoardTeamSelection("t-1", [])]),
            new WorkflowBoardSnapshotRequest(ScopeId, [new WorkflowBoardTeamSelection("t-1", ["m-1"])], " "),
            new WorkflowBoardSnapshotRequest(
                ScopeId,
                Enumerable.Range(0, 5)
                    .Select(index => new WorkflowBoardTeamSelection($"t-{index}", ["m-1"]))
                    .ToArray()),
            new WorkflowBoardSnapshotRequest(
                ScopeId,
                [new WorkflowBoardTeamSelection("t-1", tooManyMembers)]),
            new WorkflowBoardSnapshotRequest(
                ScopeId,
                [new WorkflowBoardTeamSelection("t-1", ["m-1"])],
                new string('w', 257)),
        };

        foreach (var invalidRequest in cases)
        {
            await FluentActions.Invoking(() => service.GetSnapshotAsync(invalidRequest))
                .Should()
                .ThrowAsync<WorkflowBoardSnapshotRequestException>();
        }

        roster.TeamQueryCount.Should().Be(0);
        roster.MemberQueryCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldUseExecutionQueryOnlyForValidRowsAndMapAvailableExecution()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha") with
            {
                WorkflowId = "wf-from-roster",
                PublishedServiceId = "svc-from-roster",
                ActorId = "actor-from-roster",
            })
            .AddMember(NewMember("m-other-team", "t-other"));
        var execution = new RecordingExecutionQueryPort(new WorkflowBoardExecutionSnapshot(
            WorkflowBoardExecutionAvailability.Available,
            [new WorkflowBoardCompletedNode("n-1", "Validate", DateTimeOffset.Parse("2026-06-24T13:20:00Z"), 1000)],
            [new WorkflowBoardPendingNode("n-3", "Deploy", WorkflowBoardPendingNodeStatus.Pending)],
            [new WorkflowBoardFailedNode("n-2", "Check", DateTimeOffset.Parse("2026-06-24T13:21:00Z"))])
        {
            CurrentExecutionId = "run-alpha",
            CurrentNode = new WorkflowBoardCurrentNode(
                "n-current",
                "Run",
                WorkflowBoardCurrentNodeStatus.Running,
                DateTimeOffset.Parse("2026-06-24T13:22:00Z"),
                DateTimeOffset.Parse("2026-06-24T13:24:00Z"),
                120000),
            LastNodeUpdatedAt = DateTimeOffset.Parse("2026-06-24T13:24:00Z"),
            Totals = new WorkflowBoardTotals(1, 1, 1, 1),
            Revision = "state-version-7",
        });
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha", "m-other-team"])]));

        execution.Lookups.Should().ContainSingle();
        execution.Lookups[0].Should().Be(new WorkflowBoardExecutionLookup(
            ScopeId,
            "t-alpha",
            "m-alpha",
            "wf-from-roster",
            "svc-from-roster",
            "actor-from-roster"));
        var member = snapshot.Teams.Single().Members.Single();
        member.ExecutionAvailability.Should().Be(WorkflowBoardExecutionAvailability.Available);
        member.CurrentExecutionId.Should().Be("run-alpha");
        member.CurrentNode!.Status.Should().Be(WorkflowBoardCurrentNodeStatus.Running);
        member.CompletedNodes.Should().ContainSingle();
        member.PendingNodes.Should().ContainSingle();
        member.FailedNodes.Should().ContainSingle();
        snapshot.LastNodeUpdatedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:24:00Z"));
        snapshot.Totals.CompletedSteps.Should().Be(1);
        snapshot.Totals.RunningNodes.Should().Be(1);
        snapshot.Totals.WaitingOrPendingNodes.Should().Be(1);
        snapshot.Totals.FailedNodes.Should().Be(1);
        snapshot.InvalidSelections.Should().ContainSingle()
            .Which.Reason.Should().Be(WorkflowBoardInvalidSelectionReason.MemberNotInTeam);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldChangeWatermarkWhenExecutionProjectionRevisionChanges()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha"));
        var execution = new MutableExecutionQueryPort(new WorkflowBoardExecutionSnapshot(
            WorkflowBoardExecutionAvailability.Available,
            [],
            [],
            [])
        {
            Totals = new WorkflowBoardTotals(0, 1, 0, 0),
            Revision = "state-version-1:event-evt-1",
        });
        var service = NewService(roster, execution);

        var first = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha"])]));

        execution.Snapshot = new WorkflowBoardExecutionSnapshot(
            WorkflowBoardExecutionAvailability.Available,
            [],
            [],
            [])
        {
            Totals = new WorkflowBoardTotals(1, 0, 0, 0),
            Revision = "state-version-2:event-evt-2",
        };

        var second = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha"])]));

        first.Watermark.Should().NotBe(second.Watermark);
        first.Watermark.Should().StartWith("workflow-board:v1:");
        second.Watermark.Should().StartWith("workflow-board:v1:");
    }

    [Theory]
    [InlineData("team")]
    [InlineData("member")]
    public async Task GetSnapshotAsync_ShouldWrapTransientRosterReadFailuresAsUnavailable(string failingRead)
    {
        Exception transientFailure = failingRead == "team"
            ? new TimeoutException("team roster read timed out")
            : new IOException("member roster read failed");
        var roster = new ThrowingBoardRosterQueryPort(
            failingRead == "team" ? transientFailure : null,
            failingRead == "member" ? transientFailure : null);
        var service = NewService(roster);

        var act = () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha"])]));

        var exception = await act.Should().ThrowAsync<WorkflowBoardReadModelUnavailableException>();
        exception.Which.InnerException.Should().BeSameAs(transientFailure);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldWrapTransientExecutionReadFailuresAsUnavailable()
    {
        var transientFailure = new TimeoutException("execution read timed out");
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha"));
        var service = NewService(roster, new ThrowingExecutionQueryPort(transientFailure));

        var act = () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha"])]));

        var exception = await act.Should().ThrowAsync<WorkflowBoardReadModelUnavailableException>();
        exception.Which.InnerException.Should().BeSameAs(transientFailure);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldNotMapRequestValidationFailuresAsReadModelUnavailable()
    {
        var roster = new ThrowingBoardRosterQueryPort(new TimeoutException("must not be queried"));
        var service = NewService(roster);

        var act = () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(ScopeId, []));

        await act.Should().ThrowAsync<WorkflowBoardSnapshotRequestException>();
    }

    [Theory]
    [InlineData(WorkflowBoardExecutionAvailability.Unavailable)]
    [InlineData(WorkflowBoardExecutionAvailability.Unknown)]
    [InlineData(WorkflowBoardExecutionAvailability.PendingBackendContract)]
    public async Task GetSnapshotAsync_ShouldReturnNullableTotals_WhenExecutionAvailabilityIsNotAuthoritative(
        WorkflowBoardExecutionAvailability availability)
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha"));
        var execution = new RecordingExecutionQueryPort(new WorkflowBoardExecutionSnapshot(
            availability,
            [],
            [],
            []));
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha"])]));

        snapshot.Totals.CompletedSteps.Should().BeNull();
        snapshot.Totals.RunningNodes.Should().BeNull();
        snapshot.Totals.WaitingOrPendingNodes.Should().BeNull();
        snapshot.Totals.FailedNodes.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnNullableTotals_WhenAvailableExecutionLacksAuthoritativeTotals()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha"));
        var execution = new RecordingExecutionQueryPort(new WorkflowBoardExecutionSnapshot(
            WorkflowBoardExecutionAvailability.Available,
            [],
            [],
            []));
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha"])]));

        snapshot.Totals.CompletedSteps.Should().BeNull();
        snapshot.Totals.RunningNodes.Should().BeNull();
        snapshot.Totals.WaitingOrPendingNodes.Should().BeNull();
        snapshot.Totals.FailedNodes.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldAggregateAuthoritativeExecutionTotals()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha"))
            .AddMember(NewMember("m-beta", "t-alpha"));
        var execution = new LookupExecutionQueryPort()
            .Add("m-alpha", new WorkflowBoardExecutionSnapshot(
                WorkflowBoardExecutionAvailability.Available,
                [],
                [],
                [])
            {
                Totals = new WorkflowBoardTotals(2, 1, 3, 0),
            })
            .Add("m-beta", new WorkflowBoardExecutionSnapshot(
                WorkflowBoardExecutionAvailability.Available,
                [],
                [],
                [])
            {
                Totals = new WorkflowBoardTotals(4, 2, 5, 1),
            });
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            [new WorkflowBoardTeamSelection("t-alpha", ["m-alpha", "m-beta"])]));

        snapshot.Totals.CompletedSteps.Should().Be(6);
        snapshot.Totals.RunningNodes.Should().Be(3);
        snapshot.Totals.WaitingOrPendingNodes.Should().Be(8);
        snapshot.Totals.FailedNodes.Should().Be(1);
    }

    [Theory]
    [InlineData(MemberLifecycleStageNames.Created)]
    [InlineData(MemberLifecycleStageNames.BuildReady)]
    [InlineData(MemberLifecycleStageNames.BindReady)]
    public async Task StudioWorkflowBoardRosterQueryPort_ShouldMapCurrentMemberLifecycleStagesAsNotArchived(
        string lifecycleStage)
    {
        var memberPort = new FixedStudioMemberQueryPort(NewStudioMemberDetail(
            "m-alpha",
            "t-alpha",
            lifecycleStage));
        var adapter = new StudioWorkflowBoardRosterQueryPort(
            new FixedStudioTeamQueryPort(null),
            memberPort);

        var member = await adapter.GetMemberAsync(ScopeId, "m-alpha");

        member.Should().NotBeNull();
        member!.IsArchived.Should().BeFalse();
        member.MemberId.Should().Be("m-alpha");
        member.TeamId.Should().Be("t-alpha");
    }

    [Fact]
    public async Task StudioWorkflowBoardRosterQueryPort_ShouldMapActorIdFromLastBindingExpectedActorId()
    {
        var memberPort = new FixedStudioMemberQueryPort(NewStudioMemberDetail(
            "m-alpha",
            "t-alpha",
            MemberLifecycleStageNames.BindReady,
            expectedActorId: "actor-alpha"));
        var adapter = new StudioWorkflowBoardRosterQueryPort(
            new FixedStudioTeamQueryPort(null),
            memberPort);

        var member = await adapter.GetMemberAsync(ScopeId, "m-alpha");

        member.Should().NotBeNull();
        member!.ActorId.Should().Be("actor-alpha");
    }

    private sealed class InMemoryBoardRosterQueryPort : IWorkflowBoardRosterQueryPort
    {
        private readonly Dictionary<(string ScopeId, string TeamId), WorkflowBoardRosterTeam> _teams = new();
        private readonly Dictionary<(string ScopeId, string MemberId), WorkflowBoardRosterMember> _members = new();

        public int TeamQueryCount { get; private set; }
        public int MemberQueryCount { get; private set; }

        public InMemoryBoardRosterQueryPort AddTeam(WorkflowBoardRosterTeam team)
        {
            _teams[(team.ScopeId, team.TeamId)] = team;
            return this;
        }

        public InMemoryBoardRosterQueryPort AddMember(WorkflowBoardRosterMember member)
        {
            _members[(member.ScopeId, member.MemberId)] = member;
            return this;
        }

        public Task<WorkflowBoardRosterTeam?> GetTeamAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default)
        {
            TeamQueryCount++;
            return Task.FromResult(_teams.GetValueOrDefault((scopeId, teamId)));
        }

        public Task<WorkflowBoardRosterMember?> GetMemberAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            MemberQueryCount++;
            return Task.FromResult(_members.GetValueOrDefault((scopeId, memberId)));
        }
    }

    private sealed class ThrowingBoardRosterQueryPort(
        Exception? teamException = null,
        Exception? memberException = null) : IWorkflowBoardRosterQueryPort
    {
        public Task<WorkflowBoardRosterTeam?> GetTeamAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default)
        {
            if (teamException != null)
                return Task.FromException<WorkflowBoardRosterTeam?>(teamException);

            return Task.FromResult<WorkflowBoardRosterTeam?>(NewTeam(teamId));
        }

        public Task<WorkflowBoardRosterMember?> GetMemberAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            if (memberException != null)
                return Task.FromException<WorkflowBoardRosterMember?>(memberException);

            return Task.FromResult<WorkflowBoardRosterMember?>(NewMember(memberId, "t-alpha"));
        }
    }

    private sealed class RecordingExecutionQueryPort(WorkflowBoardExecutionSnapshot? snapshot)
        : IWorkflowBoardExecutionQueryPort
    {
        public List<WorkflowBoardExecutionLookup> Lookups { get; } = [];

        public Task<WorkflowBoardExecutionSnapshot?> GetCurrentExecutionAsync(
            WorkflowBoardExecutionLookup lookup,
            CancellationToken ct = default)
        {
            Lookups.Add(lookup);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class MutableExecutionQueryPort(WorkflowBoardExecutionSnapshot? snapshot)
        : IWorkflowBoardExecutionQueryPort
    {
        public WorkflowBoardExecutionSnapshot? Snapshot { get; set; } = snapshot;

        public Task<WorkflowBoardExecutionSnapshot?> GetCurrentExecutionAsync(
            WorkflowBoardExecutionLookup lookup,
            CancellationToken ct = default) =>
            Task.FromResult(Snapshot);
    }

    private sealed class LookupExecutionQueryPort : IWorkflowBoardExecutionQueryPort
    {
        private readonly Dictionary<string, WorkflowBoardExecutionSnapshot> _snapshots = new(StringComparer.Ordinal);

        public LookupExecutionQueryPort Add(string memberId, WorkflowBoardExecutionSnapshot snapshot)
        {
            _snapshots[memberId] = snapshot;
            return this;
        }

        public Task<WorkflowBoardExecutionSnapshot?> GetCurrentExecutionAsync(
            WorkflowBoardExecutionLookup lookup,
            CancellationToken ct = default) =>
            Task.FromResult(_snapshots.GetValueOrDefault(lookup.MemberId));
    }

    private sealed class ThrowingExecutionQueryPort(Exception exception) : IWorkflowBoardExecutionQueryPort
    {
        public Task<WorkflowBoardExecutionSnapshot?> GetCurrentExecutionAsync(
            WorkflowBoardExecutionLookup lookup,
            CancellationToken ct = default) =>
            Task.FromException<WorkflowBoardExecutionSnapshot?>(exception);
    }

    private sealed class FixedStudioTeamQueryPort(StudioTeamSummaryResponse? team) : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, team == null ? [] : [team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            Task.FromResult(team);
    }

    private sealed class FixedStudioMemberQueryPort(StudioMemberDetailResponse? member) : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, member == null ? [] : [member.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult(member);
    }

    private sealed class FixedWorkflowBoardClock(DateTimeOffset now) : IWorkflowBoardClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }

    private static WorkflowBoardSnapshotQueryService NewService(
        IWorkflowBoardRosterQueryPort roster,
        IWorkflowBoardExecutionQueryPort? execution = null) =>
        new(
            roster,
            execution,
            new FixedWorkflowBoardClock(DateTimeOffset.Parse("2026-06-24T13:24:16Z")));

    private static WorkflowBoardRosterTeam NewTeam(
        string teamId,
        string? name = null,
        bool archived = false,
        int? memberCount = 8) =>
        new(
            teamId,
            ScopeId,
            name ?? teamId,
            archived,
            memberCount,
            DateTimeOffset.Parse("2026-06-24T10:00:00Z"));

    private static WorkflowBoardRosterMember NewMember(
        string memberId,
        string teamId,
        bool archived = false) =>
        new(
            memberId,
            ScopeId,
            teamId,
            memberId,
            archived,
            PublishedServiceId: $"svc-{memberId}",
            WorkflowId: $"wf-{memberId}",
            WorkflowName: $"Workflow {memberId}",
            ActorId: $"actor-{memberId}",
            RoleSummary: $"role {memberId}",
            UpdatedAt: DateTimeOffset.Parse("2026-06-24T10:01:00Z"));

    private static StudioMemberDetailResponse NewStudioMemberDetail(
        string memberId,
        string teamId,
        string lifecycleStage,
        string? expectedActorId = null)
    {
        var implementationRef = new StudioMemberImplementationRefResponse(
            MemberImplementationKindNames.Workflow,
            WorkflowId: $"wf-{memberId}");
        var summary = new StudioMemberSummaryResponse(
            memberId,
            ScopeId,
            memberId,
            $"role {memberId}",
            MemberImplementationKindNames.Workflow,
            lifecycleStage,
            $"svc-{memberId}",
            "rev-1",
            DateTimeOffset.Parse("2026-06-24T10:00:00Z"),
            DateTimeOffset.Parse("2026-06-24T10:01:00Z"))
        {
            TeamId = teamId,
            ImplementationRef = implementationRef,
        };

        return new StudioMemberDetailResponse(
            summary,
            implementationRef,
            new StudioMemberBindingContractResponse(
                $"svc-{memberId}",
                "rev-1",
                MemberImplementationKindNames.Workflow,
                DateTimeOffset.Parse("2026-06-24T10:02:00Z"),
                expectedActorId))
        {
            CurrentBindingRun = new StudioMemberBindingRunStatusResponse(
                "bind-run-1",
                ScopeId,
                memberId,
                StudioMemberBindingRunStatusNames.Succeeded,
                7,
                UpdatedAt: DateTimeOffset.Parse("2026-06-24T10:03:00Z"))
            {
                Result = new StudioMemberBindingRunResultResponse(
                    $"svc-{memberId}",
                    "rev-1",
                    MemberImplementationKindNames.Workflow,
                    expectedActorId ?? $"actor-{memberId}"),
            },
        };
    }
}
