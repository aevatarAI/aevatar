using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions;

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
        long repairRequestedAtUnixMs,
        CancellationToken ct = default);
}

public sealed record UserAgentCatalogCredentialRepairResult(
    string RequestId,
    DispatchAdmission Admission,
    UserAgentCatalogCredentialRepairOutcome Outcome);
