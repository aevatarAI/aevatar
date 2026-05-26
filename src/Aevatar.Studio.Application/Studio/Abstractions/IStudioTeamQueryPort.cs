using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Read-side port for StudioTeam (ADR-0017). Pure query semantics — never
/// replays events, never calls the actor runtime. Roster scans are
/// constrained to the requested <c>scope_id</c>.
/// </summary>
public interface IStudioTeamQueryPort
{
    Task<StudioTeamRosterResponse> ListAsync(
        string scopeId,
        StudioTeamRosterPageRequest? page = null,
        CancellationToken ct = default);

    Task<StudioTeamSummaryResponse?> GetAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);
}
