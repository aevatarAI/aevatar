using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Application-layer facade for StudioTeam (ADR-0017). Validates input and
/// delegates command / query work to the underlying ports. The hosting layer
/// only depends on this facade so a port swap (e.g. swapping the actor
/// dispatch transport) does not require endpoint changes.
/// </summary>
public interface IStudioTeamService
{
    Task<StudioTeamSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioTeamRequest request,
        CancellationToken ct = default);

    Task<StudioTeamRosterResponse> ListAsync(
        string scopeId,
        StudioTeamRosterPageRequest? page = null,
        CancellationToken ct = default);

    Task<StudioTeamSummaryResponse> GetAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);

    Task<StudioTeamSummaryResponse> UpdateAsync(
        string scopeId,
        string teamId,
        UpdateStudioTeamRequest request,
        CancellationToken ct = default);

    Task<StudioTeamSummaryResponse> ArchiveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a team lookup or update targets an id that has no read-model
/// document. Mirrors <see cref="StudioMemberNotFoundException"/>.
/// </summary>
public sealed class StudioTeamNotFoundException : Exception
{
    public StudioTeamNotFoundException(string scopeId, string teamId)
        : base($"team '{teamId}' not found in scope '{scopeId}'.")
    {
        ScopeId = scopeId;
        TeamId = teamId;
    }

    public string ScopeId { get; }
    public string TeamId { get; }
}
