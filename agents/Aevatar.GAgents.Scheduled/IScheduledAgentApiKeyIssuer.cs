namespace Aevatar.GAgents.Scheduled;

using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;

public interface IScheduledAgentApiKeyIssuer
{
    Task<ScheduledAgentApiKeyLookupResult> FindActiveKeysByNameAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        CancellationToken ct) =>
        Task.FromResult(ScheduledAgentApiKeyLookupResult.Pending(
            0,
            "api_key_name_lookup_not_supported",
            UserAgentApiKeyRevocationFailureKind.ProviderError));

    Task<ScheduledAgentApiKeyRevokeResult> RevokeActiveKeysByNameAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        CancellationToken ct) =>
        Task.FromResult(ScheduledAgentApiKeyRevokeResult.Pending(
            0,
            "api_key_name_reconciliation_not_supported",
            UserAgentApiKeyRevocationFailureKind.ProviderError));

    Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        CancellationToken ct);

    Task<ScheduledAgentApiKeyRevokeResult> RevokeAsync(string token, string apiKeyId, CancellationToken ct);

}

public sealed record ScheduledAgentApiKeyLookupResult(
    bool Completed,
    IReadOnlyList<string> ActiveApiKeyIds,
    int HttpStatus,
    string Error,
    UserAgentApiKeyRevocationFailureKind FailureKind)
{
    public static ScheduledAgentApiKeyLookupResult Complete(IReadOnlyList<string> activeApiKeyIds) =>
        new(true, activeApiKeyIds, 0, string.Empty, UserAgentApiKeyRevocationFailureKind.None);

    public static ScheduledAgentApiKeyLookupResult Pending(
        int httpStatus,
        string error,
        UserAgentApiKeyRevocationFailureKind failureKind) =>
        new(false, [], httpStatus, error ?? string.Empty, failureKind);
}

public sealed record ScheduledAgentServiceRequirements(
    string PrimaryOutboundSlug,
    string PrimaryOutboundUserServiceId,
    string? FailureNotificationSlug,
    IReadOnlyList<NyxIdUserServiceCapabilityRef> RequiredNyxServices,
    bool RequiresOrnnService = true);

public sealed record ScheduledAgentApiKeyRevokeResult(
    bool Completed,
    int HttpStatus,
    string Error,
    UserAgentApiKeyRevocationFailureKind FailureKind)
{
    public static ScheduledAgentApiKeyRevokeResult Complete(int httpStatus = 0) =>
        new(true, httpStatus, string.Empty, UserAgentApiKeyRevocationFailureKind.None);

    public static ScheduledAgentApiKeyRevokeResult Pending(
        int httpStatus,
        string error,
        UserAgentApiKeyRevocationFailureKind failureKind) =>
        new(false, httpStatus, error ?? string.Empty, failureKind);
}
