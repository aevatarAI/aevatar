using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public interface IUserAgentCatalogCredentialRepairPort
{
    Task<UserAgentCatalogCredentialRepairAcceptedReceipt> RepairMissingSecretReferenceAsync(
        string agentId,
        string apiKeyId,
        SecretReference secretReference,
        string secretSubjectId,
        string repairReason,
        string requestedBySubjectId,
        long requestedAtUnixMs,
        CancellationToken ct = default);
}

public sealed record UserAgentCatalogCredentialRepairAcceptedReceipt(
    string RequestId,
    DispatchAdmission Admission);
