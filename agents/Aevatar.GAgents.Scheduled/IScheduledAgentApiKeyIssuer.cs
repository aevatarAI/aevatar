namespace Aevatar.GAgents.Scheduled;

using Aevatar.Studio.Application.Authorization;

public interface IScheduledAgentApiKeyIssuer
{
    Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ScheduledInvocationAuthorizationPlan plan,
        string credentialName,
        CancellationToken ct);

    Task<ScheduledAgentApiKeyRevokeResult> RevokeAsync(string token, string apiKeyId, CancellationToken ct);

}

public sealed record ScheduledAgentServiceRequirements(
    string PrimaryOutboundSlug,
    string? FailureNotificationSlug,
    IReadOnlyList<string> RequiredServiceSlugs,
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
