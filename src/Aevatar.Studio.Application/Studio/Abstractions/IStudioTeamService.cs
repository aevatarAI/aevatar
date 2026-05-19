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

    Task<StudioTeamCommandAcceptedResponse> UpdateAsync(
        string scopeId,
        string teamId,
        UpdateStudioTeamRequest request,
        CancellationToken ct = default);

    Task<StudioTeamCommandAcceptedResponse> ArchiveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a team readmodel lookup targets an id that has no materialized
/// document. Update/archive paths must only surface not-found from an
/// authoritative command contract, not from post-dispatch readmodel lag.
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
