using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

public sealed class UserAgentApiKeyRevocationReadModelEntry
{
    public string AgentId { get; init; } = string.Empty;
    public string ApiKeyId { get; init; } = string.Empty;
    public OwnerScope? OwnerScope { get; init; }
    public SecretReference? NyxApiKeyReference { get; init; }
    public Timestamp? RequestedAt { get; init; }
    public int AttemptCount { get; init; }
    public Timestamp? LastAttemptAt { get; init; }
    public int LastHttpStatus { get; init; }
    public string LastError { get; init; } = string.Empty;
    public UserAgentApiKeyRevocationFailureKind FailureKind { get; init; }
    public ScheduledCredentialRevocationTrack? NyxIdTrack { get; init; }
    public ScheduledCredentialRevocationTrack? VaultTrack { get; init; }
    public ScheduledCredentialVaultRevocationDescriptor? VaultRevocationDescriptor { get; init; }
    public string SecretSubjectId { get; init; } = string.Empty;
    public string RepairReason { get; init; } = string.Empty;
    public string RequestedBySubjectId { get; init; } = string.Empty;
    public long RepairRequestedAtUnixMs { get; init; }
    public long CatalogAuthorityStateVersion { get; init; }
    public string CatalogLastEventId { get; init; } = string.Empty;
}
