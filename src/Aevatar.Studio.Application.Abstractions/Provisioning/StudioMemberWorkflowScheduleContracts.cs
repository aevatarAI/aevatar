namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberWorkflowScheduleRequest(
    string ScopeId,
    string MemberId,
    string ScheduleCron,
    string ScheduleTimezone,
    string CallerSubjectExternalUserId)
{
    public string? Prompt { get; init; }

    public string? DisplayName { get; init; }

    public string CallerSubjectPlatform { get; init; } = "nyxid";

    public string? CallerSubjectTenant { get; init; }
}

public sealed record StudioMemberWorkflowScheduleResult(
    bool Success,
    string ScopeId,
    string MemberId,
    string ScheduleId,
    string PublishedServiceId,
    string ObservatoryUrl,
    string Status);
