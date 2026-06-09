using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class StudioTeamEntryMemberResolver : ITeamEntryMemberResolver
{
    private readonly IStudioTeamQueryPort _teamQueryPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;
    private readonly IScopeBindingReadinessQueryPort _readinessQueryPort;

    public StudioTeamEntryMemberResolver(
        IStudioTeamQueryPort teamQueryPort,
        IStudioMemberQueryPort memberQueryPort,
        IScopeBindingReadinessQueryPort readinessQueryPort)
    {
        _teamQueryPort = teamQueryPort ?? throw new ArgumentNullException(nameof(teamQueryPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
        _readinessQueryPort = readinessQueryPort ?? throw new ArgumentNullException(nameof(readinessQueryPort));
    }

    public async Task<TeamEntryMemberResolution> ResolveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var team = await _teamQueryPort.GetAsync(scopeId, teamId, ct);
        if (team == null)
        {
            throw Failure(
                TeamEntryMemberErrorCodes.TeamNotFound,
                scopeId,
                teamId,
                $"team '{teamId}' not found in scope '{scopeId}'.");
        }

        if (string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Archived, StringComparison.Ordinal))
        {
            throw Failure(
                TeamEntryMemberErrorCodes.TeamArchived,
                team.ScopeId,
                team.TeamId,
                $"team '{team.TeamId}' is archived.");
        }

        var entryMemberId = team.EntryMemberId?.Trim() ?? string.Empty;
        if (entryMemberId.Length == 0)
        {
            throw Failure(
                TeamEntryMemberErrorCodes.EntryMemberNotConfigured,
                team.ScopeId,
                team.TeamId,
                $"team '{team.TeamId}' has no entry member configured.");
        }

        var member = await _memberQueryPort.GetAsync(team.ScopeId, entryMemberId, ct);
        if (member == null)
        {
            throw Failure(
                TeamEntryMemberErrorCodes.EntryMemberNotFound,
                team.ScopeId,
                team.TeamId,
                $"entry member '{entryMemberId}' not found in scope '{team.ScopeId}'.");
        }

        if (!string.Equals(member.Summary.TeamId, team.TeamId, StringComparison.Ordinal))
        {
            throw Failure(
                TeamEntryMemberErrorCodes.EntryMemberMismatch,
                team.ScopeId,
                team.TeamId,
                $"entry member '{entryMemberId}' does not belong to team '{team.TeamId}'.");
        }

        if (!string.Equals(member.Summary.LifecycleStage, MemberLifecycleStageNames.BindReady, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(member.Summary.PublishedServiceId))
        {
            throw Failure(
                TeamEntryMemberErrorCodes.EntryMemberNotReady,
                team.ScopeId,
                team.TeamId,
                $"entry member '{entryMemberId}' is not bind-ready.");
        }

        var readiness = await _readinessQueryPort.GetReadinessAsync(
            new ScopeBindingReadinessRequest(
                ScopeId: team.ScopeId,
                ServiceId: member.Summary.PublishedServiceId,
                ExpectedRevisionId: NormalizeOptional(member.Summary.LastBoundRevisionId)),
            ct);
        if (readiness.Status != ScopeBindingReadinessStatus.Ready || !readiness.InvokeReady)
        {
            throw Failure(
                TeamEntryMemberErrorCodes.EntryMemberNotReady,
                team.ScopeId,
                team.TeamId,
                $"entry member '{entryMemberId}' is not invocation-ready: {MapReadinessReason(readiness.Status)}.");
        }

        return new TeamEntryMemberResolution(
            ScopeId: team.ScopeId,
            TeamId: team.TeamId,
            EntryMemberId: entryMemberId,
            PublishedServiceId: member.Summary.PublishedServiceId);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }

    private static string MapReadinessReason(ScopeBindingReadinessStatus status) =>
        status switch
        {
            ScopeBindingReadinessStatus.PreparedArtifactMissing => "prepared_artifact_missing",
            ScopeBindingReadinessStatus.ServiceCatalogMissing => "service_catalog_missing",
            ScopeBindingReadinessStatus.ServingSetMissing => "serving_set_missing",
            ScopeBindingReadinessStatus.EligibleServingTargetMissing => "eligible_serving_target_missing",
            ScopeBindingReadinessStatus.ServiceCatalogTargetMissing => "service_catalog_target_missing",
            ScopeBindingReadinessStatus.TrafficViewTargetMissing => "traffic_view_target_missing",
            ScopeBindingReadinessStatus.Ready => "ready",
            _ => "unknown",
        };

    private static TeamEntryMemberResolutionException Failure(
        string code,
        string scopeId,
        string teamId,
        string message) =>
        new(code, scopeId, teamId, message);
}
