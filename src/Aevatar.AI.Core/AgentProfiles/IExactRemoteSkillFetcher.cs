using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Core.AgentProfiles;

public enum ExactRemoteSkillFetchFailureCode
{
    InvalidReference = 0,
    AccessTokenMissing = 1,
    NotFound = 2,
    AccessDenied = 3,
    Timeout = 4,
    InvalidResponse = 5,
    IdentityMismatch = 6,
    IntegrityEvidenceMissing = 7,
    Failed = 8,
}

public sealed record ExactRemoteSkillFetchResult(
    bool IsSuccess,
    string? Guid,
    string? LiteralVersion,
    string? Name,
    string? PublisherId,
    string? SkillHash,
    string? SkillMarkdown,
    ExactRemoteSkillFetchFailureCode? FailureCode,
    string? FailureDetail)
{
    public static ExactRemoteSkillFetchResult Success(
        string guid,
        string literalVersion,
        string name,
        string publisherId,
        string skillHash,
        string skillMarkdown) =>
        new(true, guid, literalVersion, name, publisherId, skillHash, skillMarkdown, null, null);

    public static ExactRemoteSkillFetchResult Failed(
        ExactRemoteSkillFetchFailureCode failureCode,
        string? failureDetail = null) =>
        new(false, null, null, null, null, null, null, failureCode, failureDetail);
}

public interface IExactRemoteSkillFetcher
{
    Task<ExactRemoteSkillFetchResult> FetchAsync(
        string accessToken,
        ExactRemoteSkillRef skillRef,
        CancellationToken ct = default);
}
