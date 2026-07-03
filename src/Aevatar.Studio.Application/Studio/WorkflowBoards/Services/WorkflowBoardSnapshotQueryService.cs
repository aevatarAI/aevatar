using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.WorkflowBoards.Services;

public sealed class WorkflowBoardSnapshotQueryService : IWorkflowBoardSnapshotQueryPort
{
    private readonly IWorkflowBoardRosterQueryPort _rosterQueryPort;
    private readonly IWorkflowBoardExecutionQueryPort? _executionQueryPort;
    private readonly IWorkflowBoardClock _clock;

    public WorkflowBoardSnapshotQueryService(
        IWorkflowBoardRosterQueryPort rosterQueryPort,
        IWorkflowBoardExecutionQueryPort? executionQueryPort,
        IWorkflowBoardClock clock)
    {
        _rosterQueryPort = rosterQueryPort ?? throw new ArgumentNullException(nameof(rosterQueryPort));
        _executionQueryPort = executionQueryPort;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<WorkflowBoardSnapshot> GetSnapshotAsync(
        WorkflowBoardSnapshotRequest request,
        CancellationToken ct = default)
    {
        var normalized = NormalizeAndValidate(request);
        var teams = normalized.TeamId == null
            ? await BuildScopeSliceAsync(normalized, ct)
            : await BuildTeamSliceAsync(normalized, ct);
        var watermarkFacts = teams
            .SelectMany(static team => team.Members.Select(member => BuildMemberWatermarkFact(team, member)))
            .ToArray();

        return new WorkflowBoardSnapshot(
            normalized.ScopeId,
            _clock.GetUtcNow(),
            BuildWatermark(normalized, watermarkFacts),
            CalculateCounts(teams),
            teams,
            LatestNodeUpdate(teams));
    }

    private async Task<IReadOnlyList<WorkflowBoardTeamSnapshot>> BuildScopeSliceAsync(
        WorkflowBoardSnapshotRequest request,
        CancellationToken ct)
    {
        var teamRows = await ListTeamsAsync(request.ScopeId, ct);
        var memberRows = await ListMembersAsync(
            request.ScopeId,
            teamId: null,
            NormalizeTake(request.Take),
            ct);

        return await BuildTeamsAsync(teamRows, memberRows, ct);
    }

    private async Task<IReadOnlyList<WorkflowBoardTeamSnapshot>> BuildTeamSliceAsync(
        WorkflowBoardSnapshotRequest request,
        CancellationToken ct)
    {
        var teamId = request.TeamId!;
        var team = await ReadTeamAsync(request.ScopeId, teamId, ct);
        if (team == null)
            throw new WorkflowBoardSnapshotRequestException("teamId was not found.");

        if (team.IsArchived)
            throw new WorkflowBoardSnapshotRequestException("teamId is archived.");

        var memberRows = request.MemberId == null
            ? await ListMembersAsync(request.ScopeId, teamId, NormalizeTake(request.Take), ct)
            : [await ReadRequiredMemberAsync(request.ScopeId, teamId, request.MemberId, ct)];

        return await BuildTeamsAsync([team], memberRows, ct);
    }

    private async Task<IReadOnlyList<WorkflowBoardTeamSnapshot>> BuildTeamsAsync(
        IReadOnlyList<WorkflowBoardRosterTeam> teamRows,
        IReadOnlyList<WorkflowBoardRosterMember> memberRows,
        CancellationToken ct)
    {
        var teamById = teamRows
            .Where(static team => !team.IsArchived)
            .ToDictionary(static team => team.TeamId, StringComparer.Ordinal);
        var teams = new List<WorkflowBoardTeamSnapshot>();
        var membersByTeam = memberRows
            .Where(static member => !member.IsArchived)
            .Where(static member => !string.IsNullOrWhiteSpace(member.TeamId))
            .GroupBy(static member => member.TeamId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);

        foreach (var team in teamRows)
        {
            if (team.IsArchived || !membersByTeam.TryGetValue(team.TeamId, out var members))
                continue;

            var mappedMembers = new List<WorkflowBoardMemberSnapshot>();
            foreach (var member in members)
            {
                if (!teamById.ContainsKey(member.TeamId!))
                    continue;

                var execution = await GetExecutionSnapshotAsync(team.TeamId, member, ct);
                mappedMembers.Add(MapMember(member, execution));
            }

            if (mappedMembers.Count > 0)
            {
                teams.Add(new WorkflowBoardTeamSnapshot(
                    team.TeamId,
                    team.DisplayName,
                    team.TotalMemberCount,
                    mappedMembers));
            }
        }

        return teams;
    }

    private static WorkflowBoardSnapshotRequest NormalizeAndValidate(WorkflowBoardSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ScopeId))
            throw new WorkflowBoardSnapshotRequestException("scopeId is required.");

        var scopeId = request.ScopeId.Trim();
        var teamId = NormalizeOptionalId(request.TeamId, "teamId");
        var memberId = NormalizeOptionalId(request.MemberId, "memberId");
        if (memberId != null && teamId == null)
            throw new WorkflowBoardSnapshotRequestException("memberId requires teamId.");

        _ = NormalizeTake(request.Take);
        return new WorkflowBoardSnapshotRequest(scopeId, teamId, memberId, request.Take);
    }

    private async Task<IReadOnlyList<WorkflowBoardRosterTeam>> ListTeamsAsync(
        string scopeId,
        CancellationToken ct)
    {
        try
        {
            return await _rosterQueryPort.ListTeamsAsync(scopeId, ct);
        }
        catch (Exception ex) when (IsTransientReadModelFailure(ex, ct))
        {
            throw new WorkflowBoardReadModelUnavailableException(
                "Workflow board roster read model is temporarily unavailable.",
                ex);
        }
    }

    private async Task<IReadOnlyList<WorkflowBoardRosterMember>> ListMembersAsync(
        string scopeId,
        string? teamId,
        int take,
        CancellationToken ct)
    {
        try
        {
            return await _rosterQueryPort.ListMembersAsync(scopeId, teamId, take, ct);
        }
        catch (Exception ex) when (IsTransientReadModelFailure(ex, ct))
        {
            throw new WorkflowBoardReadModelUnavailableException(
                "Workflow board roster read model is temporarily unavailable.",
                ex);
        }
    }

    private async Task<WorkflowBoardRosterTeam?> ReadTeamAsync(
        string scopeId,
        string teamId,
        CancellationToken ct)
    {
        try
        {
            return await _rosterQueryPort.GetTeamAsync(scopeId, teamId, ct);
        }
        catch (Exception ex) when (IsTransientReadModelFailure(ex, ct))
        {
            throw new WorkflowBoardReadModelUnavailableException(
                "Workflow board roster read model is temporarily unavailable.",
                ex);
        }
    }

    private async Task<WorkflowBoardRosterMember?> ReadMemberAsync(
        string scopeId,
        string memberId,
        CancellationToken ct)
    {
        try
        {
            return await _rosterQueryPort.GetMemberAsync(scopeId, memberId, ct);
        }
        catch (Exception ex) when (IsTransientReadModelFailure(ex, ct))
        {
            throw new WorkflowBoardReadModelUnavailableException(
                "Workflow board roster read model is temporarily unavailable.",
                ex);
        }
    }

    private async Task<WorkflowBoardRosterMember> ReadRequiredMemberAsync(
        string scopeId,
        string teamId,
        string memberId,
        CancellationToken ct)
    {
        var member = await ReadMemberAsync(scopeId, memberId, ct);
        if (member == null)
            throw new WorkflowBoardSnapshotRequestException("memberId was not found.");

        if (member.IsArchived)
            throw new WorkflowBoardSnapshotRequestException("memberId is archived.");

        if (!string.Equals(member.TeamId, teamId, StringComparison.Ordinal))
            throw new WorkflowBoardSnapshotRequestException("memberId does not belong to teamId.");

        return member;
    }

    private async Task<WorkflowBoardExecutionSnapshot?> GetExecutionSnapshotAsync(
        string teamId,
        WorkflowBoardRosterMember member,
        CancellationToken ct)
    {
        if (_executionQueryPort == null)
            return null;

        var lookup = new WorkflowBoardExecutionLookup(
            member.ScopeId,
            teamId,
            member.MemberId,
            member.WorkflowId,
            member.PublishedServiceId,
            member.ActorId);
        try
        {
            return await _executionQueryPort.GetCurrentExecutionAsync(lookup, ct);
        }
        catch (Exception ex) when (IsTransientReadModelFailure(ex, ct))
        {
            throw new WorkflowBoardReadModelUnavailableException(
                "Workflow board execution read model is temporarily unavailable.",
                ex);
        }
    }

    private static WorkflowBoardMemberSnapshot MapMember(
        WorkflowBoardRosterMember member,
        WorkflowBoardExecutionSnapshot? execution)
    {
        var availability = execution?.Availability ?? WorkflowBoardExecutionAvailability.PendingBackendContract;
        return new WorkflowBoardMemberSnapshot(
            member.MemberId,
            member.DisplayName,
            availability,
            execution?.CompletedNodes ?? Array.Empty<WorkflowBoardCompletedNode>(),
            execution?.PendingNodes ?? Array.Empty<WorkflowBoardPendingNode>(),
            execution?.FailedNodes ?? Array.Empty<WorkflowBoardFailedNode>())
        {
            WorkflowId = member.WorkflowId,
            WorkflowName = member.WorkflowName,
            PublishedServiceId = member.PublishedServiceId,
            ActorId = member.ActorId,
            RoleSummary = member.RoleSummary,
            CurrentExecutionId = execution?.CurrentExecutionId,
            ExecutionRevision = execution?.Revision,
            ExecutionStatus = MapExecutionStatus(execution),
            Progress = MapProgress(execution),
            CurrentNode = execution?.CurrentNode,
            LastNodeUpdatedAt = execution?.LastNodeUpdatedAt,
        };
    }

    private static WorkflowBoardMemberExecutionStatus MapExecutionStatus(WorkflowBoardExecutionSnapshot? execution)
    {
        if (execution is not { Availability: WorkflowBoardExecutionAvailability.Available })
            return WorkflowBoardMemberExecutionStatus.Unknown;

        return execution.ExecutionStatus;
    }

    private static WorkflowBoardMemberProgress? MapProgress(WorkflowBoardExecutionSnapshot? execution)
    {
        if (execution is not { Availability: WorkflowBoardExecutionAvailability.Available })
            return null;

        if (execution.Summary is not
            {
                CompletedSteps: not null,
                DefinitionStepCount: not null,
            } summary)
        {
            return null;
        }

        return new WorkflowBoardMemberProgress(
            summary.CompletedSteps.Value,
            summary.DefinitionStepCount.Value);
    }

    private static WorkflowBoardSnapshotCounts CalculateCounts(IReadOnlyList<WorkflowBoardTeamSnapshot> teams)
    {
        var members = teams.SelectMany(static team => team.Members).ToArray();
        return new WorkflowBoardSnapshotCounts(
            members.Count(static member => member.ExecutionStatus == WorkflowBoardMemberExecutionStatus.Running),
            members.Count(static member => member.ExecutionStatus == WorkflowBoardMemberExecutionStatus.Waiting),
            members.Count(static member => member.ExecutionStatus == WorkflowBoardMemberExecutionStatus.Failed),
            members.Count(static member => member.ExecutionStatus == WorkflowBoardMemberExecutionStatus.Retrying),
            members.Count(static member => member.ExecutionStatus == WorkflowBoardMemberExecutionStatus.Completed));
    }

    private static DateTimeOffset? LatestNodeUpdate(IReadOnlyList<WorkflowBoardTeamSnapshot> teams) =>
        teams.SelectMany(static team => team.Members)
            .Select(static member => member.LastNodeUpdatedAt)
            .Where(static value => value.HasValue)
            .Max();

    private static string BuildMemberWatermarkFact(
        WorkflowBoardTeamSnapshot team,
        WorkflowBoardMemberSnapshot member) =>
        string.Join(
            ':',
            "member",
            team.TeamId,
            member.MemberId,
            member.DisplayName,
            member.PublishedServiceId ?? "null",
            member.WorkflowId ?? "null",
            member.WorkflowName ?? "null",
            member.ActorId ?? "null",
            member.RoleSummary ?? "null",
            member.CurrentExecutionId ?? "null",
            member.ExecutionRevision ?? "null",
            member.ExecutionAvailability,
            member.ExecutionStatus,
            FormatTimestamp(member.LastNodeUpdatedAt));

    private static string BuildWatermark(
        WorkflowBoardSnapshotRequest request,
        IReadOnlyList<string> facts)
    {
        var filterMaterial = string.Join(
            '\n',
            request.ScopeId,
            request.TeamId ?? "all-teams",
            request.MemberId ?? "all-members",
            NormalizeTake(request.Take).ToString());
        var factMaterial = string.Join('\n', facts);
        var filterHash = Hash(filterMaterial);
        var factHash = Hash(factMaterial);
        return $"workflow-board:v2:{filterHash}:{factHash}";
    }

    private static string? NormalizeOptionalId(string? value, string fieldName)
    {
        if (value == null)
            return null;

        if (string.IsNullOrWhiteSpace(value))
            throw new WorkflowBoardSnapshotRequestException($"{fieldName} must not be blank.");

        return value.Trim();
    }

    private static int NormalizeTake(int? take)
    {
        if (take == null)
            return WorkflowBoardSnapshotRequestLimits.DefaultMemberRows;

        if (take <= 0)
            throw new WorkflowBoardSnapshotRequestException("take must be greater than zero.");

        if (take > WorkflowBoardSnapshotRequestLimits.MaxMemberRows)
            throw new WorkflowBoardSnapshotRequestException(
                $"take must be at most {WorkflowBoardSnapshotRequestLimits.MaxMemberRows}.");

        return take.Value;
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O") ?? "null";

    private static bool IsTransientReadModelFailure(Exception exception, CancellationToken ct) =>
        exception switch
        {
            OperationCanceledException when ct.IsCancellationRequested => false,
            WorkflowBoardSnapshotRequestException => false,
            WorkflowBoardReadModelUnavailableException => false,
            TimeoutException => true,
            TaskCanceledException => true,
            HttpRequestException => true,
            IOException => true,
            ProjectionIndexSchemaDriftException => true,
            InvalidOperationException ex when IsKnownStorageFailureMessage(ex.Message) => true,
            _ => false,
        };

    private static bool IsKnownStorageFailureMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Elasticsearch", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("projection index", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("read-model", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("read model", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SystemWorkflowBoardClock : IWorkflowBoardClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

public sealed class StudioWorkflowBoardRosterQueryPort : IWorkflowBoardRosterQueryPort
{
    private readonly IStudioTeamQueryPort _teamQueryPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;

    public StudioWorkflowBoardRosterQueryPort(
        IStudioTeamQueryPort teamQueryPort,
        IStudioMemberQueryPort memberQueryPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public async Task<IReadOnlyList<WorkflowBoardRosterTeam>> ListTeamsAsync(
        string scopeId,
        CancellationToken ct = default)
    {
        var roster = await _teamQueryPort.ListAsync(
            scopeId,
            new StudioTeamRosterPageRequest(PageSize: WorkflowBoardSnapshotRequestLimits.MaxMemberRows),
            ct);
        return roster.Teams.Select(MapTeam).ToArray();
    }

    public async Task<IReadOnlyList<WorkflowBoardRosterMember>> ListMembersAsync(
        string scopeId,
        string? teamId,
        int take,
        CancellationToken ct = default)
    {
        var roster = await _memberQueryPort.ListAsync(
            scopeId,
            new StudioMemberRosterPageRequest(PageSize: take, TeamId: teamId),
            ct);
        return roster.Members.Select(MapMember).ToArray();
    }

    public async Task<WorkflowBoardRosterTeam?> GetTeamAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var team = await _teamQueryPort.GetAsync(scopeId, teamId, ct);
        return team == null ? null : MapTeam(team);
    }

    public async Task<WorkflowBoardRosterMember?> GetMemberAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var member = await _memberQueryPort.GetAsync(scopeId, memberId, ct);
        return member == null ? null : MapMember(member.Summary, member.ImplementationRef ?? member.Summary.ImplementationRef, member.LastBinding);
    }

    private static WorkflowBoardRosterTeam MapTeam(StudioTeamSummaryResponse team) =>
        new(
            team.TeamId,
            team.ScopeId,
            team.DisplayName,
            string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Archived, StringComparison.Ordinal),
            team.MemberCount,
            team.UpdatedAt);

    private static WorkflowBoardRosterMember MapMember(StudioMemberSummaryResponse summary) =>
        MapMember(summary, summary.ImplementationRef, lastBinding: null);

    private static WorkflowBoardRosterMember MapMember(
        StudioMemberSummaryResponse summary,
        StudioMemberImplementationRefResponse? implementationRef,
        StudioMemberBindingContractResponse? lastBinding) =>
        new(
            summary.MemberId,
            summary.ScopeId,
            summary.TeamId,
            summary.DisplayName,
            false,
            summary.PublishedServiceId,
            implementationRef?.WorkflowId,
            null,
            lastBinding?.ExpectedActorId,
            summary.Description,
            summary.UpdatedAt);
}
