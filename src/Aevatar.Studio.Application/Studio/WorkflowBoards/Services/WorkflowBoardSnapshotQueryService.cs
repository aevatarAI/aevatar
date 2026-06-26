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
        var normalizedSelections = NormalizeAndValidate(request);
        var teams = new List<WorkflowBoardTeamSnapshot>();
        var invalidSelections = new List<WorkflowBoardInvalidSelection>();
        var watermarkFacts = new List<string>();

        foreach (var selection in normalizedSelections)
        {
            var team = await ReadTeamAsync(request.ScopeId, selection.TeamId, ct);
            if (team == null)
            {
                foreach (var memberId in selection.MemberIds)
                {
                    invalidSelections.Add(new WorkflowBoardInvalidSelection(
                        selection.TeamId,
                        memberId,
                        WorkflowBoardInvalidSelectionReason.TeamNotFound,
                        "Selected team is no longer available."));
                }

                watermarkFacts.Add($"team:{selection.TeamId}:missing");
                continue;
            }

            if (team.IsArchived)
            {
                foreach (var memberId in selection.MemberIds)
                {
                    invalidSelections.Add(new WorkflowBoardInvalidSelection(
                        selection.TeamId,
                        memberId,
                        WorkflowBoardInvalidSelectionReason.Archived,
                        "Selected team is archived."));
                }

                watermarkFacts.Add($"team:{team.TeamId}:archived:{FormatTimestamp(team.UpdatedAt)}");
                continue;
            }

            watermarkFacts.Add(
                $"team:{team.TeamId}:{team.DisplayName}:{team.TotalMemberCount?.ToString() ?? "null"}:{FormatTimestamp(team.UpdatedAt)}");

            var members = new List<WorkflowBoardMemberSnapshot>();
            foreach (var memberId in selection.MemberIds)
            {
                var member = await ReadMemberAsync(request.ScopeId, memberId, ct);
                if (member == null)
                {
                    invalidSelections.Add(new WorkflowBoardInvalidSelection(
                        selection.TeamId,
                        memberId,
                        WorkflowBoardInvalidSelectionReason.MemberNotFound,
                        "Selected member is no longer available."));
                    watermarkFacts.Add($"member:{memberId}:missing");
                    continue;
                }

                if (member.IsArchived)
                {
                    invalidSelections.Add(new WorkflowBoardInvalidSelection(
                        selection.TeamId,
                        memberId,
                        WorkflowBoardInvalidSelectionReason.Archived,
                        "Selected member is archived."));
                    watermarkFacts.Add($"member:{member.MemberId}:archived:{FormatTimestamp(member.UpdatedAt)}");
                    continue;
                }

                if (!string.Equals(member.TeamId, selection.TeamId, StringComparison.Ordinal))
                {
                    invalidSelections.Add(new WorkflowBoardInvalidSelection(
                        selection.TeamId,
                        member.MemberId,
                        WorkflowBoardInvalidSelectionReason.MemberNotInTeam,
                        "Selected member does not belong to the selected team."));
                    watermarkFacts.Add(
                        $"member:{member.MemberId}:team:{member.TeamId ?? "null"}:{FormatTimestamp(member.UpdatedAt)}");
                    continue;
                }

                var execution = await GetExecutionSnapshotAsync(
                    request.ScopeId,
                    selection.TeamId,
                    member,
                    ct);
                members.Add(MapMember(member, execution));
                watermarkFacts.Add(BuildMemberWatermarkFact(member, execution));
            }

            if (members.Count > 0)
            {
                teams.Add(new WorkflowBoardTeamSnapshot(
                    team.TeamId,
                    team.DisplayName,
                    team.TotalMemberCount,
                    members.Count,
                    members));
            }
        }

        return new WorkflowBoardSnapshot(
            request.ScopeId,
            _clock.GetUtcNow(),
            BuildWatermark(request.ScopeId, normalizedSelections, watermarkFacts),
            CalculateTotals(teams),
            teams,
            invalidSelections,
            LatestNodeUpdate(teams));
    }

    private static IReadOnlyList<WorkflowBoardTeamSelection> NormalizeAndValidate(
        WorkflowBoardSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ScopeId))
            throw new WorkflowBoardSnapshotRequestException("scopeId is required.");

        if (request.TeamSelections == null || request.TeamSelections.Count == 0)
            throw new WorkflowBoardSnapshotRequestException("teamSelections is required.");

        if (request.TeamSelections.Count > WorkflowBoardSnapshotRequestLimits.MaxSelectedTeams)
            throw new WorkflowBoardSnapshotRequestException(
                $"At most {WorkflowBoardSnapshotRequestLimits.MaxSelectedTeams} teams can be selected.");

        if (request.PreviousWatermark != null)
        {
            if (string.IsNullOrWhiteSpace(request.PreviousWatermark))
                throw new WorkflowBoardSnapshotRequestException("previousWatermark must not be blank when present.");

            if (request.PreviousWatermark.Length > WorkflowBoardSnapshotRequestLimits.MaxPreviousWatermarkLength)
                throw new WorkflowBoardSnapshotRequestException(
                    $"previousWatermark must be at most {WorkflowBoardSnapshotRequestLimits.MaxPreviousWatermarkLength} characters.");
        }

        var selections = new List<WorkflowBoardTeamSelection>();
        var seenRows = new HashSet<string>(StringComparer.Ordinal);
        var selectedMemberCount = 0;

        foreach (var selection in request.TeamSelections)
        {
            if (string.IsNullOrWhiteSpace(selection.TeamId))
                throw new WorkflowBoardSnapshotRequestException("teamId is required.");

            if (selection.MemberIds == null || selection.MemberIds.Count == 0)
                throw new WorkflowBoardSnapshotRequestException("memberIds is required.");

            var teamId = selection.TeamId.Trim();
            var memberIds = new List<string>();
            foreach (var rawMemberId in selection.MemberIds)
            {
                if (string.IsNullOrWhiteSpace(rawMemberId))
                    throw new WorkflowBoardSnapshotRequestException("memberIds must not contain blank values.");

                var memberId = rawMemberId.Trim();
                if (!seenRows.Add($"{teamId}\u001f{memberId}"))
                    continue;

                memberIds.Add(memberId);
                selectedMemberCount++;
            }

            if (memberIds.Count == 0)
                continue;

            selections.Add(new WorkflowBoardTeamSelection(teamId, memberIds));
        }

        if (selectedMemberCount == 0)
            throw new WorkflowBoardSnapshotRequestException("memberIds is required.");

        if (selectedMemberCount > WorkflowBoardSnapshotRequestLimits.MaxSelectedMembers)
            throw new WorkflowBoardSnapshotRequestException(
                $"At most {WorkflowBoardSnapshotRequestLimits.MaxSelectedMembers} members can be selected.");

        return selections;
    }

    private async Task<WorkflowBoardExecutionSnapshot?> GetExecutionSnapshotAsync(
        string scopeId,
        string teamId,
        WorkflowBoardRosterMember member,
        CancellationToken ct)
    {
        if (_executionQueryPort == null)
            return null;

        var lookup = new WorkflowBoardExecutionLookup(
            scopeId,
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
            CurrentNode = execution?.CurrentNode,
            LastNodeUpdatedAt = execution?.LastNodeUpdatedAt,
            Totals = execution?.Totals,
        };
    }

    private static WorkflowBoardTotals CalculateTotals(IReadOnlyList<WorkflowBoardTeamSnapshot> teams)
    {
        var allMembers = teams.SelectMany(static team => team.Members).ToArray();
        if (allMembers.Length == 0 || allMembers.Any(static member => !HasAuthoritativeExecutionTotals(member)))
        {
            return new WorkflowBoardTotals(null, null, null, null);
        }

        return new WorkflowBoardTotals(
            allMembers.Sum(static member => member.Totals!.CompletedSteps!.Value),
            allMembers.Sum(static member => member.Totals!.RunningNodes!.Value),
            allMembers.Sum(static member => member.Totals!.WaitingOrPendingNodes!.Value),
            allMembers.Sum(static member => member.Totals!.FailedNodes!.Value));
    }

    private static bool HasAuthoritativeExecutionTotals(WorkflowBoardMemberSnapshot member) =>
        member.ExecutionAvailability == WorkflowBoardExecutionAvailability.Available &&
        member.Totals is
        {
            CompletedSteps: not null,
            RunningNodes: not null,
            WaitingOrPendingNodes: not null,
            FailedNodes: not null,
        };

    private static DateTimeOffset? LatestNodeUpdate(IReadOnlyList<WorkflowBoardTeamSnapshot> teams) =>
        teams.SelectMany(static team => team.Members)
            .Select(static member => member.LastNodeUpdatedAt)
            .Where(static value => value.HasValue)
            .Max();

    private static string BuildMemberWatermarkFact(
        WorkflowBoardRosterMember member,
        WorkflowBoardExecutionSnapshot? execution) =>
        string.Join(
            ':',
            "member",
            member.MemberId,
            member.TeamId ?? "null",
            member.DisplayName,
            member.PublishedServiceId ?? "null",
            member.WorkflowId ?? "null",
            member.WorkflowName ?? "null",
            member.ActorId ?? "null",
            member.RoleSummary ?? "null",
            FormatTimestamp(member.UpdatedAt),
            execution?.Revision ?? "execution-pending");

    private static string BuildWatermark(
        string scopeId,
        IReadOnlyList<WorkflowBoardTeamSelection> selections,
        IReadOnlyList<string> facts)
    {
        var selectionMaterial = string.Join(
            '\n',
            selections.Select(static selection =>
                $"{selection.TeamId}:{string.Join(',', selection.MemberIds)}"));
        var factMaterial = string.Join('\n', facts);
        var selectionHash = Hash($"{scopeId}\n{selectionMaterial}");
        var factHash = Hash(factMaterial);
        return $"workflow-board:v1:{selectionHash}:{factHash}";
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

    public async Task<WorkflowBoardRosterTeam?> GetTeamAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var team = await _teamQueryPort.GetAsync(scopeId, teamId, ct);
        if (team == null)
            return null;

        return new WorkflowBoardRosterTeam(
            team.TeamId,
            team.ScopeId,
            team.DisplayName,
            string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Archived, StringComparison.Ordinal),
            team.MemberCount,
            team.UpdatedAt);
    }

    public async Task<WorkflowBoardRosterMember?> GetMemberAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default)
    {
        var member = await _memberQueryPort.GetAsync(scopeId, memberId, ct);
        if (member == null)
            return null;

        var summary = member.Summary;
        var implementationRef = member.ImplementationRef ?? summary.ImplementationRef;
        return new WorkflowBoardRosterMember(
            summary.MemberId,
            summary.ScopeId,
            summary.TeamId,
            summary.DisplayName,
            false,
            summary.PublishedServiceId,
            implementationRef?.WorkflowId,
            null,
            member.LastBinding?.ExpectedActorId,
            summary.Description,
            summary.UpdatedAt);
    }
}
