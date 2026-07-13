using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Scheduled;

public interface IUserAgentCatalogCredentialRepairPort
{
    Task<UserAgentCatalogCredentialRepairResult> RepairMissingSecretReferenceAsync(
        string agentId,
        string apiKeyId,
        SecretReference secretReference,
        string secretSubjectId,
        string repairReason,
        string requestedBySubjectId,
        long requestedAtUnixMs,
        CancellationToken ct = default);
}

public sealed record UserAgentCatalogCredentialRepairResult(
    bool Repaired,
    UserAgentCatalogCredentialRevocationRepairRejectionReason RejectionReason);
