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
    public void WorkflowBoardSnapshotRequestLimits_ShouldExposeMemberRowLimits()
    {
        WorkflowBoardSnapshotRequestLimits.DefaultMemberRows.Should().Be(20);
        WorkflowBoardSnapshotRequestLimits.MaxMemberRows.Should().Be(100);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnVisibleScopeMembers_WhenNoFilterIsSupplied()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-protocol", name: "Protocol"))
            .AddTeam(NewTeam("t-ops", name: "Ops"))
            .AddMember(NewMember("m-alpha", "t-protocol"))
            .AddMember(NewMember("m-beta", "t-protocol"))
            .AddMember(NewMember("m-gamma", "t-ops"));
        var execution = new LookupExecutionQueryPort()
            .Add("m-alpha", Available(WorkflowBoardMemberExecutionStatus.Running, completed: 3, running: 1, waiting: 2, failed: 0))
            .Add("m-beta", Available(WorkflowBoardMemberExecutionStatus.Completed, completed: 4, running: 0, waiting: 0, failed: 0))
            .Add("m-gamma", Available(WorkflowBoardMemberExecutionStatus.Waiting, completed: 1, running: 0, waiting: 1, failed: 0));
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(ScopeId));

        snapshot.ScopeId.Should().Be(ScopeId);
        snapshot.GeneratedAt.Should().Be(DateTimeOffset.Parse("2026-06-24T13:24:16Z"));
        snapshot.Teams.Select(static team => team.TeamId).Should().Equal("t-protocol", "t-ops");
        snapshot.Teams[0].Members.Select(static member => member.MemberId).Should().Equal("m-alpha", "m-beta");
        snapshot.Teams[1].Members.Select(static member => member.MemberId).Should().Equal("m-gamma");
        snapshot.Counts.Should().Be(new WorkflowBoardSnapshotCounts(
            Running: 1,
            Waiting: 1,
            Failed: 0,
            Retrying: 0,
            Completed: 1));
        snapshot.Watermark.Should().StartWith("workflow-board:v2:");
        roster.TeamListRequests.Should().ContainSingle(ScopeId);
        roster.MemberListRequests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, null, WorkflowBoardSnapshotRequestLimits.DefaultMemberRows));
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldFilterMembersByTeamAndApplyTakeAsRowLimit()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-protocol", name: "Protocol", memberCount: 3))
            .AddTeam(NewTeam("t-ops", name: "Ops"))
            .AddMember(NewMember("m-alpha", "t-protocol"))
            .AddMember(NewMember("m-beta", "t-protocol"))
            .AddMember(NewMember("m-gamma", "t-protocol"))
            .AddMember(NewMember("m-ops", "t-ops"));
        var service = NewService(roster);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            TeamId: "t-protocol",
            Take: 2));

        snapshot.Teams.Should().ContainSingle();
        snapshot.Teams[0].TeamId.Should().Be("t-protocol");
        snapshot.Teams[0].TeamName.Should().Be("Protocol");
        snapshot.Teams[0].TotalMemberCount.Should().Be(3);
        snapshot.Teams[0].Members.Select(static member => member.MemberId).Should().Equal("m-alpha", "m-beta");
        snapshot.Counts.Should().Be(new WorkflowBoardSnapshotCounts(0, 0, 0, 0, 0));
        roster.MemberListRequests.Should().ContainSingle()
            .Which.Should().Be((ScopeId, "t-protocol", 2));
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldReturnOnlyExactMember_WhenTeamAndMemberAreSupplied()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-protocol"))
            .AddMember(NewMember("m-alpha", "t-protocol") with
            {
                WorkflowId = "wf-alpha",
                PublishedServiceId = "svc-alpha",
                ActorId = "actor-alpha",
            })
            .AddMember(NewMember("m-beta", "t-protocol"));
        var execution = new RecordingExecutionQueryPort(Available(
            WorkflowBoardMemberExecutionStatus.Running,
            completed: 3,
            running: 1,
            waiting: 2,
            failed: 0,
            definitionSteps: 15,
            currentExecutionId: "run-alpha"));
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            TeamId: "t-protocol",
            MemberId: "m-alpha",
            Take: 100));

        var member = snapshot.Teams.Should().ContainSingle().Subject.Members.Should().ContainSingle().Subject;
        member.MemberId.Should().Be("m-alpha");
        member.WorkflowId.Should().Be("wf-alpha");
        member.PublishedServiceId.Should().Be("svc-alpha");
        member.CurrentExecutionId.Should().Be("run-alpha");
        member.ExecutionStatus.Should().Be(WorkflowBoardMemberExecutionStatus.Running);
        member.Progress.Should().Be(new WorkflowBoardMemberProgress(3, 15));
        snapshot.Counts.Should().Be(new WorkflowBoardSnapshotCounts(1, 0, 0, 0, 0));
        roster.MemberListRequests.Should().BeEmpty();
        execution.Lookups.Should().ContainSingle()
            .Which.Should().Be(new WorkflowBoardExecutionLookup(
                ScopeId,
                "t-protocol",
                "m-alpha",
                "wf-alpha",
                "svc-alpha",
                "actor-alpha"));
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldRejectMemberFilterWithoutTeamBeforeQueryingRoster()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-protocol"))
            .AddMember(NewMember("m-alpha", "t-protocol"));
        var service = NewService(roster);

        var act = () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            MemberId: "m-alpha"));

        await act.Should().ThrowAsync<WorkflowBoardSnapshotRequestException>()
            .WithMessage("memberId requires teamId.");
        roster.TeamQueryCount.Should().Be(0);
        roster.MemberQueryCount.Should().Be(0);
        roster.MemberListRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldRejectInvalidRequestShapeBeforeQueryingRoster()
    {
        var roster = new InMemoryBoardRosterQueryPort();
        var service = NewService(roster);

        var cases = new[]
        {
            new WorkflowBoardSnapshotRequest(" "),
            new WorkflowBoardSnapshotRequest(ScopeId, TeamId: " "),
            new WorkflowBoardSnapshotRequest(ScopeId, TeamId: "t-alpha", MemberId: " "),
            new WorkflowBoardSnapshotRequest(ScopeId, TeamId: "t-alpha", Take: 0),
            new WorkflowBoardSnapshotRequest(ScopeId, TeamId: "t-alpha", Take: -1),
            new WorkflowBoardSnapshotRequest(
                ScopeId,
                TeamId: "t-alpha",
                Take: WorkflowBoardSnapshotRequestLimits.MaxMemberRows + 1),
        };

        foreach (var invalidRequest in cases)
        {
            await FluentActions.Invoking(() => service.GetSnapshotAsync(invalidRequest))
                .Should()
                .ThrowAsync<WorkflowBoardSnapshotRequestException>();
        }

        roster.TeamQueryCount.Should().Be(0);
        roster.MemberQueryCount.Should().Be(0);
        roster.MemberListRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldRejectMissingOrWrongTeamMemberWithoutPartialInvalidRows()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-other-team", "t-other"));
        var service = NewService(roster);

        await FluentActions.Invoking(() => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
                ScopeId,
                TeamId: "t-missing")))
            .Should()
            .ThrowAsync<WorkflowBoardSnapshotRequestException>()
            .WithMessage("teamId was not found.");
        await FluentActions.Invoking(() => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
                ScopeId,
                TeamId: "t-alpha",
                MemberId: "m-missing")))
            .Should()
            .ThrowAsync<WorkflowBoardSnapshotRequestException>()
            .WithMessage("memberId was not found.");
        await FluentActions.Invoking(() => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
                ScopeId,
                TeamId: "t-alpha",
                MemberId: "m-other-team")))
            .Should()
            .ThrowAsync<WorkflowBoardSnapshotRequestException>()
            .WithMessage("memberId does not belong to teamId.");
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldMapProgressOnlyFromCompleteAvailableExecutionSummary()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-available", "t-alpha"))
            .AddMember(NewMember("m-incomplete", "t-alpha"))
            .AddMember(NewMember("m-unavailable", "t-alpha"));
        var execution = new LookupExecutionQueryPort()
            .Add("m-available", Available(WorkflowBoardMemberExecutionStatus.Completed, completed: 2, running: 1, waiting: 3, failed: 4, definitionSteps: 15))
            .Add("m-incomplete", new WorkflowBoardExecutionSnapshot(
                WorkflowBoardExecutionAvailability.Available,
                [],
                [],
                [])
            {
                ExecutionStatus = WorkflowBoardMemberExecutionStatus.Running,
                Summary = new WorkflowBoardExecutionSummary(1, 0, 0, 0, null),
            })
            .Add("m-unavailable", new WorkflowBoardExecutionSnapshot(
                WorkflowBoardExecutionAvailability.Unavailable,
                [],
                [],
                []));
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            TeamId: "t-alpha"));

        var members = snapshot.Teams.Single().Members.ToDictionary(static member => member.MemberId);
        members["m-available"].Progress.Should().Be(new WorkflowBoardMemberProgress(2, 15));
        members["m-incomplete"].Progress.Should().BeNull();
        members["m-unavailable"].Progress.Should().BeNull();
        members["m-unavailable"].ExecutionStatus.Should().Be(WorkflowBoardMemberExecutionStatus.Unknown);
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldLeaveStoppedAndUnknownOutsideCounts()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-running", "t-alpha"))
            .AddMember(NewMember("m-waiting", "t-alpha"))
            .AddMember(NewMember("m-failed", "t-alpha"))
            .AddMember(NewMember("m-retrying", "t-alpha"))
            .AddMember(NewMember("m-completed", "t-alpha"))
            .AddMember(NewMember("m-stopped", "t-alpha"))
            .AddMember(NewMember("m-unknown", "t-alpha"));
        var execution = new LookupExecutionQueryPort()
            .Add("m-running", Available(WorkflowBoardMemberExecutionStatus.Running))
            .Add("m-waiting", Available(WorkflowBoardMemberExecutionStatus.Waiting))
            .Add("m-failed", Available(WorkflowBoardMemberExecutionStatus.Failed))
            .Add("m-retrying", Available(WorkflowBoardMemberExecutionStatus.Retrying))
            .Add("m-completed", Available(WorkflowBoardMemberExecutionStatus.Completed))
            .Add("m-stopped", Available(WorkflowBoardMemberExecutionStatus.Stopped))
            .Add("m-unknown", Available(WorkflowBoardMemberExecutionStatus.Unknown));
        var service = NewService(roster, execution);

        var snapshot = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            TeamId: "t-alpha"));

        snapshot.Counts.Should().Be(new WorkflowBoardSnapshotCounts(
            Running: 1,
            Waiting: 1,
            Failed: 1,
            Retrying: 1,
            Completed: 1));
    }

    [Fact]
    public async Task GetSnapshotAsync_ShouldChangeWatermarkWhenExecutionProjectionRevisionChanges()
    {
        var roster = new InMemoryBoardRosterQueryPort()
            .AddTeam(NewTeam("t-alpha"))
            .AddMember(NewMember("m-alpha", "t-alpha"));
        var execution = new MutableExecutionQueryPort(Available(
            WorkflowBoardMemberExecutionStatus.Running,
            revision: "state-version-1:event-evt-1"));
        var service = NewService(roster, execution);

        var first = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            TeamId: "t-alpha",
            MemberId: "m-alpha"));

        execution.Snapshot = Available(
            WorkflowBoardMemberExecutionStatus.Completed,
            revision: "state-version-2:event-evt-2");

        var second = await service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
            ScopeId,
            TeamId: "t-alpha",
            MemberId: "m-alpha"));

        first.Watermark.Should().NotBe(second.Watermark);
        first.Watermark.Should().StartWith("workflow-board:v2:");
        second.Watermark.Should().StartWith("workflow-board:v2:");
    }

    [Theory]
    [InlineData("team")]
    [InlineData("member")]
    [InlineData("list")]
    public async Task GetSnapshotAsync_ShouldWrapTransientRosterReadFailuresAsUnavailable(string failingRead)
    {
        Exception transientFailure = new TimeoutException("roster read timed out");
        var roster = new ThrowingBoardRosterQueryPort(
            failingRead == "team" ? transientFailure : null,
            failingRead == "member" ? transientFailure : null,
            failingRead == "list" ? transientFailure : null);
        var service = NewService(roster);
        Func<Task> act = failingRead switch
        {
            "team" => () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(ScopeId, TeamId: "t-alpha")),
            "member" => () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(
                ScopeId,
                TeamId: "t-alpha",
                MemberId: "m-alpha")),
            _ => () => service.GetSnapshotAsync(new WorkflowBoardSnapshotRequest(ScopeId)),
        };

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
            TeamId: "t-alpha",
            MemberId: "m-alpha"));

        var exception = await act.Should().ThrowAsync<WorkflowBoardReadModelUnavailableException>();
        exception.Which.InnerException.Should().BeSameAs(transientFailure);
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

    [Fact]
    public async Task StudioWorkflowBoardRosterQueryPort_ShouldListTeamMembersUsingReadModelTeamFilter()
    {
        var teamPort = new FixedStudioTeamQueryPort(NewStudioTeamSummary("t-alpha"));
        var memberPort = new FixedStudioMemberQueryPort(NewStudioMemberDetail(
            "m-alpha",
            "t-alpha",
            MemberLifecycleStageNames.BindReady));
        var adapter = new StudioWorkflowBoardRosterQueryPort(teamPort, memberPort);

        var members = await adapter.ListMembersAsync(ScopeId, "t-alpha", 7);

        members.Should().ContainSingle().Which.MemberId.Should().Be("m-alpha");
        memberPort.ListPages.Should().ContainSingle()
            .Which.Should().Be(new StudioMemberRosterPageRequest(PageSize: 7, TeamId: "t-alpha"));
    }

    private sealed class InMemoryBoardRosterQueryPort : IWorkflowBoardRosterQueryPort
    {
        private readonly Dictionary<(string ScopeId, string TeamId), WorkflowBoardRosterTeam> _teams = new();
        private readonly Dictionary<(string ScopeId, string MemberId), WorkflowBoardRosterMember> _members = new();

        public int TeamQueryCount { get; private set; }
        public int MemberQueryCount { get; private set; }
        public List<string> TeamListRequests { get; } = [];
        public List<(string ScopeId, string? TeamId, int Take)> MemberListRequests { get; } = [];

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

        public Task<IReadOnlyList<WorkflowBoardRosterTeam>> ListTeamsAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            TeamListRequests.Add(scopeId);
            var teams = _teams.Values
                .Where(team => string.Equals(team.ScopeId, scopeId, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowBoardRosterTeam>>(teams);
        }

        public Task<IReadOnlyList<WorkflowBoardRosterMember>> ListMembersAsync(
            string scopeId,
            string? teamId,
            int take,
            CancellationToken ct = default)
        {
            MemberListRequests.Add((scopeId, teamId, take));
            var members = _members.Values
                .Where(member => string.Equals(member.ScopeId, scopeId, StringComparison.Ordinal))
                .Where(member => teamId == null || string.Equals(member.TeamId, teamId, StringComparison.Ordinal))
                .Take(take)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowBoardRosterMember>>(members);
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
        Exception? memberException = null,
        Exception? listException = null) : IWorkflowBoardRosterQueryPort
    {
        public Task<IReadOnlyList<WorkflowBoardRosterTeam>> ListTeamsAsync(
            string scopeId,
            CancellationToken ct = default)
        {
            if (listException != null)
                return Task.FromException<IReadOnlyList<WorkflowBoardRosterTeam>>(listException);

            return Task.FromResult<IReadOnlyList<WorkflowBoardRosterTeam>>([NewTeam("t-alpha")]);
        }

        public Task<IReadOnlyList<WorkflowBoardRosterMember>> ListMembersAsync(
            string scopeId,
            string? teamId,
            int take,
            CancellationToken ct = default)
        {
            if (listException != null)
                return Task.FromException<IReadOnlyList<WorkflowBoardRosterMember>>(listException);

            return Task.FromResult<IReadOnlyList<WorkflowBoardRosterMember>>([NewMember("m-alpha", teamId ?? "t-alpha")]);
        }

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
        public List<StudioMemberRosterPageRequest?> ListPages { get; } = [];

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            ListPages.Add(page);
            return Task.FromResult(new StudioMemberRosterResponse(scopeId, member == null ? [] : [member.Summary]));
        }

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

    private static WorkflowBoardExecutionSnapshot Available(
        WorkflowBoardMemberExecutionStatus status,
        int completed = 0,
        int running = 0,
        int waiting = 0,
        int failed = 0,
        int? definitionSteps = 10,
        string? currentExecutionId = null,
        string? revision = null) =>
        new(
            WorkflowBoardExecutionAvailability.Available,
            [],
            [],
            [])
        {
            CurrentExecutionId = currentExecutionId,
            ExecutionStatus = status,
            Summary = new WorkflowBoardExecutionSummary(completed, running, waiting, failed, definitionSteps),
            Revision = revision,
        };

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
            PublishedServiceId: memberId == "m-alpha" ? "svc-alpha" : $"svc-{memberId}",
            WorkflowId: memberId == "m-alpha" ? "wf-alpha" : $"wf-{memberId}",
            WorkflowName: $"Workflow {memberId}",
            ActorId: $"actor-{memberId}",
            RoleSummary: $"role {memberId}",
            UpdatedAt: DateTimeOffset.Parse("2026-06-24T10:01:00Z"));

    private static StudioTeamSummaryResponse NewStudioTeamSummary(string teamId) =>
        new(
            teamId,
            ScopeId,
            teamId,
            $"team {teamId}",
            TeamLifecycleStageNames.Active,
            1,
            DateTimeOffset.Parse("2026-06-24T10:00:00Z"),
            DateTimeOffset.Parse("2026-06-24T10:01:00Z"));

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
