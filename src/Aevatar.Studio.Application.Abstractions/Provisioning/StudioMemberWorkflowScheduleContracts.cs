using System.Text.Json.Serialization;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.Studio.Application.Provisioning;

public sealed record StudioMemberWorkflowScheduleRequest(
    string ScopeId,
    string MemberId,
    string ScheduleCron,
    string ScheduleTimezone,
    AuthenticatedAuthorizationOwnerContext AuthenticatedOwner)
{
    public string? TeamId { get; init; }

    public string? ScheduleId { get; init; }

    public string? OperationId { get; init; }

    public string? IdempotencyKey { get; init; }

    public string? CredentialProvisioningKind { get; init; }

    public string? ConfirmedPolicyVersion { get; init; }

    public string? Prompt { get; init; }

    public string? DisplayName { get; init; }

    public string? CallerSubjectPlatform { get; init; }

    public string? CallerSubjectTenant { get; init; }

    public string CallerSubjectExternalUserId => AuthenticatedOwner.SubjectExternalUserId;

    public string? ProvisioningBearerToken { get; init; }

    public bool Enabled { get; init; } = true;

    public ScheduledDispatchScheduleMode ScheduleMode { get; init; } = ScheduledDispatchScheduleMode.RecurringCron;

    public DateTimeOffset? OneShotFireAt { get; init; }

    [JsonIgnore]
    public StudioMemberWorkflowAcceptedBindingContext? AcceptedBinding { get; init; }
}

public sealed record StudioMemberWorkflowAcceptedBindingContext(
    string TeamId,
    string PublishedServiceId,
    string WorkflowId,
    string? WorkflowRevisionId)
{
    [JsonIgnore]
    public ScheduledInvocationWorkflowEvidence? WorkflowEvidence { get; init; }
}

public sealed class StudioMemberWorkflowSchedulePolicy
{
    public const int DefaultCredentialLifetimeDays = 90;

    public int CredentialLifetimeDays { get; init; } = DefaultCredentialLifetimeDays;

    public DateTimeOffset ResolveCredentialExpiresAtUtc(DateTimeOffset evaluatedAtUtc)
    {
        if (CredentialLifetimeDays <= 0)
            throw new InvalidOperationException("studio_schedule_credential_lifetime_invalid");

        var utcDate = new DateTimeOffset(evaluatedAtUtc.UtcDateTime.Date, TimeSpan.Zero);
        return utcDate.AddDays(CredentialLifetimeDays);
    }
}

public sealed record StudioMemberWorkflowAuthorizationResult(
    bool Success,
    ScheduledInvocationAuthorizationPlan? Plan,
    ScheduledInvocationAuthorizationFailureCode FailureCode,
    string Detail);

public sealed record StudioMemberWorkflowScheduleResult(
    bool Success,
    string ScopeId,
    string MemberId,
    string ScheduleId,
    string PublishedServiceId,
    string ObservatoryUrl,
    string Status)
{
    public string OperationId { get; init; } = string.Empty;

    public string CommandId { get; init; } = string.Empty;

    public bool NewOperationCommitted { get; init; }
}

public sealed record StudioMemberAutomationUpdateCommand(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId,
    string ScheduleCron,
    string ScheduleTimezone,
    bool Enabled,
    string OperationId,
    string IdempotencyKey,
    AuthenticatedAuthorizationOwnerContext AuthenticatedOwner)
{
    public string? Prompt { get; init; }

    public string? DisplayName { get; init; }

    public string? ProvisioningBearerToken { get; init; }
}

public sealed record StudioMemberAutomationActionCommand(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId,
    string OperationId,
    string IdempotencyKey)
{
    public string? Reason { get; init; }

    public string? ProvisioningBearerToken { get; init; }

    public AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner { get; init; }
}

public sealed record StudioMemberAutomationRetryRevocationCommand(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId)
{
    public string? ProvisioningBearerToken { get; init; }

    public AuthenticatedAuthorizationOwnerContext? AuthenticatedOwner { get; init; }
}

public sealed record StudioMemberAutomationView(
    string ScopeId,
    string TeamId,
    string MemberId,
    string ScheduleId,
    string PublishedServiceId,
    string DisplayName,
    string Prompt,
    string ScheduleCron,
    string ScheduleTimezone,
    bool Enabled,
    string AuthorizationStatus,
    DateTimeOffset? CredentialExpiresAtUtc,
    string LastAuthorizationErrorCode,
    string OperationId,
    long CredentialGeneration,
    bool RevocationPending,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFireAt,
    long StateVersion)
{
    public string CredentialSourceKind { get; init; } = "scheduled_invocation_agent_key";

    public DateTimeOffset UpdatedAt { get; init; }

    public string OwnerLLMRouteKind { get; init; } = "unspecified";

    public string OwnerLLMRoute { get; init; } = string.Empty;

    public string OwnerLLMUserServiceId { get; init; } = string.Empty;

    public string OwnerLLMServiceSlug { get; init; } = string.Empty;

    public string OwnerLLMModel { get; init; } = string.Empty;

    public string NyxIdRevocationStatus { get; init; } = string.Empty;

    public string VaultRevocationStatus { get; init; } = string.Empty;
}

public sealed record StudioMemberAutomationListResponse(
    IReadOnlyList<StudioMemberAutomationView> Items,
    string? NextCursor,
    long? TotalCount);

public sealed record StudioMemberAutomationMutationReceipt(
    bool Accepted,
    string Status,
    string ScheduleId,
    string OperationId,
    string CommandId);

public sealed class StudioMemberAutomationNotFoundException : Exception
{
    public StudioMemberAutomationNotFoundException()
        : base("The requested Team automation was not found.")
    {
    }
}

public sealed class StudioMemberAutomationPlanConflictException : Exception
{
    public StudioMemberAutomationPlanConflictException(
        string code,
        string message,
        ScheduledAuthorizationPlanMismatchReason authorizationPlanMismatchReason =
            ScheduledAuthorizationPlanMismatchReason.Unspecified)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "authorization_plan_changed" : code.Trim();
        AuthorizationPlanMismatchReason = authorizationPlanMismatchReason;
    }

    public string Code { get; }

    public ScheduledAuthorizationPlanMismatchReason AuthorizationPlanMismatchReason { get; }
}

public sealed class StudioMemberAutomationProjectionPendingException : Exception
{
    public StudioMemberAutomationProjectionPendingException(long requiredStateVersion)
        : base("The authorization catalog projection has not reached the committed state version.")
    {
        if (requiredStateVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredStateVersion));

        RequiredStateVersion = requiredStateVersion;
    }

    public long RequiredStateVersion { get; }
}

public sealed class StudioMemberAutomationCatalogRefreshSupersededException : Exception
{
    public StudioMemberAutomationCatalogRefreshSupersededException()
        : base("The authorization catalog refresh was superseded by a newer refresh.")
    {
    }
}

public sealed class StudioMemberAutomationCatalogRefreshUnavailableException : Exception
{
    public StudioMemberAutomationCatalogRefreshUnavailableException()
        : base("The authorization catalog could not be refreshed. Retry this request.")
    {
    }
}
