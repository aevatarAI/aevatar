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

    public StudioTeamService(
        IStudioTeamCommandPort commandPort,
        IStudioTeamQueryPort queryPort)
    {
        _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
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

    public async Task<StudioTeamSummaryResponse> UpdateAsync(
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

        await _commandPort.UpdateAsync(scopeId, teamId, request, ct);
        return await GetAsync(scopeId, teamId, ct);
    }

    public async Task<StudioTeamSummaryResponse> ArchiveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default)
    {
        await _commandPort.ArchiveAsync(scopeId, teamId, ct);
        return await GetAsync(scopeId, teamId, ct);
    }
}
