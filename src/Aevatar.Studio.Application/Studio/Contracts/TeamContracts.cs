namespace Aevatar.Studio.Application.Studio.Contracts;

/// <summary>
/// Wire-format lifecycle stage for StudioTeam. Mirrors
/// <c>StudioTeamLifecycleStage</c> proto enum but with stable string values
/// the frontend can switch on without taking a generated proto dependency
/// (matches the StudioMember convention — see <see cref="MemberLifecycleStageNames"/>).
/// </summary>
public static class TeamLifecycleStageNames
{
    public const string Active = "active";
    public const string Archived = "archived";
}

public sealed record StudioTeamSummaryResponse(
    string TeamId,
    string ScopeId,
    string DisplayName,
    string Description,
    string LifecycleStage,
    int MemberCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StudioTeamRosterResponse(
    string ScopeId,
    IReadOnlyList<StudioTeamSummaryResponse> Teams,
    string? NextPageToken = null);

public sealed record StudioTeamRosterPageRequest(
    int? PageSize = null,
    string? PageToken = null);

public sealed record CreateStudioTeamRequest(
    string DisplayName,
    string? Description = null,
    string? TeamId = null);

/// <summary>
/// Merge-Patch shape for team updates (ADR-0017 §Q6). A field with
/// <see cref="HasValue"/> = false means "absent / no change". A field with
/// <see cref="HasValue"/> = true and a null <see cref="Value"/> means
/// "explicit clear" (only valid for nullable wire fields). A field with a
/// non-empty string value means "set".
/// </summary>
public readonly struct PatchValue<T>
{
    public bool HasValue { get; }
    public T? Value { get; }

    public PatchValue(T? value)
    {
        HasValue = true;
        Value = value;
    }

    public static PatchValue<T> Absent => default;

    public static PatchValue<T> Of(T? value) => new(value);
}

public sealed record UpdateStudioTeamRequest(
    PatchValue<string> DisplayName = default,
    PatchValue<string> Description = default);

/// <summary>
/// Centralized input bounds for team contract — mirrors
/// <see cref="StudioMemberInputLimits"/> in shape and intent. Slug regex on
/// <c>teamId</c> keeps caller-supplied ids URL-safe and free of separators
/// reserved by the actor-id convention.
/// </summary>
public static class StudioTeamInputLimits
{
    public const int MaxDisplayNameLength = 256;
    public const int MaxDescriptionLength = 2048;
    public const int MaxTeamIdLength = 64;

    public static readonly System.Text.RegularExpressions.Regex TeamIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9_\-]{0,63}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
}
