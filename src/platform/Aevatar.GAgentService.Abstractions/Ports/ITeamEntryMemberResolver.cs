namespace Aevatar.GAgentService.Abstractions.Ports;

// Refactor (iter-v1/issue1450-first):
//   Old: resolver contract could be misread as a composite team readiness/status read model.
//   New: resolver is only for InvokeTeam command target admission and exposes no cross-authority team status.
/// <summary>
/// Resolves the Studio team entry member for command target admission. This
/// contract is not a stable team status/readiness read model and must not be
/// reused by UI, reporting, or other query surfaces to display cross-actor
/// authoritative state.
/// </summary>
public interface ITeamEntryMemberResolver
{
    Task<TeamEntryMemberResolution> ResolveAsync(
        string scopeId,
        string teamId,
        string endpointId,
        CancellationToken ct = default);
}

/// <summary>
/// Narrow command-target resolution result. It carries only the identifiers
/// needed to dispatch to the selected entry member's published service; it is
/// not a composite team readiness/status view. Freshness markers may only be
/// added by passing through fields already exposed by the underlying team or
/// member read models, never by inventing a resolver-local version.
/// </summary>
public sealed record TeamEntryMemberResolution(
    string ScopeId,
    string TeamId,
    string EntryMemberId,
    string PublishedServiceId);

public static class TeamEntryMemberErrorCodes
{
    public const string TeamNotFound = "TEAM_NOT_FOUND";
    public const string TeamArchived = "TEAM_ARCHIVED";
    public const string EntryMemberNotConfigured = "TEAM_ENTRY_MEMBER_NOT_CONFIGURED";
    public const string EntryMemberNotFound = "TEAM_ENTRY_MEMBER_NOT_FOUND";
    public const string EntryMemberMismatch = "TEAM_ENTRY_MEMBER_MISMATCH";
    public const string EntryMemberNotReady = "TEAM_ENTRY_MEMBER_NOT_READY";
}

public sealed class TeamEntryMemberResolutionException : Exception
{
    public TeamEntryMemberResolutionException(
        string code,
        string scopeId,
        string teamId,
        string message)
        : base(message)
    {
        Code = code;
        ScopeId = scopeId;
        TeamId = teamId;
    }

    public string Code { get; }
    public string ScopeId { get; }
    public string TeamId { get; }
}
