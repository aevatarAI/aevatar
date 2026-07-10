namespace Aevatar.GAgents.Scheduled;

public interface IScheduledAgentApiKeyIssuer
{
    Task<ScheduledAgentApiKeyIssueResult> IssueAsync(
        string token,
        ScheduledAgentServiceSlugs serviceSlugs,
        string agentId,
        string skillName,
        string? scopeId,
        CancellationToken ct);

    Task TryRevokeAsync(string token, string apiKeyId, CancellationToken ct);
}

public sealed record ScheduledAgentServiceSlugs(
    string PrimaryOutboundSlug,
    string? FailureNotificationSlug,
    IReadOnlyList<string> RequiredServiceSlugs,
    bool RequiresOrnnService = true);
