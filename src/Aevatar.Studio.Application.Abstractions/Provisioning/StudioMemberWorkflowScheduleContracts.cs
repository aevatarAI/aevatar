namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberWorkflowScheduleRequest(
    string ScopeId,
    string MemberId,
    string ScheduleCron,
    string ScheduleTimezone,
    Aevatar.Studio.Application.Authorization.AuthenticatedNyxIdOwnerContext AuthenticatedOwner,
    DateTimeOffset CredentialExpiresAtUtc)
{
    public StudioMemberWorkflowScheduleRequest(
        string ScopeId,
        string MemberId,
        string ScheduleCron,
        string ScheduleTimezone,
        string CallerSubjectExternalUserId)
        : this(
            ScopeId,
            MemberId,
            ScheduleCron,
            ScheduleTimezone,
            new Aevatar.Studio.Application.Authorization.AuthenticatedNyxIdOwnerContext
            {
                Owner = new Aevatar.Studio.Application.Authorization.NyxIdCatalogOwnerIdentity
                {
                    Authority = "nyxid",
                    OwnerKind = Aevatar.Studio.Application.Authorization.NyxIdCatalogOwnerKind.Personal,
                    OwnerSubject = CallerSubjectExternalUserId,
                },
                SubjectPlatform = "nyxid",
                SubjectExternalUserId = CallerSubjectExternalUserId,
                VerifiedBindingId = "test-binding",
            },
            DateTimeOffset.Parse("2099-01-01T00:00:00Z"))
    {
    }

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
