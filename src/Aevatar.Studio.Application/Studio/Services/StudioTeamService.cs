using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Services;

/// <summary>
/// Application-layer facade for the StudioTeam authority (ADR-0017). Performs
/// input validation at this boundary and delegates command / query work to
/// the injected ports. The Hosting layer depends only on this facade.
/// </summary>
public sealed class StudioTeamService : IStudioTeamService
{
    private readonly IStudioTeamCommandPort _commandPort;
    private readonly IStudioTeamQueryPort _queryPort;
    private readonly IStudioMemberQueryPort _memberQueryPort;

    public StudioTeamService(
        IStudioTeamCommandPort commandPort,
        IStudioTeamQueryPort queryPort,
        IStudioMemberQueryPort memberQueryPort)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _memberQueryPort = memberQueryPort ?? throw new ArgumentNullException(nameof(memberQueryPort));
    }

    public Task<StudioTeamSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioTeamRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validation lives at this Application boundary (CLAUDE.md
        // `严格分层 / 上层依赖抽象`). The Projection-layer command port is
        // an interchangeable transport; if it ever swaps, the bounds must
        // not silently disappear with it.
        StudioTeamCreateRequestValidator.Validate(request);

        return _commandPort.CreateAsync(scopeId, request, ct);
    }

    public Task<StudioTeamRosterResponse> ListAsync(
        string scopeId,
        StudioTeamRosterPageRequest? page = null,
        CancellationToken ct = default)
    {
        return _queryPort.ListAsync(scopeId, page, ct);
    }

    public async Task<StudioTeamSummaryResponse> GetAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var summary = await _queryPort.GetAsync(scopeId, teamId, ct);
        if (summary == null)
            throw new StudioTeamNotFoundException(scopeId, teamId);
        return summary;
    }

    public async Task<StudioTeamCommandResponse> UpdateAsync(
        string scopeId,
        string teamId,
        UpdateStudioTeamRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validate display_name when present (no empty-string allowed; absence
        // means "no change" per ADR-0017 §Q6).
        if (request.DisplayName.HasValue)
        {
            var dn = request.DisplayName.Value?.Trim();
            if (string.IsNullOrEmpty(dn))
                throw new InvalidOperationException(
                    "displayName must not be empty when present " +
                    "(absence in the patch body means 'no change').");
            if (dn.Length > StudioTeamInputLimits.MaxDisplayNameLength)
                throw new InvalidOperationException(
                    $"displayName must be at most {StudioTeamInputLimits.MaxDisplayNameLength} characters.");
        }

        // description allows present-and-empty (explicit clear) and
        // present-and-non-empty; only check the upper bound.
        if (request.Description.HasValue)
        {
            var desc = request.Description.Value;
            if (desc != null && desc.Length > StudioTeamInputLimits.MaxDescriptionLength)
                throw new InvalidOperationException(
                    $"description must be at most {StudioTeamInputLimits.MaxDescriptionLength} characters.");
        }

        // Refactor (iter96/cluster-547):
        //   Old: dispatch then GetAsync readmodel returned 200 OK + snapshot (pretending completion).
        //   New: no readmodel read; 202 Accepted + Location points to stable team query resource,
        //        body only carries accepted/no_change receipt.
        return await _commandPort.UpdateAsync(scopeId, teamId, request, ct);
    }

    public Task<StudioTeamCommandResponse> ArchiveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        // Refactor (iter96/cluster-547):
        //   Old: dispatch then GetAsync readmodel returned 200 OK + snapshot (pretending completion).
        //   New: no readmodel read; 202 Accepted + Location points to stable team query resource,
        //        body only carries accepted/no_change receipt.
        return _commandPort.ArchiveAsync(scopeId, teamId, ct);
    }

    public async Task SetEntryMemberAsync(
        string scopeId,
        string teamId,
        SetStudioTeamEntryMemberRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedMemberId = NormalizeRequired(request.MemberId, nameof(request.MemberId));
        var team = await GetAsync(scopeId, teamId, ct);
        EnsureTeamWritable(team);

        var member = await _memberQueryPort.GetAsync(scopeId, normalizedMemberId, ct)
            ?? throw new StudioMemberNotFoundException(scopeId, normalizedMemberId);

        if (!string.Equals(member.Summary.TeamId, team.TeamId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"member '{normalizedMemberId}' does not belong to team '{team.TeamId}'.");
        }

        await _commandPort.SetEntryMemberAsync(scopeId, team.TeamId, normalizedMemberId, ct);
    }

    public async Task ClearEntryMemberAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        var team = await GetAsync(scopeId, teamId, ct);
        EnsureTeamWritable(team);

        await _commandPort.ClearEntryMemberAsync(scopeId, team.TeamId, ct);
    }

    private static void EnsureTeamWritable(StudioTeamSummaryResponse team)
    {
        if (string.Equals(team.LifecycleStage, TeamLifecycleStageNames.Archived, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"team '{team.TeamId}' is archived; entry member updates are not allowed.");
        }
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException($"{fieldName} is required.");
        return normalized;
    }
}
