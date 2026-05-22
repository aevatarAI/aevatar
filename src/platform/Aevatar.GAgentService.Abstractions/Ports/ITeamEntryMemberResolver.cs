namespace Aevatar.GAgentService.Abstractions.Ports;

public interface ITeamEntryMemberResolver
{
    Task<TeamEntryMemberResolution> ResolveAsync(
        string scopeId,
        string teamId,
        CancellationToken ct = default);
}

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
