namespace Aevatar.Mainnet.Host.Api.Skills;

// 06-26 ornn skills invocation page — read-only catalog DTOs (Host -> browser JSON).
public sealed record UserSkillSummary(
    string Guid,
    string Name,
    string Description,
    string Category,
    IReadOnlyList<string> Tags,
    bool Private);

public sealed record UserSkillListResult(
    IReadOnlyList<UserSkillSummary> Items,
    int Total,
    int Page,
    int PageSize,
    string? Error);

// Skill detail resolved on click — `runKind` is authoritative here (the list cannot know it without fetching
// each skill). `inputs` stays empty for ornn skills (they expose only free-text `arguments`, not a structured
// schema); the page falls back to a single Prompt + optional key=value field.
public sealed record UserSkillDetail(
    string Guid,
    string Name,
    string Description,
    string RunKind,
    string WhenToUse,
    string Arguments,
    IReadOnlyList<UserSkillInputField> Inputs);

public sealed record UserSkillInputField(string Name, string Label, bool Required, string Description);

public sealed record UserExactSkillDetail(
    string Guid,
    string Name,
    string LiteralVersion,
    string Publisher,
    string SkillHash,
    IReadOnlyList<string> DeclaredToolNames);

public sealed record UserExactSkillReadResult(
    UserExactSkillDetail? Detail,
    string? Error,
    int? UpstreamStatus = null);

public sealed record AgentProfileExactSkillError(string Code, string Message);

// Lists the caller's invocable ornn skills. The access token is an INPUT parameter (never read from
// HttpContext in the service), mirroring the observatory query-port seam; it scopes visibility to the
// caller's NyxID identity (Ornn "mixed" scope = the caller's full accessible set).
internal interface IUserSkillCatalogQueryService
{
    Task<UserSkillListResult> ListVisibleSkillsAsync(
        string accessToken,
        string query,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task<UserSkillDetail?> GetSkillAsync(
        string accessToken,
        string guid,
        CancellationToken ct = default);

    Task<UserExactSkillReadResult> GetExactSkillAsync(
        string accessToken,
        string guid,
        string? literalVersion,
        CancellationToken ct = default);
}
