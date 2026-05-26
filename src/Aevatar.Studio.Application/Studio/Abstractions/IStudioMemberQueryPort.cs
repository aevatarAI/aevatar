using Aevatar.Studio.Application.Studio.Contracts;

namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Pure-read query port for StudioMember. Reads exclusively from the
/// projection document store; does not replay events or read actor state
/// directly.
/// </summary>
public interface IStudioMemberQueryPort
{
    // Refactor (iter74/cluster-074-studio-team-members-query-fanout):
    //   Old pattern: Host loops scope roster pages + Host-side TeamId filter
    //   New principle: ReadModel query port owns scope_id+team_id filter before pagination
    Task<StudioMemberRosterResponse> ListAsync(
        string scopeId,
        StudioMemberRosterPageRequest? page = null,
        CancellationToken ct = default);

    Task<StudioMemberDetailResponse?> GetAsync(
        string scopeId,
        string memberId,
        CancellationToken ct = default);
}
