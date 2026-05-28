using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Write-side port for the StudioTeam authority (ADR-0017). Dispatches
/// commands to the per-team <c>StudioTeamGAgent</c> actor. Never reads
/// downstream state — queries flow through <see cref="IStudioTeamQueryPort"/>.
/// </summary>
public interface IStudioTeamCommandPort
{
    Task<StudioTeamSummaryResponse> CreateAsync(
        string scopeId,
        CreateStudioTeamRequest request,
        CancellationToken ct = default);

    Task<StudioTeamCommandResponse> UpdateAsync(
        string scopeId,
        string teamId,
        UpdateStudioTeamRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Archives the team. Archive is irreversible (ADR-0017 §Locked Rule 5).
    /// Idempotent on already-archived teams.
    /// </summary>
    Task<StudioTeamCommandResponse> ArchiveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);

    Task SetEntryMemberAsync(
        string scopeId,
        string teamId,
        string memberId,
        CancellationToken ct = default);

    Task ClearEntryMemberAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);
}
