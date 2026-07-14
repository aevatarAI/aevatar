namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberWorkflowScheduleRequest(
    string ScopeId,
    string MemberId,
    string ScheduleCron,
    string ScheduleTimezone,
    Aevatar.Studio.Application.Authorization.AuthenticatedNyxIdOwnerContext AuthenticatedOwner,
    DateTimeOffset CredentialExpiresAtUtc)
{
    public string? Prompt { get; init; }

    public string? DisplayName { get; init; }

    public string? CallerSubjectPlatform { get; init; }

    public string? CallerSubjectTenant { get; init; }

    public string CallerSubjectExternalUserId => AuthenticatedOwner.SubjectExternalUserId;

}

public sealed record StudioMemberWorkflowAuthorizationResult(
    bool Success,
    Aevatar.Studio.Application.Authorization.ScheduledInvocationAuthorizationPlan? Plan,
    Aevatar.Studio.Application.Authorization.ScheduledInvocationAuthorizationFailureCode FailureCode,
    string Detail);

public sealed record StudioMemberWorkflowScheduleResult(
    bool Success,
    string ScopeId,
    string MemberId,
    string ScheduleId,
    string PublishedServiceId,
    string ObservatoryUrl,
    string Status);
